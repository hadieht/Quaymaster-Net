using System.Text.Json.Nodes;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Errors;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Storage;

namespace Quaymaster.Application.Docker;

/// <summary>What <c>/system/df</c> accounts for right now: per-volume sizes and per-image (size, shared) plus the totals.</summary>
public sealed record DfMeasure(
    IReadOnlyDictionary<string, long> Volumes,
    IReadOnlyDictionary<string, (long Size, long? Shared)> Images,
    long LayersSize,
    long VolumesSize);

/// <summary>A fresh (uncached) df measurement; one type per request, never in parallel (RT-26).</summary>
public interface IDfMeasurer
{
    /// <summary>Null when the daemon cannot be asked.</summary>
    Task<DfMeasure?> MeasureAsync(CancellationToken ct);
}

public sealed class DfMeasurer(IDockerEngine engine) : IDfMeasurer
{
    public async Task<DfMeasure?> MeasureAsync(CancellationToken ct)
    {
        try
        {
            var images = await engine.DiskUsageAsync("image", ct);
            var volumes = await engine.DiskUsageAsync("volume", ct);
            var vol = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var v in volumes.Arr("Volumes").Objects())
            {
                if (v.Obj("UsageData") is { } u && (u.Num("Size") ?? -1) >= 0)
                {
                    vol[v.Str("Name") ?? string.Empty] = u.Num("Size") ?? 0;
                }
            }

            var img = new Dictionary<string, (long, long?)>(StringComparer.Ordinal);
            foreach (var i in images.Arr("Images").Objects())
            {
                var shared = i.Num("SharedSize");
                img[i.Str("Id") ?? string.Empty] = (i.Num("Size") ?? 0, shared is >= 0 ? shared : null);
            }

            return new DfMeasure(vol, img, images.Num("LayersSize") ?? 0, vol.Values.Sum());
        }
        catch (AppException)
        {
            return null;
        }
    }
}

public sealed record ReclaimRun<T>(T Value, long EstimatedBytes, long? MaxBytes = null);

public interface IReclaimMeasurer
{
    /// <summary>
    /// Takes a df + free-space snapshot around <paramref name="run"/> and builds the report. Failure to measure never fails the
    /// removal: the report then says it fell back to the estimate.
    /// </summary>
    Task<(T Value, ReclaimReport Reclaim)> MeasureAsync<T>(Func<DfMeasure?, Task<ReclaimRun<T>>> run, CancellationToken ct);
}

public sealed class ReclaimMeasurer(IDfMeasurer df, ISystemService system) : IReclaimMeasurer
{
    private async Task<(UsageSnapshot Snapshot, DfMeasure? Measure)> SnapAsync(CancellationToken ct)
    {
        DfMeasure? m = null;
        try
        {
            m = await df.MeasureAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // measuring is best effort
        }

        long? free = null;
        try
        {
            free = (await system.DockerRootSpaceAsync(ct))?.Free;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // best effort
        }

        return (new UsageSnapshot(m is null ? null : m.LayersSize + m.VolumesSize, free), m);
    }

    public async Task<(T Value, ReclaimReport Reclaim)> MeasureAsync<T>(Func<DfMeasure?, Task<ReclaimRun<T>>> run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var before = await SnapAsync(ct);
        var result = await run(before.Measure);
        var after = await SnapAsync(ct);
        return (result.Value, ReclaimMath.Compute(before.Snapshot, after.Snapshot, result.EstimatedBytes, result.MaxBytes));
    }
}

/// <summary>What Compose projects own (images, volumes, networks), computed from the resolved config of every discovered project.</summary>
public interface IComposeOwnershipProvider
{
    /// <summary>Image references (as written and as cache keys) named in the Compose files of all discovered projects.</summary>
    Task<IReadOnlySet<string>> ProjectImageRefsAsync(CancellationToken ct);

    Task<ComposeOwnership> OwnershipAsync(CancellationToken ct);
}

public sealed class ComposeOwnershipProvider(IComposeService compose) : IComposeOwnershipProvider
{
    public async Task<IReadOnlySet<string>> ProjectImageRefsAsync(CancellationToken ct)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);

        // ignored projects still own their images: "stop monitoring" must never make them look unused
        foreach (var p in await compose.DiscoverAllAsync(false, ct))
        {
            if (p.Level != "full")
            {
                continue;
            }

            try
            {
                var cfg = await compose.ResolvedConfigForSafetyAsync(p.Name, ct);
                foreach (var (_, svc) in cfg.Obj("services") ?? [])
                {
                    if (svc.Str("image") is { Length: > 0 } image)
                    {
                        refs.Add(image);
                        if (ImageReferences.Parse(image) is { } r)
                        {
                            refs.Add(ImageReferences.CacheKey(r));
                        }
                    }
                }
            }
            catch (AppException)
            {
                // a project that does not resolve protects nothing it cannot name; running containers still protect their images
            }
        }

        return refs;
    }

    public async Task<ComposeOwnership> OwnershipAsync(CancellationToken ct)
    {
        var projects = new HashSet<string>(StringComparer.Ordinal);
        var volumes = new Dictionary<string, string>(StringComparer.Ordinal);
        var networks = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var p in await compose.DiscoverAllAsync(false, ct))
            {
                projects.Add(p.Name);
                if (p.Level != "full")
                {
                    continue;
                }

                try
                {
                    var cfg = await compose.ResolvedConfigForSafetyAsync(p.Name, ct);
                    foreach (var (key, v) in cfg.Obj("volumes") ?? [])
                    {
                        volumes[v.Str("name") ?? $"{p.Name}_{key}"] = p.Name;
                    }

                    foreach (var (key, v) in cfg.Obj("networks") ?? [])
                    {
                        networks[v.Str("name") ?? $"{p.Name}_{key}"] = p.Name;
                    }
                }
                catch (AppException)
                {
                    // a project whose config does not resolve still exists: its labelled resources stay protected
                }
            }
        }
        catch (AppException)
        {
            return new ComposeOwnership(false, projects, volumes, networks);
        }

        return new ComposeOwnership(true, projects, volumes, networks);
    }
}

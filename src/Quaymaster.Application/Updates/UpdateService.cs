using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Registry;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Updates;

namespace Quaymaster.Application.Updates;

public interface IUpdateService
{
    /// <summary>Status of every image reference in use. <paramref name="fresh"/> bypasses the cache; concurrent calls share one run.</summary>
    Task<IReadOnlyList<ImageUpdateStatusDto>> StatusesAsync(bool fresh, CancellationToken ct);
}

/// <summary>
/// Update detection (spec 7.1). For each image reference in use by a MONITORED container (ignored targets are skipped): the
/// local <c>RepoDigests</c> of the RUNNING image are compared with the registry's current manifest digest for the tag (HEAD with
/// token auth). Remote answers are cached for QM_CHECK_TTL_MINUTES; the client throttles, jitters and backs off on 429. When the
/// registry cannot be reached the daemon's <c>distribution inspect</c> is the fallback.
/// </summary>
public sealed class UpdateService(
    IDockerEngine engine,
    IContainerService containers,
    IRegistryClient registry,
    IImageChecksStore checks,
    IStagedStore staged,
    IQuarantineService quarantine,
    QmConfig config,
    TimeProvider time) : IUpdateService
{
    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private Task<IReadOnlyList<ImageUpdateStatusDto>>? _inflight;
    private bool _inflightFresh;

    private sealed record InUse(string Ref, string ImageId, List<ImageUserDto> Users);

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    private async Task<List<InUse>> InUseAsync(CancellationToken ct)
    {
        var list = new List<InUse>();
        foreach (var c in await containers.ListRawAsync(true, ct))
        {
            var user = new ImageUserDto(c.Id, c.Name, c.Project, c.Service, c.ImageId);
            var hit = list.Find(x => x.Ref == c.Image && x.ImageId == c.ImageId);
            if (hit is null)
            {
                list.Add(new InUse(c.Image, c.ImageId, [user]));
            }
            else
            {
                hit.Users.Add(user);
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<ImageUpdateStatusDto>> StatusesAsync(bool fresh, CancellationToken ct)
    {
        // A fresh request never piggybacks on a cached run that started earlier (it could answer from before a registry change).
        while (true)
        {
            Task<IReadOnlyList<ImageUpdateStatusDto>>? run = null;
            Task<IReadOnlyList<ImageUpdateStatusDto>>? waitFor = null;
            lock (_gate)
            {
                if (_inflight is null)
                {
                    _inflightFresh = fresh;
                    run = _inflight = Task.Run(() => RunAsync(fresh), CancellationToken.None);
                }
                else if (fresh && !_inflightFresh)
                {
                    waitFor = _inflight;
                }
                else
                {
                    run = _inflight;
                }
            }

            if (waitFor is not null)
            {
                try
                {
                    await waitFor.WaitAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // the earlier run failed; start our own
                }

                continue;
            }

            return await run!.WaitAsync(ct);
        }
    }

    private async Task<IReadOnlyList<ImageUpdateStatusDto>> RunAsync(bool fresh)
    {
        try
        {
            var output = new List<ImageUpdateStatusDto>();
            foreach (var u in await InUseAsync(CancellationToken.None))
            {
                output.Add(await StatusForAsync(u, fresh, CancellationToken.None));
            }

            return output;
        }
        finally
        {
            lock (_gate)
            {
                _inflight = null;
            }
        }
    }

    private async Task<ImageUpdateStatusDto> StatusForAsync(InUse u, bool fresh, CancellationToken ct)
    {
        var image = ImageReferences.Parse(u.Ref);
        var imageIsId = IsImageId(u.Ref);
        IReadOnlyList<string> repoDigests = [];
        var inspected = await engine.InspectImageAsync(u.ImageId, ct);
        if (inspected is not null)
        {
            repoDigests = [.. inspected.Arr("RepoDigests")?.Select(d => d.Str()).OfType<string>() ?? []];
        }

        var remote = new RemoteLookup(null, "none", null, Now);
        IReadOnlyList<string> platformDigests = [];
        if (image is { Digest: null } && !imageIsId)
        {
            remote = await RemoteDigestAsync(image, fresh, ct);
            var local = ImageReferences.DigestsForRepo(repoDigests, image);
            if (remote.Digest is not null && local.Count > 0 && !local.Contains(remote.Digest))
            {
                platformDigests = await PlatformDigestsAsync(image, remote.Digest, ct);
            }
        }

        var stagedPresent = image is not null && remote.Digest is not null && await IsStagedAsync(image, remote.Digest, ct);
        var c = UpdateClassifier.Classify(new ClassifyInput(image, u.Ref, repoDigests, remote, platformDigests, stagedPresent, imageIsId));
        var target = image is null ? u.Ref : ImageReferences.CacheKey(image);
        var quarantined = c.State != UpdateStates.UpToDate && remote.Digest is not null && await quarantine.IsQuarantinedAsync(target, remote.Digest, ct);
        return new ImageUpdateStatusDto(
            u.Ref,
            c.State,
            c.Reason,
            c.LocalDigest,
            remote.Digest,
            remote.Source == "none" ? null : ImageReferences.Iso(remote.CheckedAt),
            remote.Source,
            quarantined,
            u.Users);
    }

    private static bool IsImageId(string r) =>
        (r.StartsWith("sha256:", StringComparison.Ordinal) && r.Length == 71 && r[7..].All(char.IsAsciiHexDigitLower))
        || (r.Length is >= 12 and <= 64 && r.All(char.IsAsciiHexDigitLower));

    private async Task<bool> IsStagedAsync(ImageRef image, string digest, CancellationToken ct)
    {
        var tag = ImageReferences.StagedTag(image, digest);
        if (await staged.GetAsync(tag, ct) is null)
        {
            return false;
        }

        if (await engine.InspectImageAsync(tag, ct) is not null)
        {
            return true;
        }

        await staged.DeleteAsync(tag, ct);
        return false;
    }

    /// <summary>Remote digest of <paramref name="image"/> (cached; registry first, the daemon's <c>distribution inspect</c> as fallback).</summary>
    private async Task<RemoteLookup> RemoteDigestAsync(ImageRef image, bool fresh, CancellationToken ct)
    {
        var key = ImageReferences.CacheKey(image);
        var now = Now;
        if (!fresh)
        {
            var hit = await checks.GetAsync(key, ct);
            if (hit is not null && hit.ExpiresAt > now)
            {
                return new RemoteLookup(
                    hit.RemoteDigest,
                    "cache",
                    hit.State == "error" ? new RemoteError("cached", hit.Reason ?? "earlier lookup failed") : null,
                    hit.CheckedAt);
            }

            if (registry.BackedOffUntil(image) is not null)
            {
                var stale = await checks.GetAsync(key, ct);
                if (stale?.RemoteDigest is not null)
                {
                    return new RemoteLookup(stale.RemoteDigest, "cache", null, stale.CheckedAt);
                }
            }
        }

        RemoteLookup result;
        try
        {
            var head = await registry.HeadManifestAsync(image, null, ct);
            result = new RemoteLookup(head.Digest, "registry", null, now);
        }
        catch (RegistryException e)
        {
            // fallback: ask the daemon (it uses its own credentials)
            var viaEngine = e.Kind == "not_found" ? null : await EngineDigestAsync(image, ct);
            result = viaEngine is not null
                ? new RemoteLookup(viaEngine, "engine", null, now)
                : new RemoteLookup(null, "registry", new RemoteError(e.Kind, e.Message), now);
        }

        await checks.PutAsync(
            new ImageCheck(
                key,
                result.Error is null ? "ok" : "error",
                result.Error?.Message,
                null,
                result.Digest,
                result.Source,
                now,
                now + (long)(result.Error is null ? config.CheckTtl : TimeSpan.FromTicks(Math.Min(config.CheckTtl.Ticks, FailureRetry.Ticks))).TotalMilliseconds),
            ct);
        return result;
    }

    private async Task<string?> EngineDigestAsync(ImageRef image, CancellationToken ct)
    {
        try
        {
            var digest = await engine.DistributionDigestAsync(ImageReferences.Format(image with { Digest = null }), ct);
            return digest is not null && ImageReferences.IsDigest(digest) ? digest : null;
        }
        catch (Errors.AppException)
        {
            return null;
        }
    }

    /// <summary>Per-platform manifest digests behind a remote index (a local image may have been pulled by platform digest).</summary>
    private async Task<IReadOnlyList<string>> PlatformDigestsAsync(ImageRef image, string indexDigest, CancellationToken ct)
    {
        try
        {
            var doc = await registry.GetManifestAsync(image, indexDigest, ct);
            return [.. doc.Manifests.Where(m => m.Os is null || (m.Os == "linux" && m.Architecture == "amd64")).Select(m => m.Digest)];
        }
        catch (RegistryException)
        {
            return [];
        }
    }
}

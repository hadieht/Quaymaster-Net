using System.Text.Json.Nodes;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Redaction;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Docker;

/// <summary>Host facts (Linux: /proc and statvfs). Abstracted so the metrics math is testable.</summary>
public interface IHostProbe
{
    /// <summary>Cumulative CPU jiffies (idle, total) across all cores.</summary>
    (double Idle, double Total) CpuTimes();

    long MemTotalBytes();

    long? MemAvailableBytes();

    double[] LoadAverage();

    double UptimeSeconds();

    /// <summary>Total and available (non-root) bytes of the filesystem holding <paramref name="path"/>; throws <see cref="IOException"/>.</summary>
    (long Total, long Free) Statfs(string path);
}

public interface ISystemService
{
    ModeInfoDto Mode();

    Task<object> HealthAsync(CancellationToken ct);

    Task<JsonNode?> InfoAsync(CancellationToken ct);

    Task<HostMetricsDto> HostAsync(CancellationToken ct);
}

public interface IEventStreamStatus
{
    bool Connected { get; }
}

public interface IStreamCounter
{
    int Count { get; }
}

public sealed class SystemService(
    IDockerEngine engine,
    DockerBootstrap bootstrap,
    SafetyPolicy policy,
    IStackFiles stackFiles,
    IMutationGateway gateway,
    IHostProbe host,
    QmConfig config,
    IEventStreamStatus eventStatus,
    IStreamCounter streams,
    TimeProvider time) : ISystemService
{
    private readonly DateTimeOffset _startedAt = time.GetUtcNow();
    private readonly SemaphoreSlim _cpuGate = new(1, 1);
    private (double Idle, double Total, DateTimeOffset At)? _prevCpu;

    public ModeInfoDto Mode()
    {
        var self = policy.Self;
        return new ModeInfoDto(policy.ReadOnly, policy.ReadOnlyReason, policy.Scope, self.ContainerId, self.Project, stackFiles.AllowedRoots);
    }

    /// <summary>Authenticated health with details (RT-35: /healthz itself reveals nothing).</summary>
    public async Task<object> HealthAsync(CancellationToken ct)
    {
        var docker = "ok";
        try
        {
            await engine.PingAsync(ct);
            await bootstrap.EnsureReadyAsync(ct);
        }
        catch (AppException)
        {
            docker = "unreachable";
        }

        var v = bootstrap.Version;
        _ = gateway;
        return new
        {
            status = docker == "ok" ? "ok" : "degraded",
            docker,
            apiVersion = v?.NegotiatedApiVersion,
            daemonApiVersion = v?.DaemonApiVersion,
            engineVersion = v?.Version,
            composeVersion = await ComposeVersionAsync(ct),
            eventStream = eventStatus.Connected ? "connected" : "disconnected",
            uptimeSeconds = (long)Math.Round((time.GetUtcNow() - _startedAt).TotalSeconds),
            streams = streams.Count,
        };
    }

    private string? _composeVersion;

    private async Task<string?> ComposeVersionAsync(CancellationToken ct)
    {
        if (_composeVersion is not null)
        {
            return _composeVersion;
        }

        try
        {
            var r = await gateway.RunComposeAsync(Compose.ComposeArgv.Version, new Compose.ComposeRunOptions(TimeSpan.FromSeconds(15)), ct);
            if (r.Code == 0)
            {
                _composeVersion = r.Stdout.Trim();
            }
        }
        catch (AppException)
        {
            // docker binary missing: version unknown
        }

        return _composeVersion;
    }

    public async Task<JsonNode?> InfoAsync(CancellationToken ct) =>
        Redactor.RedactDeep(await engine.InfoAsync(ct), new RedactOptions(config.RedactAllEnv));

    public async Task<HostMetricsDto> HostAsync(CancellationToken ct)
    {
        double? cpuPercent;
        await _cpuGate.WaitAsync(ct);
        try
        {
            // CPU: delta between two snapshots
            var now = host.CpuTimes();
            var prev = _prevCpu;
            if (prev is null || (time.GetUtcNow() - prev.Value.At) < TimeSpan.FromMilliseconds(500))
            {
                prev ??= (now.Idle, now.Total, time.GetUtcNow());
                await Task.Delay(300, ct);
            }

            var cur = host.CpuTimes();
            var dTotal = cur.Total - prev.Value.Total;
            cpuPercent = dTotal > 0 ? Math.Round((1 - ((cur.Idle - prev.Value.Idle) / dTotal)) * 1000) / 10 : null;
            _prevCpu = (cur.Idle, cur.Total, time.GetUtcNow());
        }
        finally
        {
            _cpuGate.Release();
        }

        var memTotal = host.MemTotalBytes();
        var memAvailable = host.MemAvailableBytes() ?? memTotal;

        HostRootDto? dockerRoot = null;
        string? dockerRootError = null;
        var root = config.DockerRootMount ?? await DockerRootDirAsync(ct);
        if (root is null)
        {
            dockerRootError = "Docker root directory unknown";
        }
        else
        {
            try
            {
                var (total, free) = host.Statfs(root);
                dockerRoot = new HostRootDto(root, total, free);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                dockerRootError = $"cannot statfs {root}: {e.Message}{(config.DockerRootMount is null ? " (in container mode mount the Docker root read-only and set QM_DOCKER_ROOT_MOUNT)" : string.Empty)}";
            }
        }

        var load = host.LoadAverage();
        return new HostMetricsDto(cpuPercent, [load[0], load[1], load[2]], memTotal, memAvailable, host.UptimeSeconds(), dockerRoot, dockerRootError);
    }

    public async Task<string?> DockerRootDirAsync(CancellationToken ct)
    {
        try
        {
            return (await engine.InfoAsync(ct)).Str("DockerRootDir");
        }
        catch (AppException)
        {
            return null;
        }
    }
}

public interface IDiskUsageService
{
    DiskUsageDto? Cached { get; }

    Task<DiskUsageDto?> GetAsync(bool force, CancellationToken ct);
}

/// <summary>
/// <c>system df</c> data (RT-26): <c>/system/df</c> walks every volume, which can take minutes on big hosts, so it is
/// queried per type (never in parallel), single-flight, and refreshed at most every <c>QM_DF_MIN_INTERVAL_SECONDS</c>
/// unless an admin forces it (itself rate-limited to 60 s).
/// </summary>
public sealed class DiskUsageService(IDockerEngine engine, QmConfig config, IOperationManager operations, TimeProvider time) : IDiskUsageService
{
    private readonly Lock _gate = new();
    private DiskUsageDto? _value;
    private Task<DiskUsageDto>? _inflight;
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;

    public DiskUsageDto? Cached => Volatile.Read(ref _value);

    public Task<DiskUsageDto?> GetAsync(bool force, CancellationToken ct)
    {
        lock (_gate)
        {
            var age = time.GetUtcNow() - _lastStart;
            var stale = _value is null || age > config.DfMinInterval;
            var forced = force && age > TimeSpan.FromSeconds(60);
            if (!stale && !forced)
            {
                return Task.FromResult(_value);
            }

            if (operations.Busy)
            {
                return Task.FromResult(_value); // never during an operation
            }

            if (_inflight is not null)
            {
                return WrapAsync(_inflight, ct);
            }

            _lastStart = time.GetUtcNow();
            _inflight = MeasureAsync();
            return WrapAsync(_inflight, ct);
        }
    }

    private static async Task<DiskUsageDto?> WrapAsync(Task<DiskUsageDto> task, CancellationToken ct) => await task.WaitAsync(ct);

    private async Task<DiskUsageDto> MeasureAsync()
    {
        try
        {
            var images = await engine.DiskUsageAsync("image", CancellationToken.None);
            var containers = await engine.DiskUsageAsync("container", CancellationToken.None);
            var volumes = await engine.DiskUsageAsync("volume", CancellationToken.None);
            var imageList = images.Arr("Images").Objects().ToList();
            var v = new DiskUsageDto(
                Auth.Iso.Format(time.GetUtcNow()),
                new DiskUsageImagesDto(
                    imageList.Count,
                    images.Num("LayersSize") ?? 0,
                    imageList.Sum(i => i.Num("Size") ?? 0),
                    imageList.Count(i => (i.Num("Containers") ?? 0) == 0)),
                containers.Arr("Containers").Objects().ToDictionary(
                    c => c.Str("Id") ?? string.Empty,
                    c => new ContainerDiskDto(c.Num("SizeRw") ?? 0, c.Num("SizeRootFs") ?? 0),
                    StringComparer.Ordinal),
                volumes.Arr("Volumes").Objects()
                    .Where(x => x.Obj("UsageData") is { } u && (u.Num("Size") ?? -1) >= 0)
                    .ToDictionary(x => x.Str("Name") ?? string.Empty, x => x.Obj("UsageData")!.Num("Size") ?? 0, StringComparer.Ordinal));
            Volatile.Write(ref _value, v);
            return v;
        }
        finally
        {
            lock (_gate)
            {
                _inflight = null;
            }
        }
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Background;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Stats;
using Quaymaster.Application.Streaming;
using Quaymaster.Domain.Auditing;

namespace Quaymaster.Application.Monitoring;

public sealed record NotificationEvent(
    string Type,
    string Severity,
    string Title,
    string Message,
    string DedupeKey,
    long? CooldownMs = null,
    IReadOnlyDictionary<string, object?>? Data = null);

/// <summary>
/// Where monitors send what they notice. Delivery (channels, de-duplication across restarts, retries) arrives with the
/// notification service; until then <see cref="AuditNotificationSink"/> records alerts in the audit log.
/// </summary>
public interface INotificationSink
{
    void Notify(NotificationEvent notification);
}

/// <summary>Default sink: audit log entry <c>monitor.alert</c> (actor <c>system</c>) with an in-memory cool-down per de-duplication key.</summary>
public sealed class AuditNotificationSink(IAuditLog audit, TimeProvider time) : INotificationSink
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);

    public void Notify(NotificationEvent notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var cooldown = TimeSpan.FromMilliseconds(notification.CooldownMs ?? 10 * 60_000);
            if (_lastSent.TryGetValue(notification.DedupeKey, out var last) && now - last < cooldown)
            {
                return;
            }

            _lastSent[notification.DedupeKey] = now;
        }

        _ = Task.Run(() => audit.RecordAsync(new AuditRecord(
            "system",
            "monitor.alert",
            AuditOutcome.Success,
            null,
            notification.Type,
            new { notification.Severity, notification.Title, notification.Message, notification.Data })));
    }
}

public interface IMonitorSettingsService
{
    Task<MonitorSettingsDto> GetAsync(CancellationToken ct);

    Task<MonitorSettingsDto> SaveAsync(MonitorSettingsDto settings, Actor actor, CancellationToken ct);
}

/// <summary>
/// Background monitors (spec 8): they only EMIT notification events; de-duplication and delivery belong to the
/// notification service, and a failing channel never reaches back here. Settings are stored in the <c>kv</c> table.
/// This phase covers the container/host monitors (died, unhealthy, OOM, restart loop, CPU, memory, disk); update and
/// maintenance monitors arrive with their services.
/// </summary>
public sealed partial class Monitors(
    IKvStore kv,
    ISystemService system,
    INotificationSink sink,
    IAuditLog audit,
    Broadcaster<DockerEventRecord> events,
    Broadcaster<StatsSampleEvent> samples,
    TimeProvider time,
    ILogger<Monitors> log) : IMonitorSettingsService, IQMBackgroundTask
{
    public const string SettingsKey = "monitor.settings";

    public static readonly MonitorSettingsDto Defaults = new(true, 10, 5L * 1024 * 1024 * 1024, 90, 90, 300, 5, 300, 60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _diedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _stopped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _over = new(StringComparer.Ordinal);
    private MonitorSettingsDto _settings = Defaults;

    public string Name => "monitors";

    public async Task<MonitorSettingsDto> GetAsync(CancellationToken ct)
    {
        var raw = await kv.GetAsync(SettingsKey, ct);
        var s = Defaults;
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                s = Merge(raw);
            }
            catch (JsonException)
            {
                s = Defaults;
            }
        }

        lock (_gate)
        {
            _settings = s;
        }

        return s;
    }

    /// <summary>Stored settings override the defaults field by field (older versions stored fewer fields).</summary>
    public static MonitorSettingsDto Merge(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject ?? throw new JsonException("not an object");
        var d = JsonSerializer.SerializeToNode(Defaults, Json) as System.Text.Json.Nodes.JsonObject ?? throw new JsonException("defaults");
        foreach (var (k, v) in node)
        {
            d[k] = v?.DeepClone();
        }

        return d.Deserialize<MonitorSettingsDto>(Json) ?? Defaults;
    }

    public async Task<MonitorSettingsDto> SaveAsync(MonitorSettingsDto settings, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(actor);
        await kv.SetAsync(SettingsKey, JsonSerializer.Serialize(settings, Json), ct);
        lock (_gate)
        {
            _settings = settings;
        }

        await audit.RecordAsync(new AuditRecord(actor.Name, "monitors.settings.put", AuditOutcome.Success, actor.Ip, "monitors", settings), ct);
        return settings;
    }

    private MonitorSettingsDto Current()
    {
        lock (_gate)
        {
            return _settings;
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _ = await GetAsync(stoppingToken);
        using var e = events.Subscribe(OnDockerEvent);
        using var s = samples.Subscribe(OnSample);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _ = await GetAsync(stoppingToken);
                    await PollAsync(stoppingToken);
                }
                catch (Errors.AppException ex)
                {
                    Log.PollFailed(log, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    // ---- event-driven: died / unhealthy / restart loop ------------------------------------------------------------

    public void OnDockerEvent(DockerEventRecord ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var s = Current();
        if (!s.Enabled || ev.Type != "container")
        {
            return;
        }

        var now = time.GetUtcNow();
        var name = ev.Name ?? ev.ActorId[..Math.Min(12, ev.ActorId.Length)];
        var where = ev.Project is not null ? $"{ev.Project}/{ev.Service ?? name}" : name;
        if (ev.Action is "stop" or "kill")
        {
            lock (_gate)
            {
                _stopped[ev.ActorId] = now;
            }

            return;
        }

        if (ev.Action == "health_status" && ev.Attributes.GetValueOrDefault("health") == "unhealthy")
        {
            sink.Notify(new NotificationEvent("container.unhealthy", "warning", $"Container unhealthy: {where}", $"{name} reports an unhealthy healthcheck.", $"unhealthy:{ev.ActorId}", 30 * 60_000, new Dictionary<string, object?> { ["container"] = name }));
            return;
        }

        if (ev.Action == "oom")
        {
            sink.Notify(new NotificationEvent("container.died", "critical", $"Container killed (out of memory): {where}", $"{name} was killed by the OOM killer.", $"oom:{ev.ActorId}", null, new Dictionary<string, object?> { ["container"] = name }));
            return;
        }

        if (ev.Action != "die" || ev.Synthetic)
        {
            return;
        }

        var code = ev.Attributes.GetValueOrDefault("exitCode");
        int loopCount;
        bool deliberate;
        lock (_gate)
        {
            var lastStop = _stopped.GetValueOrDefault(ev.ActorId);
            deliberate = lastStop != default && now - lastStop < TimeSpan.FromSeconds(15);

            // restart loop detection counts every die, deliberate or not
            var (window, isLoop) = Domain.Maintenance.RestartLoop.Record(_diedAt.GetValueOrDefault(ev.ActorId) ?? [], now, TimeSpan.FromSeconds(s.RestartLoopWindowSeconds), s.RestartLoopCount);
            _diedAt[ev.ActorId] = [.. window];
            loopCount = window.Count;
            _ = isLoop;
        }

        if (loopCount >= s.RestartLoopCount)
        {
            sink.Notify(new NotificationEvent("container.restart_loop", "critical", $"Restart loop: {where}", $"{name} exited {loopCount} times in {s.RestartLoopWindowSeconds}s (last exit code {code ?? "?"}).", $"loop:{ev.ActorId}", 30 * 60_000, new Dictionary<string, object?> { ["container"] = name, ["exits"] = loopCount }));
            return;
        }

        if (!deliberate && code is not null && code != "0")
        {
            sink.Notify(new NotificationEvent("container.died", "warning", $"Container died: {where}", $"{name} exited with code {code}.", $"died:{ev.ActorId}", 10 * 60_000, new Dictionary<string, object?> { ["container"] = name, ["exitCode"] = code }));
        }
    }

    // ---- sampled: per-container CPU / memory ----------------------------------------------------------------------

    public void OnSample(StatsSampleEvent sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var s = Current();
        if (!s.Enabled)
        {
            return;
        }

        var now = time.GetUtcNow();
        var memPct = sample.Point.MemLimit > 0 ? (double)sample.Point.MemUsage / sample.Point.MemLimit * 100 : 0;
        Sustained($"cpu:{sample.Id}", sample.Point.Cpu > s.CpuPercentAbove, now, s, () =>
            sink.Notify(new NotificationEvent("host.cpu_high", "warning", $"High CPU: {sample.Name}", $"{sample.Name} used {sample.Point.Cpu:F0}% of a core for more than {s.SustainSeconds}s.", $"cpu:{sample.Id}", 60 * 60_000, new Dictionary<string, object?> { ["container"] = sample.Name, ["cpu"] = Math.Round(sample.Point.Cpu) })));
        Sustained($"mem:{sample.Id}", memPct > s.MemoryPercentAbove, now, s, () =>
            sink.Notify(new NotificationEvent("host.memory_high", "warning", $"High memory: {sample.Name}", $"{sample.Name} used {memPct:F0}% of its memory limit for more than {s.SustainSeconds}s.", $"mem:{sample.Id}", 60 * 60_000, new Dictionary<string, object?> { ["container"] = sample.Name, ["memoryPercent"] = Math.Round(memPct) })));
    }

    private void Sustained(string key, bool bad, DateTimeOffset now, MonitorSettingsDto s, Action fire)
    {
        DateTimeOffset since;
        lock (_gate)
        {
            if (!bad)
            {
                _over.Remove(key);
                return;
            }

            since = _over.GetValueOrDefault(key, now);
            _over[key] = since;
        }

        if (now - since >= TimeSpan.FromSeconds(s.SustainSeconds))
        {
            fire();
        }
    }

    // ---- polled: host ---------------------------------------------------------------------------------------------

    public async Task PollAsync(CancellationToken ct)
    {
        var s = Current();
        if (!s.Enabled)
        {
            return;
        }

        var now = time.GetUtcNow();
        HostMetricsDto? host;
        try
        {
            host = await system.HostAsync(ct);
        }
        catch (Errors.AppException)
        {
            host = null;
        }

        if (host is null)
        {
            return;
        }

        if (host.DockerRoot is { Total: > 0 } root)
        {
            var pct = (double)root.Free / root.Total * 100;
            if (pct < s.DiskFreePercentBelow || root.Free < s.DiskFreeBytesBelow)
            {
                sink.Notify(new NotificationEvent(
                    "host.disk_low",
                    pct < s.DiskFreePercentBelow / 2 ? "critical" : "warning",
                    "Low disk space on the Docker root",
                    $"{root.Free / Math.Pow(1024, 3):F1} GiB free ({pct:F1} %).",
                    "disk-low",
                    6 * 3_600_000,
                    new Dictionary<string, object?> { ["freeBytes"] = root.Free, ["freePercent"] = Math.Round(pct * 10) / 10 }));
            }
        }

        var memPct = (1 - ((double)host.MemAvailable / host.MemTotal)) * 100;
        Sustained("host-cpu", (host.CpuPercent ?? 0) > s.CpuPercentAbove, now, s, () =>
            sink.Notify(new NotificationEvent("host.cpu_high", "warning", "High host CPU", $"Host CPU at {host.CpuPercent}% for more than {s.SustainSeconds}s.", "host-cpu", 3_600_000)));
        Sustained("host-mem", memPct > s.MemoryPercentAbove, now, s, () =>
            sink.Notify(new NotificationEvent("host.memory_high", "warning", "High host memory use", $"Host memory at {memPct:F0}% for more than {s.SustainSeconds}s.", "host-mem", 3_600_000)));
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 140, Level = LogLevel.Error, Message = "Monitor poll failed: {Reason}")]
        public static partial void PollFailed(ILogger logger, string reason);
    }
}

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Redaction;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Docker;

public sealed record ResolvedContainer(string Id, string Name, string? Project, string? Service, JsonObject Raw)
{
    public bool IsTty => Raw.Obj("Config")?["Tty"].Bool() == true;
}

public sealed record ActionRequest(
    string? Confirm = null,
    bool? ApplyDrift = null,
    bool? ConfirmNamespaceProvider = null,
    string? Signal = null,
    int? TimeoutSeconds = null);

public sealed record LogQuery(int? Tail, long? Since, bool Timestamps);

/// <summary>Implemented by the Compose service: recreates one Compose-managed container (never via clone).</summary>
public interface IRecreator
{
    Task<string> RecreateAsync(ResolvedContainer container, ActionRequest request, Actor actor, CancellationToken ct);
}

public interface IContainerService
{
    SafetyPolicy Policy { get; }

    Task<IReadOnlyList<ContainerInfo>> ListRawAsync(bool fresh, CancellationToken ct);

    Task<IReadOnlyList<ContainerInfo>> ListRawAllAsync(bool fresh, CancellationToken ct);

    /// <summary>
    /// EVERY container the daemon reports, read straight from the engine: no cache, no single-flight join (so it can never
    /// return a list fetched before the caller's last mutation) and no visibility filter. For safety computations only.
    /// </summary>
    Task<IReadOnlyList<ContainerInfo>> ListEverythingFreshAsync(CancellationToken ct);

    Task<IReadOnlyList<ContainerSummaryDto>> ListAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<string, ContainerExtras>> ExtrasAsync(IReadOnlyList<string> ids, CancellationToken ct);

    Task<ResolvedContainer> ResolveAsync(string reference, CancellationToken ct);

    Task<ContainerDetailDto> InspectAsync(string reference, CancellationToken ct);

    Task<LogsDto> LogsTailAsync(string reference, LogQuery query, CancellationToken ct);

    IAsyncEnumerable<LogLine> LogsFollowAsync(ResolvedContainer container, int tail, bool timestamps, CancellationToken ct);

    Task<ActionResultDto> ActAsync(string reference, string action, ActionRequest request, Actor actor, CancellationToken ct);

    void Invalidate();
}

public sealed record ContainerExtras(int RestartCount, string? StartedAt);

public sealed class ContainerService(
    IDockerEngine engine,
    IMutationGateway gateway,
    IIgnoreList ignore,
    Scrubber scrubber,
    QmConfig config,
    Func<IRecreator> recreatorAccessor,
    TimeProvider time) : IContainerService
{
    public static readonly IReadOnlyList<string> Actions = ["start", "stop", "restart", "kill", "pause", "unpause", "remove", "recreate"];

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan ExtrasTtl = TimeSpan.FromSeconds(15);
    private static readonly Regex SignalRe = new("^(SIG)?[A-Z0-9]{1,10}$", RegexOptions.CultureInvariant);

    private readonly Lock _gate = new();
    private (DateTimeOffset At, IReadOnlyList<ContainerInfo> Value)? _cache;
    private Task<IReadOnlyList<ContainerInfo>>? _inflight;
    private readonly Dictionary<string, (DateTimeOffset At, ContainerExtras Value)> _extras = new(StringComparer.Ordinal);

    public SafetyPolicy Policy => gateway.Policy;

    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    /// <summary>MONITORED containers: visibility scope and the ignore list applied.</summary>
    public async Task<IReadOnlyList<ContainerInfo>> ListRawAsync(bool fresh, CancellationToken ct)
    {
        var all = await ListRawAllAsync(fresh, ct);
        await ignore.RefreshAsync(ct);
        return ignore.All.Count == 0 ? all : all.Where(c => !ignore.IsContainerIgnored(c.Name, c.Labels)).ToList();
    }

    /// <summary>
    /// Every container the daemon reports (visibility scope applied, ignore list NOT applied). Only for safety
    /// computations that must see everything ("is this image/volume/network in use?"), never for display or actions.
    /// </summary>
    public Task<IReadOnlyList<ContainerInfo>> ListRawAllAsync(bool fresh, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!fresh && _cache is { } hit && time.GetUtcNow() - hit.At < CacheTtl)
            {
                return Task.FromResult(hit.Value);
            }

            // F-05: a fresh read must never join a fetch that started before the caller's last mutation
            if (fresh)
            {
                return FetchAsync(joinable: false).WaitAsync(ct);
            }

            // single-flight; the shared fetch is not tied to one caller's cancellation
            _inflight ??= FetchAsync();
            return _inflight.WaitAsync(ct);
        }
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListEverythingFreshAsync(CancellationToken ct) =>
        (await engine.ListContainersAsync(ct)).Objects().Select(ContainerInfo.From).ToList();

    private async Task<IReadOnlyList<ContainerInfo>> FetchAsync(bool joinable = true)
    {
        try
        {
            var array = await engine.ListContainersAsync(CancellationToken.None);
            var all = array.Objects().Select(ContainerInfo.From).ToList();
            var policy = gateway.Policy;
            IReadOnlyList<ContainerInfo> visible = policy.VisibilityRestricted ? all.Where(c => policy.ContainerVisible(c.Name, c.Project)).ToList() : all;
            lock (_gate)
            {
                _cache = (time.GetUtcNow(), visible);
            }

            return visible;
        }
        finally
        {
            if (joinable)
            {
                lock (_gate)
                {
                    _inflight = null;
                }
            }
        }
    }

    public async Task<IReadOnlyList<ContainerSummaryDto>> ListAsync(CancellationToken ct) =>
        (await ListRawAsync(false, ct)).Select(c => ToSummary(c)).ToList();

    public ContainerSummaryDto ToSummary(ContainerInfo c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var refusal = gateway.Policy.ContainerRefusal(new ContainerRef(c.Id, c.Name, c.Project));
        return new ContainerSummaryDto
        {
            Id = c.Id,
            ShortId = c.Id.Length > 12 ? c.Id[..12] : c.Id,
            Name = c.Name,
            Image = c.Image,
            ImageId = c.ImageId,
            State = DockerNames.NormState(c.State),
            Status = c.Status,
            Health = DockerNames.HealthFromStatus(c.Status),
            CreatedAt = Iso.Format(DateTimeOffset.FromUnixTimeSeconds(c.Created)),
            Ports = c.Ports,
            Labels = Redactor.RedactLabels(c.Labels),
            ComposeProject = c.Project,
            ComposeService = c.Service,
            ProtectedReason = refusal?.Message,
            Mutable = refusal is null,
        };
    }

    /// <summary>Restart count and start time are only in <c>inspect</c>: fetched with a short cache; failures are skipped.</summary>
    public async Task<IReadOnlyDictionary<string, ContainerExtras>> ExtrasAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var now = time.GetUtcNow();
        var result = new Dictionary<string, ContainerExtras>(StringComparer.Ordinal);
        var todo = new List<string>();
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (_extras.TryGetValue(id, out var hit) && now - hit.At < ExtrasTtl)
                {
                    result[id] = hit.Value;
                }
                else
                {
                    todo.Add(id);
                }
            }
        }

        foreach (var batch in todo.Chunk(8))
        {
            var fetched = await Task.WhenAll(batch.Select(async id =>
            {
                try
                {
                    var raw = await engine.InspectContainerAsync(id, ct);
                    if (raw is null)
                    {
                        return (id, (ContainerExtras?)null);
                    }

                    var started = raw.Obj("State")?.Str("StartedAt");
                    return (id, new ContainerExtras((int)(raw.Num("RestartCount") ?? 0), started is { Length: > 0 } && !started.StartsWith("0001", StringComparison.Ordinal) ? started : null));
                }
                catch (AppException)
                {
                    return (id, null);
                }
            }));
            lock (_gate)
            {
                foreach (var (id, extra) in fetched)
                {
                    if (extra is null)
                    {
                        _extras.Remove(id);
                        continue;
                    }

                    _extras[id] = (now, extra);
                    result[id] = extra;
                }
            }
        }

        lock (_gate)
        {
            foreach (var stale in _extras.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                _extras.Remove(stale);
            }
        }

        return result;
    }

    /// <summary>Resolves any user-supplied reference to the full id via inspect (RT-03: resolve, check, act).</summary>
    public async Task<ResolvedContainer> ResolveAsync(string reference, CancellationToken ct)
    {
        if (!DockerNames.IsSafeRef(reference, RefKind.Container))
        {
            throw AppException.BadRequest("Invalid container reference");
        }

        var raw = await engine.InspectContainerAsync(reference, ct) ?? throw AppException.NotFound("Container");
        var labels = raw.Obj("Config")?.Obj("Labels").StringMap() ?? [];
        var name = (raw.Str("Name") ?? string.Empty).TrimStart('/');
        var project = labels.GetValueOrDefault(ComposeLabels.Project) is { Length: > 0 } p ? p : null;
        if (!gateway.Policy.ContainerVisible(name, project))
        {
            throw AppException.NotFound("Container");
        }

        await ignore.RefreshAsync(ct);
        if (ignore.IsContainerIgnored(name, labels))
        {
            throw new AppException(
                409,
                ErrorCode.Ignored,
                "This container is on the \"stopped monitoring\" list. Restore it in Settings to manage it again.");
        }

        scrubber.AddAll(Redactor.CollectSecretValues(raw["Config"], config.RedactAllEnv));
        return new ResolvedContainer(
            raw.Str("Id") ?? string.Empty,
            name,
            project,
            labels.GetValueOrDefault(ComposeLabels.Service) is { Length: > 0 } s ? s : null,
            raw);
    }

    /// <summary>Inspect output with every secret redacted at every depth (RT-04).</summary>
    public async Task<ContainerDetailDto> InspectAsync(string reference, CancellationToken ct)
    {
        var r = await ResolveAsync(reference, ct);
        var summary = (await ListRawAsync(false, ct)).FirstOrDefault(c => c.Id == r.Id);
        return new ContainerDetailDto(
            summary is null ? null : ToSummary(summary),
            Redactor.RedactDeep(r.Raw, new RedactOptions(config.RedactAllEnv)));
    }

    // ---- logs -----------------------------------------------------------------------------------------------------

    public async Task<LogsDto> LogsTailAsync(string reference, LogQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var r = await ResolveAsync(reference, ct);
        var tail = Math.Min(Math.Max(query.Tail ?? 200, 1), 5000);
        var lines = new List<LogLine>();
        var splitter = new LogLineSplitter(query.Timestamps, lines.Add);
        await foreach (var frame in engine.LogsAsync(r.Id, r.IsTty, new LogRequest(false, tail, query.Since, query.Timestamps), ct))
        {
            splitter.Push(frame);
        }

        splitter.Flush();
        // cap the response at ~2 MiB, keeping the newest lines
        long size = 0;
        var kept = new List<LogLineDto>();
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            size += lines[i].Text.Length + 40;
            if (size > 2 * 1024 * 1024)
            {
                break;
            }

            kept.Add(Clean(lines[i]));
        }

        kept.Reverse();
        return new LogsDto(kept);
    }

    /// <summary>
    /// Follows logs. Rate limited with back-pressure: when the per-second byte budget is spent the engine stream is not
    /// read until the next window, so the daemon (not our memory) absorbs the burst.
    /// </summary>
    public async IAsyncEnumerable<LogLine> LogsFollowAsync(
        ResolvedContainer container,
        int tail,
        bool timestamps,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(container);
        var pending = new Queue<LogLine>();
        var splitter = new LogLineSplitter(timestamps, pending.Enqueue);
        var limiter = new ByteRateLimiter(config.Sse.LogBytesPerSecond, time);
        var request = new LogRequest(true, Math.Min(Math.Max(tail, 0), 2000), null, timestamps);
        await foreach (var frame in engine.LogsAsync(container.Id, container.IsTty, request, ct))
        {
            var wait = limiter.Account(frame.Data.Length);
            splitter.Push(frame);
            while (pending.Count > 0)
            {
                yield return CleanLine(pending.Dequeue());
            }

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }
        }

        splitter.Flush();
        while (pending.Count > 0)
        {
            yield return CleanLine(pending.Dequeue());
        }
    }

    private LogLine CleanLine(LogLine l) => l with { Text = Redactor.RedactText(scrubber.Scrub(l.Text)) };

    private LogLineDto Clean(LogLine l)
    {
        var c = CleanLine(l);
        return new LogLineDto(c.Stream, c.Ts, c.Text);
    }

    // ---- actions --------------------------------------------------------------------------------------------------

    public async Task<ActionResultDto> ActAsync(string reference, string action, ActionRequest request, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (!Actions.Contains(action, StringComparer.Ordinal))
        {
            throw AppException.BadRequest($"Unknown action \"{action}\"");
        }

        var scope = new AuditScope(actor, $"container.{action}", reference, request);
        return await gateway.AuditedAsync(scope, async () =>
        {
            var c = await ResolveAsync(reference, ct);
            // identity is now known: record the real target, not what the caller typed
            scope.Target = $"{c.Name} ({(c.Id.Length > 12 ? c.Id[..12] : c.Id)})";
            var cref = new ContainerRef(c.Id, c.Name, c.Project);
            await gateway.AssertContainerAsync(cref, ct);

            if (action is "remove" or "recreate" && request.Confirm != c.Name)
            {
                throw new AppException(409, ErrorCode.ConfirmationRequired, $"Confirm by sending {{\"confirm\":\"{c.Name}\"}}", new { expected = c.Name });
            }

            if (action == "kill" && request.Signal is not null && !SignalRe.IsMatch(request.Signal))
            {
                throw AppException.BadRequest("Invalid signal");
            }

            if (action == "recreate")
            {
                if (c.Project is null)
                {
                    throw new AppException(
                        409,
                        ErrorCode.Unsupported,
                        "Recreating a standalone container (config clone) is not available yet; it arrives with the update workflow (RT-12). Compose-managed containers can be recreated.");
                }

                var recreator = recreatorAccessor();
                var operationId = await recreator.RecreateAsync(c, request, actor, ct);
                Invalidate();
                return new ActionResultDto { OperationId = operationId };
            }

            // destructive: make sure the ID still carries the name we checked (another tool may have renamed it)
            if (action is "remove" or "kill")
            {
                var again = await ResolveAsync(c.Id, ct);
                if (again.Name != c.Name)
                {
                    throw AppException.Conflict("Container was renamed while the action was pending");
                }
            }

            var timeout = request.TimeoutSeconds is { } t ? Math.Min(Math.Max(t, 0), 600) : (int?)null;
            var command = new ContainerCommand(c.Id, ParseAction(action), timeout, request.Signal);
            var changed = await gateway.ExecuteContainerAsync(cref, command, ct);
            Invalidate();
            return new ActionResultDto { Note = changed ? null : "already in requested state" };
        }, ct);
    }

    private static ContainerAction ParseAction(string action) => action switch
    {
        "start" => ContainerAction.Start,
        "stop" => ContainerAction.Stop,
        "restart" => ContainerAction.Restart,
        "kill" => ContainerAction.Kill,
        "pause" => ContainerAction.Pause,
        "unpause" => ContainerAction.Unpause,
        "remove" => ContainerAction.Remove,
        _ => throw AppException.BadRequest($"Unknown action \"{action}\""),
    };

    internal static string FormatInvariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Per-second byte budget. <see cref="Account"/> returns how long the caller must wait before reading more.</summary>
public sealed class ByteRateLimiter(int bytesPerSecond, TimeProvider time)
{
    private long _used;
    private DateTimeOffset _windowStart = time.GetUtcNow();

    public TimeSpan Account(int bytes)
    {
        var now = time.GetUtcNow();
        if (now - _windowStart >= TimeSpan.FromSeconds(1))
        {
            _windowStart = now;
            _used = 0;
        }

        _used += bytes;
        if (_used < bytesPerSecond)
        {
            return TimeSpan.Zero;
        }

        var wait = TimeSpan.FromMilliseconds(Math.Max(10, 1000 - (now - _windowStart).TotalMilliseconds));
        _used = 0;
        _windowStart = now + wait;
        return wait;
    }
}

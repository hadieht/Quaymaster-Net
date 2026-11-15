using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Registry;
using Quaymaster.Application.Safety;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Compose;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Safety;
using Quaymaster.Domain.Updates;

namespace Quaymaster.UnitTests.Maintenance;

/// <summary>In-memory <see cref="IRunStore"/> with the semantics of the SQLite one (compare-and-set transitions, steps, intents).</summary>
internal sealed class FakeRunStore : IRunStore
{
    private sealed class Row
    {
        public required NewRun New { get; init; }

        public string Status { get; set; } = "";

        public string? Reason { get; set; }

        public RunResult? Result { get; set; }

        public IReadOnlyList<string>? Guidance { get; set; }

        public string? ApprovedBy { get; set; }

        public long? ApprovedAt { get; set; }

        public string? ApprovedPlanHash { get; set; }

        public long? StartedAt { get; set; }

        public long? FinishedAt { get; set; }

        public long? WindowEnd { get; set; }

        public List<RunStepDto> Steps { get; } = [];
    }

    private readonly List<Row> _rows = [];
    private readonly List<(long Id, NewIntent Intent, bool Done)> _intents = [];
    private int _stepId;

    public int IntentsOpen => _intents.Count(i => !i.Done);

    private Row Get(string id) => _rows.First(r => r.New.Id == id);

    public Task InsertAsync(NewRun run, CancellationToken ct)
    {
        _rows.Add(new Row { New = run, Status = run.Status, Reason = run.Reason });
        return Task.CompletedTask;
    }

    private static string? Iso(long? ms) => ms is { } v ? ImageReferences.Iso(v) : null;

    private static RunInfo Info(Row r) => new()
    {
        Id = r.New.Id,
        Kind = r.New.Kind,
        Trigger = r.New.Trigger,
        ScheduleId = r.New.ScheduleId,
        Occurrence = Iso(r.New.OccurrenceUtc),
        WindowId = r.New.WindowId,
        WindowEnd = Iso(r.WindowEnd),
        Target = r.New.Target,
        Status = r.Status,
        Reason = r.Reason,
        PlanHash = r.New.Plan?.PlanHash,
        RequestedBy = r.New.RequestedBy,
        ApprovalExpiresAt = Iso(r.New.ApprovalExpiresAt),
        ApprovedBy = r.ApprovedBy,
        ApprovedAt = Iso(r.ApprovedAt),
        ApprovedPlanHash = r.ApprovedPlanHash,
        Result = r.Result,
        Guidance = r.Guidance,
        CreatedAt = ImageReferences.Iso(r.New.CreatedAt),
        StartedAt = Iso(r.StartedAt),
        FinishedAt = Iso(r.FinishedAt),
    };

    public Task<RunDetail?> GetAsync(string id, CancellationToken ct)
    {
        var r = _rows.FirstOrDefault(x => x.New.Id == id);
        return Task.FromResult(r is null ? null : new RunDetail { Info = Info(r), Plan = r.New.Plan, Steps = [.. r.Steps] });
    }

    public Task<RunMeta?> GetMetaAsync(string id, CancellationToken ct)
    {
        var r = _rows.FirstOrDefault(x => x.New.Id == id);
        return Task.FromResult(r is null ? null : new RunMeta(r.New.Options, r.New.ApprovalExpiresAt, r.StartedAt, r.WindowEnd, r.New.WindowId, r.New.ScheduleId));
    }

    public Task<IReadOnlyList<RunInfo>> ListAsync(int limit, string? status, string? target, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunInfo>>([.. _rows.Where(r => (status is null || r.Status == status) && (target is null || r.New.Target == target)).Reverse().Take(limit).Select(Info)]);

    /// <summary>Pretends another run is executing right now (a foreign `running` row that is not part of the listed rows).</summary>
    public bool OtherRunExecuting { get; set; }

    public Task<RunInfo?> RunningAsync(CancellationToken ct) =>
        Task.FromResult(_rows.Where(r => r.Status == "running").Select(Info).FirstOrDefault() ?? (OtherRunExecuting ? new RunInfo { Id = "other", Kind = "compose", Trigger = "manual", Target = "other", RequestedBy = "admin", CreatedAt = "2026-01-01T00:00:00Z", Status = RunStatuses.Running } : null));

    public Task<bool> ApproveAsync(string id, string by, string planHash, long now, CancellationToken ct)
    {
        var r = Get(id);
        if (r.Status != RunStatuses.AwaitingApproval)
        {
            return Task.FromResult(false);
        }

        (r.Status, r.ApprovedBy, r.ApprovedAt, r.ApprovedPlanHash) = (RunStatuses.Approved, by, now, planHash);
        return Task.FromResult(true);
    }

    public Task<bool> MarkRunningAsync(string id, long now, CancellationToken ct)
    {
        var r = Get(id);
        if (r.Status != RunStatuses.Approved)
        {
            return Task.FromResult(false);
        }

        (r.Status, r.StartedAt) = (RunStatuses.Running, now);
        return Task.FromResult(true);
    }

    public Task SetWindowEndAsync(string id, long windowEndUtc, CancellationToken ct)
    {
        Get(id).WindowEnd = windowEndUtc;
        return Task.CompletedTask;
    }

    /// <summary>Number of upcoming <see cref="FinishAsync"/> calls that fail with a transient database error (F-07).</summary>
    public int FinishFailures { get; set; }

    public Task FinishAsync(string id, string status, RunResult? result, IReadOnlyList<string>? guidance, string? reason, long now, CancellationToken ct)
    {
        if (FinishFailures > 0)
        {
            FinishFailures--;
            throw new InvalidOperationException("database is locked");
        }

        var r = Get(id);
        (r.Status, r.Result, r.Guidance, r.Reason, r.FinishedAt) = (status, result, guidance, reason, now);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunInfo>> ExpireApprovalsAsync(long now, CancellationToken ct)
    {
        var due = _rows.Where(r => r.Status == RunStatuses.AwaitingApproval && r.New.ApprovalExpiresAt is { } e && e <= now).ToList();
        foreach (var r in due)
        {
            (r.Status, r.FinishedAt, r.Reason) = (RunStatuses.Skipped, now, "approval expired");
        }

        return Task.FromResult<IReadOnlyList<RunInfo>>([.. due.Select(Info)]);
    }

    public Task<IReadOnlyList<RunInfo>> InterruptRunningAsync(long now, CancellationToken ct)
    {
        var running = _rows.Where(r => r.Status == RunStatuses.Running).ToList();
        var infos = running.Select(Info).ToList();
        foreach (var r in running)
        {
            (r.Status, r.FinishedAt) = (RunStatuses.Interrupted, now);
        }

        return Task.FromResult<IReadOnlyList<RunInfo>>(infos);
    }

    public Task<int> InterruptApprovedAsync(long now, CancellationToken ct)
    {
        var approved = _rows.Where(r => r.Status == RunStatuses.Approved).ToList();
        foreach (var r in approved)
        {
            (r.Status, r.FinishedAt) = (RunStatuses.Interrupted, now);
        }

        return Task.FromResult(approved.Count);
    }

    public Task<int> StartStepAsync(string runId, int seq, string? service, string stepName, long now, CancellationToken ct)
    {
        var id = ++_stepId;
        Get(runId).Steps.Add(new RunStepDto(id, seq, service, stepName, "running", ImageReferences.Iso(now), null, null));
        return Task.FromResult(id);
    }

    public Task FinishStepAsync(int stepId, string status, JsonObject? detail, long now, CancellationToken ct)
    {
        foreach (var r in _rows)
        {
            var i = r.Steps.FindIndex(s => s.Id == stepId);
            if (i >= 0)
            {
                r.Steps[i] = r.Steps[i] with { Status = status, FinishedAt = ImageReferences.Iso(now), Detail = detail };
            }
        }

        return Task.CompletedTask;
    }

    public Task FailOpenStepsAsync(string runId, long now, CancellationToken ct)
    {
        var r = Get(runId);
        for (var i = 0; i < r.Steps.Count; i++)
        {
            if (r.Steps[i].Status == "running")
            {
                r.Steps[i] = r.Steps[i] with { Status = "failed", FinishedAt = ImageReferences.Iso(now) };
            }
        }

        return Task.CompletedTask;
    }

    public Task<long> AddIntentAsync(NewIntent intent, long now, CancellationToken ct)
    {
        var id = _intents.Count + 1L;
        _intents.Add((id, intent, false));
        return Task.FromResult(id);
    }

    public Task CompleteIntentAsync(long id, long now, CancellationToken ct)
    {
        var i = _intents.FindIndex(x => x.Id == id);
        _intents[i] = _intents[i] with { Done = true };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IntentInfo>> OpenIntentsAsync(string? operationId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IntentInfo>>([.. _intents.Where(i => !i.Done && (operationId is null || i.Intent.OperationId == operationId))
            .Select(i => new IntentInfo(i.Id, i.Intent.OperationId, i.Intent.Service, i.Intent.Action, i.Intent.FromImage, i.Intent.ToImage, i.Intent.ContainerIdsBefore))]);
}

internal sealed class FakeRollbackStore : IRollbackStore
{
    public Dictionary<string, RollbackPoint> Points { get; } = new(StringComparer.Ordinal);

    public Task PutAsync(RollbackPoint point, CancellationToken ct)
    {
        Points[point.Tag] = point;
        return Task.CompletedTask;
    }

    public Task<RollbackPoint?> GetAsync(string tag, CancellationToken ct) => Task.FromResult(Points.GetValueOrDefault(tag));

    public Task<IReadOnlyList<RollbackPoint>> ActiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RollbackPoint>>([.. Points.Values.Where(p => !p.Released)]);

    public Task ReleaseAsync(string tag, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<RollbackPoint>> ListAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<RollbackPoint>>([.. Points.Values.Take(limit)]);

    public Task<IReadOnlyList<RollbackPoint>> ForRunAsync(string runId, CancellationToken ct) => Task.FromResult<IReadOnlyList<RollbackPoint>>([.. Points.Values.Where(p => p.RunId == runId)]);
}

internal sealed class CollectingSink : INotificationSink
{
    public List<NotificationEvent> Events { get; } = [];

    public void Notify(NotificationEvent notification) => Events.Add(notification);

    public IEnumerable<string> Types => Events.Select(e => e.Type);
}

internal sealed class FakeOperationContext : IOperationContext
{
    public string Id => "op";

    public List<string> Output { get; } = [];

    public void Log(string text) => Output.Add(text);

    public bool ShuttingDown { get; set; }

    public void ReleaseLock()
    {
    }
}

/// <summary>Image engine double: user tags, staged tags, ids. Records the ORDER of every mutation.</summary>
internal sealed class FakeImages : IImageOps
{
    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    public List<string> Log { get; } = [];

    public string? PullFailure { get; set; }

    public Dictionary<string, string> PullResults { get; } = new(StringComparer.Ordinal);

    public Task<JsonObject?> InspectAsync(string nameOrId, CancellationToken ct)
    {
        if (Tags.TryGetValue(nameOrId, out var id))
        {
            return Task.FromResult<JsonObject?>(new JsonObject { ["Id"] = id, ["RepoDigests"] = new JsonArray() });
        }

        return Task.FromResult<JsonObject?>(Tags.ContainsValue(nameOrId) || nameOrId.StartsWith("sha256:", StringComparison.Ordinal) ? new JsonObject { ["Id"] = nameOrId, ["RepoDigests"] = new JsonArray() } : null);
    }

    public Task<PullResult> PullByDigestAsync(ImageRef image, string digest, Action<string>? log, CancellationToken ct)
    {
        Log.Add($"pull:{digest}");
        if (PullFailure is not null)
        {
            throw new AppException(502, ErrorCode.RegistryError, PullFailure);
        }

        var id = PullResults.GetValueOrDefault(digest) ?? "sha256:" + new string('9', 64);
        var tag = ImageReferences.StagedTag(image, digest);
        Tags[tag] = id;
        return Task.FromResult(new PullResult(id, digest, tag));
    }

    public Task TagAsync(string imageId, string repoTag, CancellationToken ct)
    {
        Log.Add($"tag:{repoTag}={imageId[..Math.Min(12, imageId.Length)]}");
        Tags[repoTag] = imageId;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string nameOrId, CancellationToken ct)
    {
        Log.Add($"rmi:{nameOrId}");
        Tags.Remove(nameOrId);
        return Task.CompletedTask;
    }
}

/// <summary>Docker / Compose double: services with containers, a user-tag → image mapping (via <see cref="FakeImages"/>), scripted health.</summary>
internal sealed class FakeEnv(FakeImages images) : IRunEnvironment
{
    public sealed class Svc
    {
        public required string Ref { get; init; }

        public required string Image { get; set; }

        public List<string> Ids { get; set; } = [];

        public string State { get; set; } = "running";

        public bool Healthy { get; set; } = true;
    }

    private int _next = 100;

    public Dictionary<string, Svc> Services { get; } = new(StringComparer.Ordinal);

    public string Project { get; set; } = "shop";

    /// <summary>Image IDs whose containers report <c>unhealthy</c>.</summary>
    public HashSet<string> BadImages { get; } = new(StringComparer.Ordinal);

    public HashSet<string> UpFails { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string?> Hashes { get; } = new(StringComparer.Ordinal);

    public bool Drift { get; set; }

    public List<string> Ups { get; } = [];

    public List<string> Restarts { get; } = [];

    public Task<ProjectTarget> TargetAsync(string project, CancellationToken ct) => Task.FromResult(new ProjectTarget(project, "/srv/shop", ["/srv/shop/compose.yaml"]));

    /// <summary>After an <c>up</c>, this many container reads still return the list from BEFORE it (F-05: single-flight join).</summary>
    public int StaleReadsAfterUp { get; set; }

    /// <summary>Image ids whose inspect throws a Docker error (F-06: socket timeout after compose up).</summary>
    public HashSet<string> InspectThrowsFor { get; } = new(StringComparer.Ordinal);

    /// <summary>Container reads throw a Docker error while true (F-06).</summary>
    public bool ContainersThrow { get; set; }

    private List<EngineContainer>? _stale;

    private int _staleLeft;

    private List<EngineContainer> Snapshot() =>
        [.. Services.SelectMany(kv => kv.Value.Ids.Select(id => new EngineContainer(id, kv.Key + "-1", Project, kv.Key, kv.Value.Image, kv.Value.State, kv.Value.State == "running" ? "Up 1 minute" : "Exited")))];

    public Task<IReadOnlyList<EngineContainer>> ContainersAsync(CancellationToken ct)
    {
        if (ContainersThrow)
        {
            throw new AppException(503, ErrorCode.DockerUnavailable, "Docker daemon unreachable: timeout");
        }

        if (_staleLeft > 0 && _stale is not null)
        {
            _staleLeft--;
            return Task.FromResult<IReadOnlyList<EngineContainer>>(_stale);
        }

        return Task.FromResult<IReadOnlyList<EngineContainer>>(Snapshot());
    }

    public Task<InspectLite?> InspectAsync(string containerId, CancellationToken ct)
    {
        var kv = Services.FirstOrDefault(s => s.Value.Ids.Contains(containerId));
        if (kv.Value is null)
        {
            return Task.FromResult<InspectLite?>(null);
        }

        var s = kv.Value;
        if (InspectThrowsFor.Contains(s.Image))
        {
            throw new AppException(503, ErrorCode.DockerUnavailable, "Docker daemon unreachable: timeout");
        }

        var healthy = s.Healthy && !BadImages.Contains(s.Image);
        return Task.FromResult<InspectLite?>(new InspectLite(s.Image, 0, s.State, s.State == "running", false, 0, "2026-11-14T12:00:00Z", healthy ? "healthy" : "unhealthy"));
    }

    public Task<DeployResult> UpAsync(string project, string service, bool forceRecreate, bool noStart, Action<string> log, CancellationToken ct)
    {
        Ups.Add(service);
        if (StaleReadsAfterUp > 0)
        {
            (_stale, _staleLeft) = (Snapshot(), StaleReadsAfterUp);
        }

        if (UpFails.Contains(service))
        {
            return Task.FromResult(new DeployResult(false, 1, "docker compose up exited with code 1: boom", ["compose", "up"]));
        }

        var s = Services[service];
        var want = images.Tags.GetValueOrDefault(RecoveryCommands.RepoTagOf(s.Ref)!) ?? s.Image;
        if (forceRecreate || want != s.Image)
        {
            s.Image = want;
            s.Ids = [$"c{++_next:D3}".PadRight(64, '0')];
        }

        return Task.FromResult(new DeployResult(true, 0, string.Empty, ["compose", "up", service]));
    }

    public Task<string?> ConfigHashAsync(string project, string service, CancellationToken ct) => Task.FromResult(Hashes.TryGetValue(service, out var h) ? h : "hash-" + service);

    public Task<bool> HasDriftAsync(string project, string service, CancellationToken ct) => Task.FromResult(Drift);

    public Task<JsonObject> SnapshotAsync(string project, CancellationToken ct) => Task.FromResult(new JsonObject { ["files"] = new JsonArray() });

    public Task<IReadOnlyList<GraphEdge>> EdgesAsync(string project, CancellationToken ct) => Task.FromResult<IReadOnlyList<GraphEdge>>([]);

    public Task<string?> RestartAsync(string containerId, CancellationToken ct)
    {
        Restarts.Add(containerId);
        return Task.FromResult<string?>(null);
    }

    public void Invalidate()
    {
    }
}

internal sealed class FakeProbe : IHttpProbe
{
    public Queue<int> Answers { get; } = new();

    public Task<int> StatusAsync(string url, CancellationToken ct) => Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : 503);
}

/// <summary>A wired executor / run service with every collaborator replaced by an in-memory double.</summary>
internal sealed class RunWorld
{
    public const string Old = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string New = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string Digest = "sha256:" + "d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1";

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

    public FakeRunStore Runs { get; } = new();

    public FakeRollbackStore Rollback { get; } = new();

    public FakeImages Images { get; } = new();

    public FakeEnv Env { get; }

    public CollectingSink Notes { get; } = new();

    public FakeProbe Probe { get; } = new();

    public RunHooks Hooks { get; } = new();

    public IQuarantineService Quarantine { get; } = Substitute.For<IQuarantineService>();

    public IPlanService Planner { get; } = Substitute.For<IPlanService>();

    public IWindowStore Windows { get; } = Substitute.For<IWindowStore>();

    public IMutationGateway Gateway { get; } = Substitute.For<IMutationGateway>();

    public MaintenanceGate Gate { get; } = new();

    public QmConfig Config { get; }

    public Plan CurrentPlan { get; set; } = null!;

    public RunExecutor Executor { get; }

    public RunWorld(params SvcSpec[] specs)
    {
        Env = new FakeEnv(Images);
        Config = QmConfigLoader.Load(
            new Dictionary<string, string?> { ["QM_SECRET_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray()) },
            new NoFiles());
        Gateway.Policy.Returns(new SafetyPolicy(new SafetyOptions(false, [], [], [])));
        Quarantine.AddAsync(default!, default!, default, default, default).ReturnsForAnyArgs(Task.FromResult(true));
        Planner.PlanProjectAsync(default!, default).ReturnsForAnyArgs(_ => Task.FromResult<Plan>(CurrentPlan));
        foreach (var s in specs)
        {
            Env.Services[s.Name] = new FakeEnv.Svc { Ref = s.Ref, Image = Old, Ids = [s.Id] };
            Images.Tags[RecoveryCommands.RepoTagOf(s.Ref)!] = Old;
            Images.PullResults[Digest] = New;
        }

        CurrentPlan = BuildPlan(specs);
        var health = new HealthGate(Env, Probe, new HealthTimings { PollMs = 1000, Sleep = (ms, _) => { Clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; } }, Clock);
        Executor = new RunExecutor(
            Runs, Rollback, Substitute.For<IStagedStore>(), Quarantine, Images, Planner, Env, health, Windows, Notes, Gateway, Gate,
            Substitute.For<IUpdateService>(), Hooks, Config, Clock);
    }

    public sealed record SvcSpec(string Name, string Id, string Ref = "nginx:1.27", bool AutoRollback = true, bool Writable = false, bool WasRunning = true);

    public static Plan BuildPlan(params SvcSpec[] specs)
    {
        var services = specs.Select(s => new PlanService
        {
            Service = s.Name,
            Role = "update",
            Containers = [new PlanContainer(s.Id, s.Name + "-1")],
            Ref = s.Ref,
            FromImageId = Old,
            FromDigest = "sha256:" + new string('0', 64),
            ToDigest = Digest,
            ConfigHash = "hash-" + s.Name,
            WasRunning = s.WasRunning,
            OneShot = false,
            AutoRollback = s.AutoRollback && !s.Writable,
            AutoRollbackReason = s.Writable ? "writable persistent mount" : "enabled by default",
            Health = new PlanHealth("healthcheck", 60, 15, null),
            Downtime = "short",
            DataRisk = new DataRisk(s.Writable, s.Writable ? [new PlanMount("volume", "data", "/var/lib/data", false)] : [], string.Empty),
            Critical = false,
            RequireApproval = true,
            UpdateMode = "notify",
            Unattended = new Unattended(false, ["requires approval"]),
            Quarantined = false,
        }).ToList();
        var plan = new Plan
        {
            Target = "shop",
            CreatedAt = "2026-11-14T12:00:00.000Z",
            Order = [.. specs.Select(s => s.Name)],
            Services = services,
            Excluded = [],
            Preflight = new PlanPreflight(true, []),
            Validation = new PlanValidation(true, [], []),
            Drift = [],
            Impact = new PlanImpact([], []),
            Disk = new PlanDisk(null, null, 0, true),
            Warnings = [],
            PlanHash = string.Empty,
        };
        return plan with { PlanHash = PlanHasher.Compute(plan) };
    }

    /// <summary>Inserts an approved run for <see cref="CurrentPlan"/> (approval bound to <paramref name="approvedHash"/>).</summary>
    public async Task<string> ApprovedRunAsync(string? approvedHash = null, JsonObject? options = null, string? windowId = null)
    {
        var id = Guid.NewGuid().ToString();
        var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await Runs.InsertAsync(new NewRun(id, "update", "manual", "shop", RunStatuses.AwaitingApproval, null, CurrentPlan, "admin", options ?? [], now, WindowId: windowId, ApprovalExpiresAt: now + 3_600_000), default);
        (await Runs.ApproveAsync(id, "admin", approvedHash ?? CurrentPlan.PlanHash, now, default)).Should().BeTrue();
        return id;
    }

    public Task<string> RunAsync(string runId) => Executor.ExecuteAsync(runId, new FakeOperationContext(), default);

    public async Task<RunDetail> DetailAsync(string runId) => (await Runs.GetAsync(runId, default))!;

    private sealed class NoFiles : IFileReader
    {
        public string ReadAllText(string path) => throw new FileNotFoundException(path);
    }
}

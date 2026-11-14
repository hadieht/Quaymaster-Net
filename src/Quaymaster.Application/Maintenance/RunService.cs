using System.Text.Json.Nodes;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Safety;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Maintenance;

namespace Quaymaster.Application.Maintenance;

/// <summary>A schedule firing (T4c's scheduler): the occurrence the run belongs to and whether the schedule is <c>auto</c>.</summary>
public sealed record ScheduledRun(string ScheduleId, string ScheduleName, string Mode, long OccurrenceUtc, string? WindowId);

public sealed record CreateRunRequest(
    string Project,
    IReadOnlyList<string>? Services = null,
    bool ApplyDrift = false,
    bool AllowUnhealthy = false,
    bool StartNow = false,
    string? Confirm = null,
    string? WindowId = null,
    bool IgnoreWindow = false);

public interface IRunService
{
    Task<RunListDto> ListAsync(int limit, string? status, string? target, CancellationToken ct);

    Task<RunDetailDto> GetAsync(string id, CancellationToken ct);

    /// <summary>Creates an update run from a fresh plan; it waits for approval unless an admin starts it right away or the unattended truth table holds.</summary>
    Task<RunDetailDto> CreateAsync(CreateRunRequest request, Actor actor, ScheduledRun? schedule, CancellationToken ct);

    Task<RunDetailDto> ApproveAsync(string id, string planHash, Actor actor, CancellationToken ct);

    Task<RunDetailDto> RejectAsync(string id, Actor actor, CancellationToken ct);

    Task<OperationStartedDto> RestoreAsync(string id, RestoreBody body, Actor actor, CancellationToken ct);

    /// <summary>Overdue approvals become <c>skipped</c>. Called on every read and by the scheduler.</summary>
    Task<int> ExpireApprovalsAsync(CancellationToken ct);
}

/// <summary>
/// Lifecycle of maintenance runs: approval bound to the plan hash (with expiry), the single-run lock, restore. The state machine
/// that actually changes containers lives in <see cref="RunExecutor"/>. Port of <c>MaintenanceService</c> (run part).
/// </summary>
public sealed class RunService(
    IRunStore runs,
    IPlanService planner,
    IRunExecutor executor,
    IOperationManager operations,
    IWindowStore windows,
    IRollbackStore rollback,
    INotificationSink notify,
    IMutationGateway gateway,
    QmConfig config,
    TimeProvider time) : IRunService
{
    private const string LockKey = "maintenance";

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<RunListDto> ListAsync(int limit, string? status, string? target, CancellationToken ct)
    {
        await ExpireApprovalsAsync(ct);
        return new RunListDto(await runs.ListAsync(limit, status, target, ct));
    }

    public async Task<RunDetailDto> GetAsync(string id, CancellationToken ct)
    {
        await ExpireApprovalsAsync(ct);
        return RunDetailDto.From(await runs.GetAsync(id, ct) ?? throw AppException.NotFound("Run"));
    }

    public async Task<int> ExpireApprovalsAsync(CancellationToken ct)
    {
        var expired = await runs.ExpireApprovalsAsync(Now(), ct);
        foreach (var r in expired)
        {
            notify.Notify(new NotificationEvent(
                "run.skipped",
                "info",
                $"Approval expired: {r.Target}",
                $"The update request {r.Id} was not approved in time and was skipped.",
                $"expired:{r.Id}",
                null,
                new Dictionary<string, object?> { ["run"] = r.Id, ["target"] = r.Target, ["status"] = RunStatuses.Skipped }));
            await AuditAsync(new Actor("system", null), "maintenance.run.expired", r.Target, new { run = r.Id });
        }

        return expired.Count;
    }

    private static AppException ReadOnly(string reason) => new(409, ErrorCode.ReadOnly, $"Read-only mode: {reason}");

    private async Task EnsureNoRunActiveAsync(CancellationToken ct)
    {
        if (await runs.RunningAsync(ct) is not null)
        {
            throw AppException.Conflict("Another maintenance run is in progress (one run at a time)");
        }
    }

    public async Task<RunDetailDto> CreateAsync(CreateRunRequest request, Actor actor, ScheduledRun? schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var req = request;
        var sched = schedule;
        var manual = sched is null;
        var options = new JsonObject();
        if (req.Services is not null)
        {
            options["services"] = new JsonArray([.. req.Services.Select(s => (JsonNode?)JsonValue.Create(s))]);
        }

        options["applyDrift"] = req.ApplyDrift;
        options["allowUnhealthy"] = req.AllowUnhealthy;
        options["ignoreWindow"] = req.IgnoreWindow;
        if (!manual)
        {
            options["scheduleMode"] = sched!.Mode;
            options["onlyModes"] = new JsonArray("auto");
        }

        if (gateway.Policy.ReadOnlyReason is { } ro)
        {
            throw ReadOnly(ro);
        }

        if (req.StartNow && req.Confirm != req.Project)
        {
            throw new AppException(409, ErrorCode.ConfirmationRequired, $"Confirm by sending {{\"confirm\":\"{req.Project}\"}}", new { expected = req.Project });
        }

        var plan = await planner.PlanProjectAsync(
            new PlanRequest(req.Project, req.Services, sched?.Mode, manual ? null : ["auto"], req.ApplyDrift, req.AllowUnhealthy, true),
            ct);
        var id = Guid.NewGuid().ToString();
        var now = Now();
        var windowId = req.WindowId ?? sched?.WindowId;
        NewRun Row(string status, string? reason, long? expires) => new(
            id, "update", manual ? "manual" : "schedule", req.Project, status, reason, plan, actor.Name, options, now, sched?.ScheduleId, sched?.OccurrenceUtc, windowId, expires);

        if (plan.Blocked is { } blocked)
        {
            var nothing = blocked.Code == "nothing_to_do";
            var status = nothing ? RunStatuses.Skipped : RunStatuses.Blocked;
            await runs.InsertAsync(Row(status, blocked.Message, null), ct);
            await runs.FinishAsync(id, status, null, plan.Drift.Count > 0 ? ["Review the configuration drift before updating."] : null, blocked.Message, now, ct);
            await AuditAsync(actor, "maintenance.run.create", req.Project, new { run = id, blocked = blocked.Code });
            if (!manual && !nothing)
            {
                notify.Notify(new NotificationEvent(
                    "run.failed", "warning", $"Scheduled update blocked: {req.Project}", blocked.Message, $"blocked:{req.Project}:{blocked.Code}", 24 * 3_600_000L));
            }

            return await GetAsync(id, ct);
        }

        // RT-23: unattended only when policy auto ∧ schedule auto ∧ no approval required ∧ not critical (per service, in the plan)
        var unattended = ApprovalRules.IsUnattended(!manual, sched?.Mode, plan);

        // M-02: a refused start-now must not leave an approvable orphan run behind
        if (req.StartNow)
        {
            await EnsureNoRunActiveAsync(ct);
        }

        await runs.InsertAsync(Row(RunStatuses.AwaitingApproval, null, now + (long)config.ApprovalExpiry.TotalMilliseconds), ct);
        await AuditAsync(actor, "maintenance.run.create", req.Project, new { run = id, planHash = plan.PlanHash, unattended });
        if (req.StartNow || unattended)
        {
            await ApproveAsync(id, plan.PlanHash, unattended && !req.StartNow ? new Actor($"schedule:{sched!.ScheduleName}", null) : actor, ct);
        }
        else
        {
            notify.Notify(new NotificationEvent(
                "maintenance.awaiting_approval",
                "info",
                $"Update awaiting approval: {req.Project}",
                $"{string.Join(", ", plan.Services.Where(s => s.IsUpdate).Select(s => s.Service))} can be updated. Review the plan and approve it (expires {Domain.Registry.ImageReferences.Iso(now + (long)config.ApprovalExpiry.TotalMilliseconds)}).",
                $"approval:{id}",
                null,
                new Dictionary<string, object?> { ["run"] = id, ["planHash"] = plan.PlanHash }));
        }

        return await GetAsync(id, ct);
    }

    /// <summary>Approves exactly the plan the human saw (<paramref name="planHash"/>), then starts it.</summary>
    public async Task<RunDetailDto> ApproveAsync(string id, string planHash, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var run = await runs.GetAsync(id, ct) ?? throw AppException.NotFound("Run");
        var meta = await runs.GetMetaAsync(id, ct) ?? throw AppException.NotFound("Run");
        if (run.Info.Status != RunStatuses.AwaitingApproval)
        {
            throw AppException.Conflict($"Run is {run.Info.Status}, not awaiting approval");
        }

        if (ApprovalRules.IsExpired(meta.ApprovalExpiresAt, Now()))
        {
            await ExpireApprovalsAsync(ct);
            throw AppException.Conflict("The approval request has expired; create a new run");
        }

        if (!ApprovalRules.Matches(run.Plan?.PlanHash, planHash))
        {
            throw new AppException(409, ErrorCode.ApprovalStale, "The plan changed since you reviewed it; review the current plan and approve again");
        }

        if (gateway.Policy.ReadOnlyReason is { } ro)
        {
            throw ReadOnly(ro);
        }

        if (meta.WindowId is { } wid && !(meta.Options["ignoreWindow"] is JsonValue v && v.TryGetValue<bool>(out var ignore) && ignore))
        {
            var w = await windows.GetAsync(wid, ct);
            if (w is null || TimeMath.WindowAt(w.Def, Now()) is null)
            {
                throw new AppException(409, ErrorCode.WindowClosed, "The maintenance window is closed; approve inside the window or send ignoreWindow when creating the run");
            }
        }

        await EnsureNoRunActiveAsync(ct);
        if (!await runs.ApproveAsync(id, actor.Name, planHash, Now(), ct))
        {
            throw AppException.Conflict("Run is no longer awaiting approval");
        }

        await AuditAsync(actor, "maintenance.run.approve", run.Info.Target, new { run = id, approvedPlanHash = planHash });
        try
        {
            await operations.StartAsync(
                new OperationStart(
                    "maintenance.update",
                    run.Info.Target,
                    actor.Name,
                    new { run = id, planHash },
                    LockKey,
                    async ctx =>
                    {
                        var status = await executor.ExecuteAsync(id, ctx, CancellationToken.None);
                        if (status is not (RunStatuses.Completed or RunStatuses.Partial or RunStatuses.Skipped))
                        {
                            throw new InvalidOperationException($"run {status}");
                        }
                    },
                    Id: id),
                ct);
        }
        catch (AppException e)
        {
            // lost the race for the single-run lock after the approval was recorded: never leave an approved-but-orphaned run
            await runs.FinishAsync(id, RunStatuses.Blocked, null, null, e.Message, Now(), CancellationToken.None);
            throw;
        }

        return await GetAsync(id, ct);
    }

    public async Task<RunDetailDto> RejectAsync(string id, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var run = await runs.GetAsync(id, ct) ?? throw AppException.NotFound("Run");
        if (run.Info.Status != RunStatuses.AwaitingApproval)
        {
            throw AppException.Conflict($"Run is {run.Info.Status}, not awaiting approval");
        }

        await runs.FinishAsync(id, RunStatuses.Rejected, null, null, $"rejected by {actor.Name}", Now(), ct);
        await AuditAsync(actor, "maintenance.run.reject", run.Info.Target, new { run = id });
        return await GetAsync(id, ct);
    }

    /// <summary>One-click restore of the recovery points of a finished run (RT-14); admin-confirmed, never automatic.</summary>
    public async Task<OperationStartedDto> RestoreAsync(string id, RestoreBody body, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(actor);
        var run = (await runs.GetAsync(id, ct) ?? throw AppException.NotFound("Run")).Info;
        if (RunStatuses.IsActive(run.Status))
        {
            throw AppException.Conflict($"Run is {run.Status}; restore applies to finished runs");
        }

        if (run.Kind != "update")
        {
            throw AppException.BadRequest("Only update runs have recovery points");
        }

        if (run.PlanHash is null)
        {
            throw AppException.Conflict("Standalone runs are restored by hand: the previous container is kept stopped under its -qm-old-<time> name (see the run's reason for the commands)");
        }

        if (body.Confirm != run.Target)
        {
            throw new AppException(409, ErrorCode.ConfirmationRequired, $"Confirm by sending {{\"confirm\":\"{run.Target}\"}}", new { expected = run.Target });
        }

        if (gateway.Policy.ReadOnlyReason is { } ro)
        {
            throw ReadOnly(ro);
        }

        await EnsureNoRunActiveAsync(ct);
        var points = (await rollback.ForRunAsync(id, ct)).Where(p => body.Services is null or { Count: 0 } || body.Services.Contains(p.Service ?? string.Empty)).ToList();
        if (points.Count == 0)
        {
            throw AppException.Conflict("This run has no recovery points to restore");
        }

        await AuditAsync(actor, "maintenance.run.restore", run.Target, new { run = id, services = body.Services is { Count: > 0 } ? (object)body.Services : "all" });
        var opId = await operations.StartAsync(
            new OperationStart(
                "maintenance.restore",
                run.Target,
                actor.Name,
                new { run = id, services = body.Services },
                LockKey,
                ctx => executor.RestoreAsync(id, body.Services, actor, ctx, CancellationToken.None)),
            ct);
        return new OperationStartedDto(opId);
    }

    private Task AuditAsync(Actor actor, string action, string target, object parameters) =>
        gateway.RecordAsync(new AuditScope(actor, action, target, null), AuditOutcome.Success, parameters, null, CancellationToken.None);
}

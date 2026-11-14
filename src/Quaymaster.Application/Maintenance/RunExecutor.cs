using System.Text.Json;
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
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Updates;

namespace Quaymaster.Application.Maintenance;

/// <summary>The state machine of spec 7.5, run for one approved plan.</summary>
public interface IRunExecutor
{
    /// <summary>Executes an <c>approved</c> run. Returns the final run status.</summary>
    Task<string> ExecuteAsync(string runId, IOperationContext ctx, CancellationToken ct);

    /// <summary>One-click restore (RT-14): puts the recorded previous image back for the run's services and re-checks them.</summary>
    Task RestoreAsync(string runId, IReadOnlyList<string>? only, Actor actor, IOperationContext ctx, CancellationToken ct);
}

/// <summary>
/// Test seam: called after each persisted step event. A hook may throw <see cref="ProcessCrashedException"/> to emulate a killed
/// process (the database is then left exactly as it was).
/// </summary>
public sealed class RunHooks
{
    public Func<string, string, string?, string, Task>? OnStep { get; set; }
}

/// <summary>Thrown only by test hooks to emulate a killed process: nothing is cleaned up, no final status is written.</summary>
public sealed class ProcessCrashedException() : Exception("simulated process crash");

public sealed partial class RunExecutor(
    IRunStore runs,
    IRollbackStore rollback,
    IStagedStore staged,
    IQuarantineService quarantine,
    IImageOps images,
    IPlanService planner,
    IRunEnvironment env,
    HealthGate health,
    IWindowStore windows,
    INotificationSink notify,
    IMutationGateway gateway,
    IMaintenanceGate gate,
    IUpdateService updates,
    RunHooks hooks,
    QmConfig config,
    TimeProvider time) : IRunExecutor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Actor SystemActor = new("system", null);

    /// <summary>Unwinds the state machine with a final status.</summary>
    private sealed class RunStop(string status, string message, string code) : Exception(message)
    {
        public string Status { get; } = status;

        public string Code { get; } = code;
    }

    private sealed record ServiceRun(string Outcome, string Detail, IReadOnlyList<string>? Guidance = null);

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    private static JsonObject Obj(object? o) => (o is null ? null : JsonSerializer.SerializeToNode(o, Json) as JsonObject) ?? [];

    public async Task<string> ExecuteAsync(string runId, IOperationContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var detail = await runs.GetAsync(runId, ct) ?? throw AppException.NotFound("Run");
        var plan = detail.Plan ?? throw AppException.NotFound("Run plan");
        var info = detail.Info;
        var meta = await runs.GetMetaAsync(runId, ct) ?? throw AppException.NotFound("Run");
        if (info.Status != RunStatuses.Approved)
        {
            throw AppException.Conflict($"Run is {info.Status}, not approved");
        }

        if (!await runs.MarkRunningAsync(runId, Now(), ct))
        {
            throw AppException.Conflict("Run is no longer approved");
        }

        using var owner = gate.Own(plan.Target);
        var steps = new StepRunner(this, runId);
        var outcomes = new List<ServiceResult>();
        var guidance = new List<string>();
        void Log(string text) => ctx.Log(text + "\n");
        Task<T> Step<T>(string name, string? service, Func<Task<(T Value, JsonObject? Detail, bool Failed)>> fn) => steps.RunAsync(name, service, fn);

        var status = RunStatuses.Completed;
        string? reason = null;
        try
        {
            // ---- 1. preflight ------------------------------------------------------------------------------------------------
            var names = plan.Services.Where(s => s.IsUpdate).Select(s => s.Service).ToList();
            PlanRequest Replan() => new(
                plan.Target,
                names,
                OptionText(meta.Options, "scheduleMode"),
                OptionList(meta.Options, "onlyModes"),
                OptionBool(meta.Options, "applyDrift"),
                OptionBool(meta.Options, "allowUnhealthy"),
                true);
            var (fresh, windowEnd) = await Step("preflight", null, async () =>
            {
                if (!ApprovalRules.Matches(info.ApprovedPlanHash, plan.PlanHash))
                {
                    throw new RunStop(RunStatuses.ApprovalStale, "The approved plan hash does not match the stored plan", "approval_stale");
                }

                if (gateway.Policy.ReadOnlyReason is { } ro)
                {
                    throw new RunStop(RunStatuses.Blocked, $"Read-only mode: {ro}", "read_only");
                }

                long? end = null;
                if (meta.WindowId is { } wid && !OptionBool(meta.Options, "ignoreWindow"))
                {
                    var w = await windows.GetAsync(wid, ct);
                    var span = w is { Enabled: true } ? TimeMath.WindowAt(w.Def, Now()) : null;
                    if (span is null)
                    {
                        throw new RunStop(RunStatuses.Skipped, "The maintenance window is closed; the run did not start", "window_closed");
                    }

                    end = span.End;
                    await runs.SetWindowEndAsync(runId, span.End, CancellationToken.None);
                }

                var p = await planner.PlanProjectAsync(Replan(), ct);
                return ((p, end), Obj(new { planHash = p.PlanHash, checks = p.Preflight.Checks }), false);
            });

            // ---- 2. validate (+ RT-01 hash gate) -----------------------------------------------------------------------------
            await Step("validate", null, () =>
            {
                if (fresh.Blocked is { } b && b.Code is "validation" or "blocked_drift" or "cycle" or "limited_capability" or "refused" or "preflight")
                {
                    throw new RunStop(RunStatuses.Blocked, b.Message, b.Code);
                }

                if (fresh.PlanHash != plan.PlanHash)
                {
                    throw new RunStop(RunStatuses.ApprovalStale, $"The plan changed since it was approved: {PlanHasher.DescribeDiff(plan, fresh)}", "approval_stale");
                }

                if (fresh.Blocked is { } other)
                {
                    throw new RunStop(RunStatuses.Blocked, other.Message, other.Code);
                }

                return Task.FromResult((true, (JsonObject?)Obj(new { warnings = fresh.Validation.Warnings, drift = fresh.Drift }), false));
            });

            // ---- 3. check ----------------------------------------------------------------------------------------------------
            await Step("check", null, () => Task.FromResult((true, (JsonObject?)Obj(new
            {
                digests = plan.Services.Where(s => s.IsUpdate).Select(s => new { service = s.Service, from = s.FromDigest, to = s.ToDigest }),
            }), false)));

            var target = await env.TargetAsync(plan.Target, ct);

            // ---- 4. recovery point (tag the running image + config snapshot) ---------------------------------------------------
            await Step("recovery_point", null, async () =>
            {
                var snapshot = await env.SnapshotAsync(plan.Target, ct);
                var all = await env.ContainersAsync(ct);
                foreach (var s in plan.Services)
                {
                    var live = all.Where(c => c.Project == plan.Target && c.Service == s.Service).ToList();

                    // the rollback image ID comes from the RUNNING container, never from the tag (RT-06);
                    // L-6: with replicas on mixed images EVERY container must still run the planned image
                    if (live.Count == 0 || live.Any(c => c.ImageId != s.FromImageId))
                    {
                        throw new RunStop(RunStatuses.ApprovalStale, $"{s.Service} no longer runs the planned image", "approval_stale");
                    }

                    var tag = ImageReferences.RollbackTag(plan.Target, s.Service, runId);
                    await images.TagAsync(s.FromImageId, tag, ct);
                    var now = Now();
                    await rollback.PutAsync(
                        new RollbackPoint(tag, runId, plan.Target, s.Service, (s.Containers.Count > 0 ? s.Containers[0].Name : null), s.Ref, s.FromImageId, now, now + (long)config.RollbackRetention.TotalMilliseconds, false),
                        ct);
                }

                Log($"recovery points tagged for: {string.Join(", ", plan.Order)}");
                return (true, (JsonObject?)snapshot, false);
            });

            // ---- 5. pull (by digest, staged; the user-visible tag does not move) ---------------------------------------------
            var stagedIds = new Dictionary<string, string>(StringComparer.Ordinal);
            await Step("pull", null, async () =>
            {
                var pulled = new JsonArray();
                foreach (var s in plan.Services.Where(x => x.IsUpdate))
                {
                    var image = ImageReferences.Parse(s.Ref);
                    if (image is null || s.ToDigest is null)
                    {
                        throw new RunStop(RunStatuses.Failed, $"cannot pull {s.Ref}", "pull_failed");
                    }

                    var tag = ImageReferences.StagedTag(image, s.ToDigest);
                    var id = (await images.InspectAsync(tag, ct))?["Id"]?.GetValue<string>();
                    if (id is null)
                    {
                        Log($"pulling {ImageReferences.EngineName(image)}@{s.ToDigest}");
                        try
                        {
                            id = (await images.PullByDigestAsync(image, s.ToDigest, Log, ct)).ImageId;
                        }
                        catch (Exception e) when (e is not ProcessCrashedException and not OperationCanceledException)
                        {
                            throw new RunStop(
                                RunStatuses.Failed,
                                $"Pull failed: {e.Message}. Nothing was restarted, recreated or retagged; the services are untouched.",
                                "pull_failed");
                        }
                    }

                    stagedIds[s.Service] = id;
                    pulled.Add(Obj(new { @ref = s.Ref, digest = s.ToDigest, stagedTag = tag }));
                }

                return (true, (JsonObject?)new JsonObject { ["pulled"] = pulled }, false);
            });

            // ---- 6. impact ---------------------------------------------------------------------------------------------------
            await Step("impact", null, () => Task.FromResult((true, (JsonObject?)Obj(new { impact = plan.Impact, order = plan.Order }), false)));

            // ---- 7-9. deploy → health → confirm / rollback, one service at a time ------------------------------------------
            var pending = new HashSet<string>(plan.Order, StringComparer.Ordinal);
            async Task SkipRest(string why, string stepReason)
            {
                foreach (var rest in plan.Services.Where(x => pending.Contains(x.Service)))
                {
                    outcomes.Add(new ServiceResult(rest.Service, ServiceOutcomes.Skipped, why));
                    var sid = await runs.StartStepAsync(runId, steps.Next(), rest.Service, "deploy", Now(), CancellationToken.None);
                    await runs.FinishStepAsync(sid, "skipped", Obj(new { reason = stepReason }), Now(), CancellationToken.None);
                }

                pending.Clear();
            }

            bool AnyUpdated() => outcomes.Any(o => o.Outcome is ServiceOutcomes.Updated or ServiceOutcomes.DependentRecreated);
            foreach (var s in plan.Services)
            {
                // stop conditions are evaluated BEFORE starting a service; health / rollback below never are (RT-09)
                if (ctx.ShuttingDown)
                {
                    await SkipRest("Quaymaster is shutting down; the service was not started", "shutdown");
                    status = ExecutionRules.StopStatus(AnyUpdated());
                    reason = "Quaymaster is shutting down; the remaining services were skipped (nothing was interrupted)";
                    break;
                }

                if (!ExecutionRules.FitsWindow(windowEnd, Now(), s.Health.TimeoutSeconds))
                {
                    await SkipRest("the maintenance window closes before it could finish", "window closes");
                    status = ExecutionRules.StopStatus(AnyUpdated());
                    reason = "The maintenance window closed; the remaining services were skipped";
                    break;
                }

                await AssertNoConflictAsync(plan, pending, ct);

                // RT-01: the hash gate runs again right before EVERY recreate, not only at preflight
                if (await DriftBeforeAsync(plan.Target, s, ct) is { } drift)
                {
                    await SkipRest($"configuration drift detected during the run ({drift})", "drift");
                    status = AnyUpdated() ? RunStatuses.Partial : RunStatuses.Blocked;
                    reason = $"{s.Service}: {drift}. The run stopped before recreating it; nothing was rolled back.";
                    guidance.Add("The Compose configuration changed while the run was in progress. Review the change, then create a new run (with applyDrift to apply it deliberately).");
                    notify.Notify(new NotificationEvent(
                        "run.failed", "critical", $"Maintenance run stopped: {plan.Target}/{s.Service} changed during the run", reason, $"drift:{runId}:{s.Service}"));
                    break;
                }

                pending.Remove(s.Service);
                var res = await DeployOneAsync(runId, plan, s, target, stagedIds.GetValueOrDefault(s.Service), steps, Log, ctx, ct);
                outcomes.Add(new ServiceResult(s.Service, res.Outcome, res.Detail));
                if (res.Guidance is not null)
                {
                    guidance.AddRange(res.Guidance);
                }

                if (res.Outcome is ServiceOutcomes.Updated or ServiceOutcomes.DependentRecreated)
                {
                    continue;
                }

                // failure: stop the rollout, the remaining services are skipped
                status = RollbackRules.RunStatusFor(res.Outcome);
                reason = res.Detail;
                await SkipRest($"rollout stopped after {s.Service} failed", $"rollout stopped after {s.Service}");
                break;
            }
        }
        catch (ProcessCrashedException)
        {
            throw; // leaves the database exactly as a killed process would
        }
        catch (RunStop e)
        {
            status = e.Status;
            reason = e.Message;
            if (e.Status == RunStatuses.ApprovalStale)
            {
                var fresh = await TryReplanAsync(new PlanRequest(
                    plan.Target,
                    plan.Services.Where(s => s.IsUpdate).Select(s => s.Service).ToList(),
                    OptionText(meta.Options, "scheduleMode"),
                    OptionList(meta.Options, "onlyModes"),
                    OptionBool(meta.Options, "applyDrift"),
                    false,
                    true));
                if (fresh is { Blocked: null })
                {
                    var newId = await RequestAgainAsync(info, meta, fresh);
                    reason = $"{e.Message}. A new approval request was created ({newId}).";
                }
            }

            if (e.Code == "pull_failed")
            {
                guidance.Add("Nothing was changed. Fix the registry / network problem and run the update again.");
            }

            if (e.Code == "blocked_drift")
            {
                guidance.Add("Review the configuration drift, then re-create the run with applyDrift to apply it deliberately.");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            status = RunStatuses.Failed;
            reason = $"Unexpected error: {e.Message}";
        }

        return await RecordAsync(runId, info, plan, status, reason, outcomes, guidance, ctx);
    }

    // ---- one service ----------------------------------------------------------------------------------------------------------

    private async Task<ServiceRun> DeployOneAsync(
        string runId,
        Plan plan,
        PlanService s,
        ProjectTarget target,
        string? stagedImageId,
        StepRunner step,
        Action<string> log,
        IOperationContext ctx,
        CancellationToken ct)
    {
        // The write-ahead intent opens before the first side effect and completes only when the service outcome is final (healthy,
        // rolled back and re-verified, or left as is). A crash in between leaves it open so recovery can compare the live state
        // with from / to (RT-14).
        var intent = await runs.AddIntentAsync(
            new NewIntent(runId, s.Service, "deploy", s.FromImageId, s.IsUpdate ? stagedImageId : null, [.. s.Containers.Select(c => c.Id)]),
            Now(),
            CancellationToken.None);
        try
        {
            var res = await DeployInnerAsync(runId, plan, s, target, stagedImageId, step, log, ctx, ct);
            await runs.CompleteIntentAsync(intent, Now(), CancellationToken.None);
            return res;
        }
        catch (ProcessCrashedException)
        {
            throw;
        }
        catch
        {
            await runs.CompleteIntentAsync(intent, Now(), CancellationToken.None);
            throw;
        }
    }
}

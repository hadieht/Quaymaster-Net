using System.Text.Json.Nodes;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;

namespace Quaymaster.Application.Maintenance;

public sealed partial class RunExecutor
{
    /// <summary>Persists every step (start before the work, outcome after) in order, and fires the test hook.</summary>
    private sealed class StepRunner(RunExecutor x, string runId, int start = 0)
    {
        private int _seq = start;

        public int Next() => ++_seq;

        public async Task<T> RunAsync<T>(string name, string? service, Func<Task<(T Value, JsonObject? Detail, bool Failed)>> fn)
        {
            var id = await x.Runs.StartStepAsync(runId, Next(), service, name, x.Now(), CancellationToken.None);
            await x.Hook(runId, name, service, "start");
            try
            {
                var (value, d, failed) = await fn();
                await x.Runs.FinishStepAsync(id, failed ? "failed" : "succeeded", d, x.Now(), CancellationToken.None);
                await x.Hook(runId, name, service, "end");
                return value;
            }
            catch (ProcessCrashedException)
            {
                throw;
            }
            catch (Exception e)
            {
                await x.Runs.FinishStepAsync(id, e is RunStop { Status: RunStatuses.Skipped } ? "skipped" : "failed", Obj(new { error = e.Message }), x.Now(), CancellationToken.None);
                throw;
            }
        }
    }

    private IRunStore Runs => runs;

    private Task Hook(string runId, string step, string? service, string phase) => hooks.OnStep is { } h ? h(runId, step, service, phase) : Task.CompletedTask;

    private static string? OptionText(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool OptionBool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static IReadOnlyList<string>? OptionList(JsonObject o, string key) =>
        o[key] is JsonArray a ? [.. a.Select(n => n?.GetValue<string>() ?? string.Empty)] : null;

    private static HealthSpec SpecFor(PlanService s, string expectedImageId) =>
        new(s.Health.Kind, (long)(s.Health.TimeoutSeconds * 1000), (long)(s.Health.StabilizationSeconds * 1000), s.Health.Probe, expectedImageId);

    private static string Short19(string id) => RecoveryCommands.ShortId(id);

    private async Task<IReadOnlyList<EngineContainer>> ServiceContainersAsync(string project, string service, CancellationToken ct) =>
        [.. (await env.ContainersAsync(ct)).Where(c => c.Project == project && c.Service == service)];

    /// <summary>
    /// The service's container set after a compose up: the daemon may still list the container compose just replaced
    /// (stopped, pending removal). The health gate must see only what this deploy brought up — everything else is the
    /// previous generation. If filtering leaves fewer containers than the deployment is expected to have (no recreate,
    /// or a lagging daemon), the full list is kept so the gate fails against what is actually there, not against a
    /// partial set.
    /// </summary>
    /// <summary>
    /// F-05: the containers this deploy brought up. Reads fresh (never a list fetched before the mutation) and waits a bounded time
    /// until as many NEW ids as expected are listed; only then falls back to what is actually there.
    /// </summary>
    private async Task<IReadOnlyList<EngineContainer>> NewGenerationAsync(string project, string service, IReadOnlyCollection<string> previousIds, int expectedCount, CancellationToken ct)
    {
        IReadOnlyList<EngineContainer> all = [];
        for (var attempt = 0; attempt < PostDeployListAttempts; attempt++)
        {
            all = await ServiceContainersAsync(project, service, ct);
            var previous = previousIds.ToHashSet(StringComparer.Ordinal);
            if (all.Count(c => !previous.Contains(c.Id)) >= expectedCount)
            {
                break;
            }

            if (attempt < PostDeployListAttempts - 1)
            {
                await Task.Delay(PostDeployListDelay, ct);
            }
        }

        return PostDeployLive(previousIds, expectedCount, all);
    }

    private const int PostDeployListAttempts = 4;

    private static readonly TimeSpan PostDeployListDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>F-06: a Docker / network error while verifying is a failed check (rollback + guidance), never an unhandled crash.</summary>
    private static bool IsDockerError(Exception e) => e is AppException or HttpRequestException or IOException or TimeoutException;

    private static IReadOnlyList<EngineContainer> PostDeployLive(IReadOnlyCollection<string> previousIds, int expectedCount, IReadOnlyList<EngineContainer> all)
    {
        var previous = previousIds.ToHashSet(StringComparer.Ordinal);
        var live = all.Where(c => !previous.Contains(c.Id)).ToList();
        return live.Count >= expectedCount ? live : all;
    }

    private async Task<Plan?> TryReplanAsync(PlanRequest request)
    {
        try
        {
            return await planner.PlanProjectAsync(request, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ProcessCrashedException)
        {
            // F-07: a failed re-plan (a registry 429 is a RegistryException, not an AppException) only means "no new request"
            return null;
        }
    }

    // ---- deploy → health → confirm / rollback for one service -------------------------------------------------------------------

    private async Task<ServiceRun> DeployInnerAsync(
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
        var repoTag = RecoveryCommands.RepoTagOf(s.Ref) ?? s.Ref;
        var image = ImageReferences.Parse(s.Ref);
        var isUpdate = s.IsUpdate;
        var expected = s.FromImageId;
        if (isUpdate)
        {
            if (stagedImageId is null)
            {
                return new ServiceRun(ServiceOutcomes.Failed, "staged image missing");
            }

            expected = stagedImageId;
        }

        // ---- 7. deploy (the user-visible tag moves only here, RT-07) -------------------------------------------------------------
        var deploy = await step.RunAsync("deploy", s.Service, async () =>
        {
            try
            {
                if (isUpdate)
                {
                    await images.TagAsync(expected, repoTag, ct);
                }
                else
                {
                    expected = (await images.InspectAsync(s.Ref, ct))?["Id"]?.GetValue<string>() ?? s.FromImageId;
                }

                log($"deploying {s.Service}{(s.WasRunning ? string.Empty : " (stays stopped: --no-start)")}{(isUpdate ? string.Empty : " (namespace dependent: recreated with its provider)")}");
                var r = await env.UpAsync(plan.Target, s.Service, !isUpdate, !s.WasRunning, log, ct);
                await gateway.RecordAsync(
                    new AuditScope(SystemActor, "maintenance.deploy", $"{plan.Target}/{s.Service}", null),
                    r.Ok ? AuditOutcome.Success : AuditOutcome.Failure,
                    new { run = runId, image = Short19(expected), argv = r.Argv },
                    null,
                    CancellationToken.None);
                return r.Ok
                    ? ((Ok: true, Why: string.Empty), (JsonObject?)Obj(new { argv = r.Argv }), false)
                    : ((Ok: false, Why: r.Detail), (JsonObject?)Obj(new { code = r.Code }), true);
            }
            catch (Exception e) when (e is not ProcessCrashedException and not OperationCanceledException)
            {
                return ((Ok: false, Why: e.Message), (JsonObject?)Obj(new { error = e.Message }), true);
            }
        });

        // the intent stays open through health and rollback: a crash before the outcome is final must be reported truthfully

        // ---- 8. health (always completes, regardless of the window) ---------------------------------------------------------------
        var healthy = false;
        var why = deploy.Why;
        if (deploy.Ok)
        {
            try
            {
                var live = await NewGenerationAsync(plan.Target, s.Service, [.. s.Containers.Select(c => c.Id)], s.Containers.Count, ct);
                if (!s.WasRunning)
                {
                    // stays stopped: only assert that it was recreated on the expected image
                    healthy = live.Count > 0 && live.All(c => c.ImageId == expected);
                    why = healthy ? string.Empty : "the recreated container does not use the expected image";
                    await step.RunAsync("health", s.Service, () => Task.FromResult((true, (JsonObject?)Obj(new { skipped = "service was stopped; stays stopped", ok = healthy }), false)));
                }
                else
                {
                    var r = await step.RunAsync("health", s.Service, async () =>
                    {
                        var res = await health.WaitAsync([.. live.Select(c => c.Id)], SpecFor(s, expected), ct);
                        return (res, (JsonObject?)Obj(new { ok = res.Ok, reason = res.Reason, ms = res.ElapsedMs }), !res.Ok);
                    });
                    healthy = r.Ok;
                    why = r.Reason;

                    // RT-29: dependents of a recreated service are re-verified
                    if (healthy && isUpdate && await VerifyDependentsAsync(plan, s, step, ct) is { } dep)
                    {
                        return new ServiceRun(
                            ServiceOutcomes.Failed,
                            $"{s.Service} is healthy but its dependent \"{dep}\" is not; the rollout was stopped and nothing was rolled back automatically",
                            [$"Check the dependent service \"{dep}\" (docker compose logs). {s.Service} runs the new image."]);
                    }
                }
            }
            catch (Exception e) when (IsDockerError(e))
            {
                healthy = false;
                why = $"the health check could not complete (Docker error: {e.Message})";
            }
        }

        if (healthy)
        {
            if (isUpdate && image is not null && s.ToDigest is not null)
            {
                await CleanStagedAsync(image, s.ToDigest, stagedImageId, log, ct);
            }

            return new ServiceRun(
                isUpdate ? ServiceOutcomes.Updated : ServiceOutcomes.DependentRecreated,
                isUpdate ? $"now on {Short19(s.ToDigest ?? string.Empty)}" : "recreated");
        }

        // ---- 9. failure: quarantine, then rollback or exact manual recovery ---------------------------------------------------------
        var decision = RollbackRules.Decide(isUpdate, deploy.Ok, false, s.AutoRollback);
        if (decision.Quarantine && image is not null && s.ToDigest is not null
            && await quarantine.AddAsync(ImageReferences.CacheKey(image), s.ToDigest, runId, why, ct))
        {
            notify.Notify(new NotificationEvent(
                "run.failed",
                "warning",
                $"Update quarantined: {plan.Target}/{s.Service}",
                $"The image {Short19(s.ToDigest)} failed its health gate and will not be retried by unattended runs until the tag moves or you clear the quarantine.",
                $"quarantine:{ImageReferences.CacheKey(image)}:{s.ToDigest}",
                24 * 3_600_000L));
        }

        var manual = RecoveryCommands.ManualRestore(target, s, repoTag);

        // RT-07 / F-08: the update did not stick, so the shared tag must not keep pointing at the failing image
        // (the next `compose up` of any project using it would deploy it again)
        var tagRestored = false;
        if (decision.RestoreTag)
        {
            try
            {
                await images.TagAsync(s.FromImageId, repoTag, ct);
                tagRestored = true;
            }
            catch (Exception e) when (e is AppException or InvalidOperationException)
            {
                tagRestored = false;
            }
        }

        if (!decision.AttemptRollback)
        {
            await AlertLeftAsync(runId, plan, s, why, ct);
            return new ServiceRun(
                ServiceOutcomes.FailedLeftAsIs,
                $"{s.Service} failed its health gate ({why}). {(isUpdate ? $"Automatic rollback is off for it ({s.AutoRollbackReason}); it was left as is{(tagRestored ? $", but the tag {repoTag} was put back on the previous image so a later \"compose up\" does not redeploy the failing one" : string.Empty)}." : "Left as is.")}",
                manual);
        }

        var (rbOk, rbWhy) = await RollbackServiceAsync(runId, plan, s, repoTag, step, log, ct);
        if (rbOk)
        {
            return new ServiceRun(RollbackRules.AfterRollback(true), $"{s.Service} failed its health gate ({why}) and was rolled back to the previous image, which is healthy again");
        }

        await AlertLeftAsync(runId, plan, s, $"{why}; the automatic rollback also failed: {rbWhy}", ct);
        return new ServiceRun(RollbackRules.AfterRollback(false), $"{s.Service} failed its health gate ({why}) and the automatic rollback also failed: {rbWhy}", manual);
    }

    /// <summary>The staged alias can go once the user's tag holds the image (an untag, never an image delete).</summary>
    private async Task CleanStagedAsync(ImageRef image, string digest, string? stagedImageId, Action<string> log, CancellationToken ct)
    {
        var tag = ImageReferences.StagedTag(image, digest);
        try
        {
            await staged.DeleteAsync(tag, ct);
            await images.RemoveAsync(tag, ct);

            // some engines drop the registry digest record together with the alias; without it the next check would report
            // "local_only". Re-pulling by digest is a manifest fetch only (the layers are present).
            var left = await images.InspectAsync(stagedImageId ?? tag, ct);
            if (left is not null && !(left["RepoDigests"] as JsonArray ?? []).Any(d => d?.GetValue<string>().EndsWith("@" + digest, StringComparison.Ordinal) == true))
            {
                _ = await images.PullByDigestAsync(image, digest, null, ct);
            }
        }
        catch (Exception e) when (e is AppException or InvalidOperationException)
        {
            log($"could not clean the staged alias {tag}: {e.Message}");
        }
    }

    /// <summary>A service the rollout could not bring back healthy: critical alert (it may be down).</summary>
    private async Task AlertLeftAsync(string runId, Plan plan, PlanService s, string why, CancellationToken ct)
    {
        IReadOnlyList<EngineContainer> live;
        try
        {
            live = await ServiceContainersAsync(plan.Target, s.Service, ct);
        }
        catch (Exception e) when (IsDockerError(e))
        {
            live = []; // cannot tell: alert as down (the safe reading)
        }

        var down = live.Count == 0 || live.Any(c => c.State != "running");
        notify.Notify(new NotificationEvent(
            "service.left_down",
            "critical",
            $"Service left {(down ? "down" : "unhealthy")} after a failed update: {plan.Target}/{s.Service}",
            $"{s.Service} {(down ? "is not running" : "is running but failed its health gate")} ({why}). It was not rolled back automatically. Recovery: see run {runId}.",
            $"leftdown:{runId}:{s.Service}",
            null,
            new Dictionary<string, object?> { ["run"] = runId, ["service"] = s.Service, ["project"] = plan.Target }));
    }

    /// <summary>Re-tags the previous image and redeploys; asserts the image and re-runs the health gate (RT-06).</summary>
    private async Task<(bool Ok, string Why)> RollbackServiceAsync(string runId, Plan plan, PlanService s, string repoTag, StepRunner step, Action<string> log, CancellationToken ct)
    {
        var point = await rollback.GetAsync(ImageReferences.RollbackTag(plan.Target, s.Service, runId), ct);
        var oldId = point?.ImageId ?? s.FromImageId;
        IReadOnlyList<EngineContainer> before;
        try
        {
            before = await ServiceContainersAsync(plan.Target, s.Service, ct);
        }
        catch (Exception e) when (IsDockerError(e))
        {
            before = [];
        }

        var intent = await runs.AddIntentAsync(new NewIntent(runId, s.Service, "rollback", null, oldId, [.. before.Select(c => c.Id)]), Now(), CancellationToken.None);
        var result = await step.RunAsync("rollback", s.Service, async () =>
        {
            try
            {
                // RT-01: a rollback is a recreate too; it must not apply a configuration change made meanwhile
                if (await env.HasDriftAsync(plan.Target, s.Service, ct))
                {
                    return ((Ok: false, Why: "the Compose configuration changed during the run; the rollback would apply that change, so it was not done"), (JsonObject?)null, true);
                }

                await images.TagAsync(oldId, repoTag, ct);
                log($"rolling {s.Service} back to {Short19(oldId)}");
                var dep = await env.UpAsync(plan.Target, s.Service, false, !s.WasRunning, log, ct);
                await gateway.RecordAsync(
                    new AuditScope(SystemActor, "maintenance.rollback", $"{plan.Target}/{s.Service}", null),
                    dep.Ok ? AuditOutcome.Success : AuditOutcome.Failure,
                    new { run = runId, image = Short19(oldId) },
                    null,
                    CancellationToken.None);
                if (!dep.Ok)
                {
                    return ((Ok: false, Why: dep.Detail), (JsonObject?)null, true);
                }

                var live = await NewGenerationAsync(plan.Target, s.Service, [.. before.Select(c => c.Id)], before.Count, ct);
                if (!s.WasRunning)
                {
                    var same = live.All(c => c.ImageId == oldId);
                    return ((Ok: same, Why: "image check"), (JsonObject?)null, !same);
                }

                // after the rollback the image is asserted again and the health gate re-run (RT-06)
                var h = await health.WaitAsync([.. live.Select(c => c.Id)], SpecFor(s, oldId), ct);
                return ((Ok: h.Ok, Why: h.Reason), (JsonObject?)Obj(new { ok = h.Ok, reason = h.Reason }), !h.Ok);
            }
            catch (Exception e) when (e is not ProcessCrashedException and not OperationCanceledException)
            {
                return ((Ok: false, Why: e.Message), (JsonObject?)null, true);
            }
        });
        await runs.CompleteIntentAsync(intent, Now(), CancellationToken.None);
        return result;
    }

    /// <summary>RT-29: after a dependency was recreated, the services that depend on it must still be healthy. Returns the unhealthy one.</summary>
    private async Task<string?> VerifyDependentsAsync(Plan plan, PlanService s, StepRunner step, CancellationToken ct)
    {
        var inRun = plan.Services.Select(x => x.Service).ToHashSet(StringComparer.Ordinal);
        var deps = plan.Impact.Dependents.Where(d => d.Via == s.Service && d.Kind == "depends_on" && !inRun.Contains(d.Service)).Select(d => d.Service).Distinct(StringComparer.Ordinal).ToList();
        if (deps.Count == 0)
        {
            return null;
        }

        var edges = await env.EdgesAsync(plan.Target, ct);
        foreach (var dep in deps)
        {
            var live = (await ServiceContainersAsync(plan.Target, dep, ct)).Where(c => c.State is not ("exited" or "created")).ToList();
            if (live.Count == 0)
            {
                continue; // a stopped dependent is none of our business
            }

            var restart = edges.Any(e => e.From == dep && e.To == s.Service && e.Restart == true);

            async Task<bool> CheckAsync()
            {
                var first = await env.InspectAsync(live[0].Id, ct);
                if (first is null)
                {
                    return false;
                }

                var hasHc = first.Health is { } h && h != "none";
                var r = await health.WaitAsync([.. live.Select(c => c.Id)], new HealthSpec(hasHc ? "healthcheck" : "stability", 30_000, 3_000, null, first.Image), ct);
                return r.Ok;
            }

            var errors = new List<string>();
            var ok = await step.RunAsync("health", dep, async () =>
            {
                var good = await CheckAsync();
                if (!good && restart)
                {
                    foreach (var c in live)
                    {
                        if (await env.RestartAsync(c.Id, ct) is { } err)
                        {
                            errors.Add($"{c.Id[..12]}: {err}");
                        }
                    }

                    good = await CheckAsync();
                }

                return (good, (JsonObject?)Obj(new { dependentOf = s.Service, restartedBecauseDeclared = restart && good, restartErrors = errors.Count > 0 ? errors : null }), !good);
            });
            if (!ok)
            {
                return dep;
            }
        }

        return null;
    }

    /// <summary>RT-14: if another tool changed the run's containers meanwhile, abort WITHOUT rolling back.</summary>
    private async Task AssertNoConflictAsync(Plan plan, HashSet<string> pending, CancellationToken ct)
    {
        var all = await env.ContainersAsync(ct);
        foreach (var name in pending)
        {
            var s = plan.Services.FirstOrDefault(x => x.Service == name);
            if (s is null)
            {
                continue;
            }

            var live = all.Where(c => c.Project == plan.Target && c.Service == name).ToList();
            var ids = live.Select(c => c.Id).Order(StringComparer.Ordinal).ToList();
            var planned = s.Containers.Select(c => c.Id).Order(StringComparer.Ordinal).ToList();
            if (!ids.SequenceEqual(planned) || live.Any(c => c.ImageId != s.FromImageId))
            {
                notify.Notify(new NotificationEvent(
                    "run.conflict",
                    "critical",
                    $"Maintenance run aborted: {plan.Target}/{name} changed underneath it",
                    $"Another tool recreated or modified {name} while the run was in progress. The run stopped and did NOT roll anything back.",
                    $"conflict:{plan.Target}:{name}"));
                throw new RunStop(RunStatuses.Failed, $"Another tool changed {name} during the run (containers or image differ from the plan). The run stopped without rolling back.", "conflict");
            }
        }
    }

    /// <summary>RT-01: the service's current hash vs the one the approved plan was made against. Fails closed.</summary>
    private async Task<string?> DriftBeforeAsync(string project, PlanService s, CancellationToken ct)
    {
        if (s.ConfigHash is null)
        {
            return null;
        }

        try
        {
            var now = await env.ConfigHashAsync(project, s.Service, ct);
            return now is null
                ? "its configuration can no longer be resolved"
                : now == s.ConfigHash ? null : "its Compose configuration changed after the plan was approved";
        }
        catch (AppException e)
        {
            return $"its configuration hash could not be verified ({e.Message[..Math.Min(200, e.Message.Length)]})";
        }
    }

    /// <summary>RT-08: a stale approval is never executed; a new request is created for the new plan.</summary>
    private async Task<string> RequestAgainAsync(RunInfo old, RunMeta meta, Plan fresh)
    {
        var id = Guid.NewGuid().ToString();
        var now = Now();
        await runs.InsertAsync(
            new NewRun(
                id,
                "update",
                old.Trigger,
                old.Target,
                RunStatuses.AwaitingApproval,
                $"replaces {old.Id}, whose approved plan went stale",
                fresh,
                old.RequestedBy,
                meta.Options,
                now,
                old.ScheduleId,
                null,
                meta.WindowId,
                now + (long)config.ApprovalExpiry.TotalMilliseconds),
            CancellationToken.None);
        notify.Notify(new NotificationEvent(
            "maintenance.awaiting_approval",
            "warning",
            $"Approval needed again: {old.Target}",
            "What was approved no longer matches reality (a tag moved or the configuration changed). Review the new plan.",
            $"approval:{id}",
            null,
            new Dictionary<string, object?> { ["run"] = id, ["planHash"] = fresh.PlanHash }));
        return id;
    }

    // ---- 10. record & notify ----------------------------------------------------------------------------------------------------

    private async Task<string> RecordAsync(string runId, RunInfo run, Plan plan, string status, string? reason, List<ServiceResult> outcomes, List<string> guidance, IOperationContext ctx)
    {
        var summary = ExecutionRules.Summary(status, plan.Target, outcomes.Count(o => o.Outcome == ServiceOutcomes.Updated), reason);
        var step = await runs.StartStepAsync(runId, 9999, null, "record", Now(), CancellationToken.None);
        await runs.FinishStepAsync(step, "succeeded", Obj(new { status }), Now(), CancellationToken.None);
        ctx.ReleaseLock(); // the terminal status below is the signal that the next run may start
        await FinishWithRetryAsync(runId, status, new RunResult(outcomes, summary), guidance.Count > 0 ? guidance : null, reason);
        env.Invalidate();
        try
        {
            _ = await updates.StatusesAsync(false, CancellationToken.None);
        }
        catch (Exception e) when (e is AppException or HttpRequestException or InvalidOperationException)
        {
            // refreshing the update view is best effort
        }

        await gateway.RecordAsync(
            new AuditScope(new Actor(run.ApprovedBy ?? run.RequestedBy, null), "maintenance.run.finish", plan.Target, null),
            status is RunStatuses.Completed or RunStatuses.Partial or RunStatuses.Skipped ? AuditOutcome.Success : AuditOutcome.Failure,
            new { run = runId, status, planHash = plan.PlanHash, approvedPlanHash = run.ApprovedPlanHash, summary },
            null,
            CancellationToken.None);
        ctx.Log($"run {runId} finished: {status}{(reason is null ? string.Empty : " — " + reason)}\n");
        notify.Notify(NotificationFor(runId, plan.Target, status, summary, guidance));
        return status;
    }

    /// <summary>
    /// F-07: the terminal status is what frees the "a run is active" gate; a transient database error must not leave the run
    /// <c>running</c> forever, so the write is retried and never tied to a cancellation token.
    /// </summary>
    private async Task FinishWithRetryAsync(string runId, string status, RunResult result, IReadOnlyList<string>? guidance, string? reason)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await runs.FinishAsync(runId, status, result, guidance, reason, Now(), CancellationToken.None);
                return;
            }
            catch (Exception e) when (attempt < 5 && e is not ProcessCrashedException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), CancellationToken.None);
            }
        }
    }

    /// <summary>The notification event of a finished run (T5 maps these types to channels).</summary>
    public static NotificationEvent NotificationFor(string runId, string target, string status, string summary, IReadOnlyList<string> guidance)
    {
        ArgumentNullException.ThrowIfNull(guidance);
        var data = new Dictionary<string, object?> { ["run"] = runId, ["target"] = target, ["status"] = status };
        var key = $"run:{runId}";
        return status switch
        {
            RunStatuses.Completed => new NotificationEvent("run.succeeded", "info", $"Update completed: {target}", summary, key, null, data),
            RunStatuses.RolledBack => new NotificationEvent("run.rolled_back", "warning", $"Update rolled back: {target}", summary, key, null, data),
            RunStatuses.Partial => new NotificationEvent("run.partial", "warning", $"Update partially completed: {target}", summary, key, null, data),
            RunStatuses.Skipped => new NotificationEvent("run.skipped", "info", $"Update skipped: {target}", summary, key, null, data),
            _ => new NotificationEvent("run.failed", "critical", $"Update {status}: {target}", guidance.Count > 0 ? summary + "\n" + string.Join('\n', guidance) : summary, key, null, data),
        };
    }

    // ---- restore -----------------------------------------------------------------------------------------------------------------

    public async Task RestoreAsync(string runId, IReadOnlyList<string>? only, Actor actor, IOperationContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(ctx);
        var detail = await runs.GetAsync(runId, ct) ?? throw AppException.NotFound("Run");
        var plan = detail.Plan ?? throw AppException.NotFound("Run plan");
        var points = (await rollback.ForRunAsync(runId, ct)).Where(p => only is null or { Count: 0 } || only.Contains(p.Service ?? string.Empty)).ToList();
        if (points.Count == 0)
        {
            throw AppException.Conflict("This run has no recovery points to restore");
        }

        using var owner = gate.Own(plan.Target);
        var steps = new StepRunner(this, runId, 20_000);
        var failures = new List<string>();

        // reverse deployment order: dependents first, dependencies last
        var order = plan.Order.Reverse().ToList();
        foreach (var p in points.OrderBy(p => order.IndexOf(p.Service ?? string.Empty)))
        {
            var service = p.Service ?? string.Empty;
            var s = plan.Services.FirstOrDefault(x => x.Service == service);
            var repoTag = RecoveryCommands.RepoTagOf(p.Ref);
            if (s is null || repoTag is null)
            {
                continue;
            }

            var before = await ServiceContainersAsync(plan.Target, service, ct);
            var intent = await runs.AddIntentAsync(new NewIntent(runId, service, "rollback", null, p.ImageId, [.. before.Select(c => c.Id)]), Now(), CancellationToken.None);
            try
            {
                await steps.RunAsync("restore", service, async () =>
                {
                    if (await images.InspectAsync(p.ImageId, ct) is null)
                    {
                        throw new InvalidOperationException("the recovery image no longer exists locally");
                    }

                    // RT-01: restoring must not apply an unreviewed configuration change
                    if (await env.HasDriftAsync(plan.Target, service, ct))
                    {
                        throw new InvalidOperationException("the Compose configuration changed since the service was last deployed; restoring would apply that change. Review it and restore by hand");
                    }

                    await images.TagAsync(p.ImageId, repoTag, ct);
                    ctx.Log($"restoring {service} to {Short19(p.ImageId)}\n");
                    var dep = await env.UpAsync(plan.Target, service, false, !s.WasRunning, text => ctx.Log(text + "\n"), ct);
                    if (!dep.Ok)
                    {
                        throw new InvalidOperationException(dep.Detail);
                    }

                    if (s.WasRunning)
                    {
                        var live = await NewGenerationAsync(plan.Target, service, [.. before.Select(c => c.Id)], before.Count, ct);
                        var h = await health.WaitAsync([.. live.Select(c => c.Id)], SpecFor(s, p.ImageId), ct);
                        if (!h.Ok)
                        {
                            throw new InvalidOperationException(h.Reason);
                        }
                    }

                    return (true, (JsonObject?)null, false);
                });
            }
            catch (ProcessCrashedException)
            {
                throw;
            }
            catch (Exception e) when (e is InvalidOperationException or AppException)
            {
                failures.Add($"{service}: {e.Message}");
            }

            await runs.CompleteIntentAsync(intent, Now(), CancellationToken.None);
        }

        env.Invalidate();
        await gateway.RecordAsync(
            new AuditScope(actor, "maintenance.restore", plan.Target, null),
            failures.Count == 0 ? AuditOutcome.Success : AuditOutcome.Failure,
            new { run = runId, services = points.Select(p => p.Service), failures },
            null,
            CancellationToken.None);
        notify.Notify(new NotificationEvent(
            failures.Count > 0 ? "run.failed" : "run.rolled_back",
            failures.Count > 0 ? "critical" : "warning",
            $"Restore {(failures.Count > 0 ? "failed" : "completed")}: {plan.Target}",
            failures.Count > 0 ? string.Join("; ", failures) : $"Restored {string.Join(", ", points.Select(p => p.Service))}",
            $"restore:{runId}:{Now()}"));
        if (failures.Count > 0)
        {
            throw new AppException(502, ErrorCode.ComposeFailed, $"restore failed: {string.Join("; ", failures)}");
        }
    }
}

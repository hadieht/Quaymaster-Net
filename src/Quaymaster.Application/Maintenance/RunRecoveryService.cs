using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Maintenance;

namespace Quaymaster.Application.Maintenance;

public interface IRunRecoveryService
{
    /// <summary>
    /// At startup (spec 7.4, RT-14): runs that were <c>running</c> become <c>interrupted</c> and are never resumed; services left
    /// down raise a critical alert and the run carries the exact recovery commands. Returns how many runs were interrupted.
    /// </summary>
    Task<int> RecoverAsync(CancellationToken ct);
}

public sealed class RunRecoveryService(IRunStore runs, IRunEnvironment env, INotificationSink notify, IMutationGateway gateway, TimeProvider time) : IRunRecoveryService
{
    private static readonly Actor SystemActor = new("system", null);

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        var interrupted = await runs.InterruptRunningAsync(Now(), ct);

        // approved but never started (the process died in between): never run later on a stale approval
        _ = await runs.InterruptApprovedAsync(Now(), ct);
        foreach (var run in interrupted)
        {
            await runs.FailOpenStepsAsync(run.Id, Now(), ct);
            var guidance = new List<string>();
            var leftDown = false;
            var detail = await runs.GetAsync(run.Id, ct);
            var all = await LiveAsync(ct);
            var target = await TargetAsync(run.Target, ct);
            foreach (var it in await runs.OpenIntentsAsync(run.Id, ct))
            {
                var r = Assess(run.Target, it, all, detail?.Plan, target);
                guidance.AddRange(r.Guidance);
                leftDown |= r.Down || r.Unhealthy;
                Alert(run, it, r);
                await runs.CompleteIntentAsync(it.Id, Now(), ct);
            }

            if (guidance.Count == 0)
            {
                guidance.Add("The run was interrupted before any container was changed.");
            }

            await runs.FinishAsync(run.Id, RunStatuses.Interrupted, null, guidance, "Quaymaster stopped while this run was in progress", Now(), ct);
            notify.Notify(new NotificationEvent(
                "run.interrupted",
                leftDown ? "critical" : "warning",
                $"Maintenance run interrupted: {run.Target}",
                $"Quaymaster stopped during run {run.Id}. It is not resumed automatically. {guidance[0]}",
                $"interrupted:{run.Id}",
                null,
                new Dictionary<string, object?> { ["run"] = run.Id, ["target"] = run.Target, ["leftDown"] = leftDown }));
            await gateway.RecordAsync(
                new AuditScope(SystemActor, "maintenance.run.interrupted", run.Target, null),
                AuditOutcome.Failure,
                new { run = run.Id, leftDown, guidance },
                null,
                CancellationToken.None);
        }

        // intents left open by a restore (the run itself was already final): report them the same way (F-13)
        foreach (var it in await runs.OpenIntentsAsync(null, ct))
        {
            var run = await runs.GetAsync(it.OperationId, ct);
            if (run is null)
            {
                await runs.CompleteIntentAsync(it.Id, Now(), ct);
                continue;
            }

            var r = Assess(run.Info.Target, it, await LiveAsync(ct), run.Plan, await TargetAsync(run.Info.Target, ct));
            notify.Notify(new NotificationEvent(
                r.Down ? "service.left_down" : "run.interrupted",
                r.Down ? "critical" : "warning",
                $"{(r.Down ? "Service left down" : "Restore interrupted")} after a crash: {r.Title}",
                $"Quaymaster stopped during an operation on {it.Service} (run {run.Info.Id}). {r.Guidance[0]}",
                $"orphan-intent:{it.Id}"));
            await gateway.RecordAsync(
                new AuditScope(SystemActor, "maintenance.restore.interrupted", run.Info.Target, null),
                AuditOutcome.Failure,
                new { run = run.Info.Id, service = it.Service, guidance = r.Guidance },
                null,
                CancellationToken.None);
            await runs.CompleteIntentAsync(it.Id, Now(), ct);
        }

        return interrupted.Count;
    }

    private async Task<IReadOnlyList<EngineContainer>> LiveAsync(CancellationToken ct)
    {
        try
        {
            return await env.ContainersAsync(ct);
        }
        catch (AppException)
        {
            // Docker unreachable: the alert still goes out, the commands are generic
            return [];
        }
    }

    private async Task<ProjectTarget?> TargetAsync(string project, CancellationToken ct)
    {
        try
        {
            return await env.TargetAsync(project, ct);
        }
        catch (AppException)
        {
            return null;
        }
    }

    private static IntentAssessment Assess(string project, IntentInfo it, IReadOnlyList<EngineContainer> all, Plan? plan, ProjectTarget? target)
    {
        var live = all.Where(c => c.Project == project && c.Service == it.Service).Select(c => new LiveContainer(c.Id, c.ImageId, c.State, c.Status)).ToList();
        return RecoveryCommands.Assess(project, it, live, plan?.Services.FirstOrDefault(s => s.Service == it.Service), target);
    }

    private void Alert(RunInfo run, IntentInfo it, IntentAssessment r)
    {
        if (r.Down)
        {
            notify.Notify(new NotificationEvent(
                "service.left_down",
                "critical",
                $"Service left down after a crash: {r.Title}",
                $"Quaymaster stopped while it was updating {it.Service}; the service is not running. Recovery: see run {run.Id}.",
                $"leftdown:{run.Id}:{it.Service}"));
        }
        else if (r.Unhealthy)
        {
            notify.Notify(new NotificationEvent(
                "service.left_down",
                "critical",
                $"Service left unhealthy after a crash: {r.Title}",
                $"Quaymaster stopped while it was updating {it.Service}; the service is running but Docker reports it UNHEALTHY. It was not rolled back automatically after the restart. Recovery: see run {run.Id}.",
                $"leftunhealthy:{run.Id}:{it.Service}"));
        }
        else if (r.Unverified)
        {
            notify.Notify(new NotificationEvent(
                "run.interrupted",
                "warning",
                $"Update unverified after a crash: {r.Title}",
                $"Quaymaster stopped while {it.Service} was being updated; it is running but its health was never confirmed. See run {run.Id}.",
                $"unverified:{run.Id}:{it.Service}"));
        }
    }
}

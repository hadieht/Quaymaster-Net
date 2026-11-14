using System.Text.Json;
using System.Text.Json.Nodes;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Safety;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Maintenance;

public sealed record StandaloneRequest(string Container, string? Confirm);

/// <summary>Everything validated before a standalone run starts (no side effects).</summary>
public sealed record StandalonePrepared(string ContainerId, string Name, string Ref, string ToDigest, string FromImageId);

/// <summary>Config-clone update of a container that does not belong to a Compose project (RT-12). Manual only, best effort.</summary>
public interface IStandaloneService
{
    /// <summary>Validates confirmation, scope, update availability and cloneability. Throws; changes nothing.</summary>
    Task<StandalonePrepared> PrepareAsync(StandaloneRequest request, CancellationToken ct);

    /// <summary>Prepares, records an approved run and starts it in the background. Returns the run id (also the operation id).</summary>
    Task<string> StartAsync(StandaloneRequest request, Actor actor, CancellationToken ct);

    /// <summary>Runs the clone sequence for an already recorded run. Returns the final run status.</summary>
    Task<string> ExecuteAsync(string runId, StandalonePrepared p, string requestedBy, IOperationContext ctx, CancellationToken ct);
}

/// <summary>
/// Sequence: recovery point → pull by digest → create the clone (stopped) and connect its networks → stop the old container, set its
/// restart policy to <c>no</c>, rename it aside → rename the clone → start → health gate (probe). Any failure removes the clone,
/// renames the old container back, restores its policy, starts it and moves the tag back. Port of <c>StandaloneUpdater</c>.
/// </summary>
public sealed class StandaloneExecutor(
    IContainerService containers,
    IImageOps images,
    IUpdateService updates,
    IRunStore runs,
    IRollbackStore rollback,
    IStagedStore staged,
    IPolicyResolver resolver,
    IMutationGateway gateway,
    HealthGate health,
    IOperationManager operations,
    INotificationSink notify,
    QmConfig config,
    TimeProvider time) : IStandaloneService
{
    private const string LockKey = "maintenance";

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<StandalonePrepared> PrepareAsync(StandaloneRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (gateway.Policy.ReadOnlyReason is { } ro)
        {
            throw new AppException(409, ErrorCode.ReadOnly, $"Read-only mode: {ro}");
        }

        var c = await containers.ResolveAsync(request.Container, ct);
        if (request.Confirm != c.Name)
        {
            throw new AppException(409, ErrorCode.ConfirmationRequired, $"Confirm by sending {{\"confirm\":\"{c.Name}\"}}", new { expected = c.Name });
        }

        await gateway.AssertContainerAsync(new ContainerRef(c.Id, c.Name, c.Project), ct);
        if (c.Project is not null)
        {
            throw new AppException(409, ErrorCode.Unsupported, "Compose-managed containers are updated through their project.");
        }

        var statuses = await updates.StatusesAsync(true, ct);
        var st = statuses.FirstOrDefault(s => s.UsedBy.Any(u => u.ContainerId == c.Id))
            ?? throw AppException.Conflict("No update information for this container's image");
        if (st.State is not ("update_available" or "update_staged") || st.RemoteDigest is null)
        {
            throw AppException.Conflict($"The image is {st.State}; there is nothing to update");
        }

        // RT-03: the tag moves for every container that uses it; refuse when that includes fenced-off ones
        var fenced = st.UsedBy
            .Where(u => u.ContainerId != c.Id)
            .Where(u => gateway.Policy.ContainerRefusal(new ContainerRef(u.ContainerId, u.ContainerName, u.Project)) is { Code: not RefusalCode.ReadOnly })
            .Select(u => u.ContainerName)
            .ToList();
        if (fenced.Count > 0)
        {
            throw new AppException(409, ErrorCode.OutOfScope, $"Refused: {string.Join(", ", fenced)} use the same image tag but are outside QM_MANAGED_SCOPE or protected");
        }

        if ((await resolver.ResolveAsync(null, null, c.Name, ct)).UpdateMode == "ignore")
        {
            throw AppException.Conflict("The update policy of this container is \"ignore\"");
        }

        var imageId = c.Raw["Image"]?.GetValue<string>() ?? string.Empty;
        var img = await images.InspectAsync(imageId, ct) ?? throw AppException.Conflict("The container's image is no longer in the local store");
        try
        {
            _ = CloneSpecBuilder.Build(c.Raw, img, st.Ref);
        }
        catch (NotCloneableException e)
        {
            throw new AppException(409, ErrorCode.Unsupported, e.Message);
        }

        return new StandalonePrepared(c.Id, c.Name, st.Ref, st.RemoteDigest, imageId);
    }

    public async Task<string> StartAsync(StandaloneRequest request, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var p = await PrepareAsync(request, ct);
        if (operations.IsLocked(LockKey) || await runs.RunningAsync(ct) is not null)
        {
            throw AppException.Conflict("Another maintenance run is in progress (one run at a time)");
        }

        var id = Guid.NewGuid().ToString();
        await runs.InsertAsync(new NewRun(id, "update", "manual", p.Name, RunStatuses.Approved, null, null, actor.Name, new JsonObject { ["standalone"] = true }, Now()), ct);
        await gateway.RecordAsync(
            new AuditScope(actor, "maintenance.standalone.start", p.Name, null),
            AuditOutcome.Success,
            new { run = id, from = Short(p.FromImageId), to = Short(p.ToDigest) },
            null,
            CancellationToken.None);
        try
        {
            _ = await operations.StartAsync(
                new OperationStart(
                    "maintenance.update",
                    p.Name,
                    actor.Name,
                    new { run = id, standalone = true },
                    LockKey,
                    async ctx =>
                    {
                        var status = await ExecuteAsync(id, p, actor.Name, ctx, CancellationToken.None);
                        if (status is not RunStatuses.Completed)
                        {
                            throw new InvalidOperationException($"run {status}");
                        }
                    },
                    Id: id),
                ct);
        }
        catch (AppException e)
        {
            await runs.FinishAsync(id, RunStatuses.Blocked, null, null, e.Message, Now(), CancellationToken.None);
            throw;
        }

        return id;
    }

    private static string Short(string id) => id.Length > 19 ? id[..19] : id;

    private sealed class HealthFailure(string reason) : Exception(reason);

    public async Task<string> ExecuteAsync(string runId, StandalonePrepared p, string requestedBy, IOperationContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(ctx);
        if (!await runs.MarkRunningAsync(runId, Now(), ct))
        {
            throw AppException.Conflict("Run is no longer approved");
        }

        var seq = 0;
        void Log(string t) => ctx.Log(t + "\n");
        async Task<T> Step<T>(string name, Func<Task<T>> fn)
        {
            var sid = await runs.StartStepAsync(runId, ++seq, p.Name, name, Now(), CancellationToken.None);
            try
            {
                var v = await fn();
                await runs.FinishStepAsync(sid, "succeeded", null, Now(), CancellationToken.None);
                return v;
            }
            catch (Exception e)
            {
                await runs.FinishStepAsync(sid, "failed", new JsonObject { ["error"] = e.Message }, Now(), CancellationToken.None);
                throw;
            }
        }

        var image = ImageReferences.Parse(p.Ref) ?? throw AppException.BadRequest("Invalid image reference");
        var repoTag = $"{ImageReferences.EngineName(image)}:{image.Tag ?? "latest"}";
        var stamp = time.GetUtcNow().ToUnixTimeSeconds();
        var oldName = $"{p.Name}-qm-old-{stamp}";
        var tmpName = $"{p.Name}-qm-new-{stamp}";
        var oldRef = new ContainerRef(p.ContainerId, p.Name, null);
        var guidance = new List<string>();
        var status = RunStatuses.Completed;
        string? reason;
        string? newId = null;
        var oldStopped = false;
        var oldRenamed = false;
        var newRenamed = false;
        var tagMoved = false;
        var policyChanged = false;
        var origPolicy = ("no", 0);
        long? intent = null;
        try
        {
            var c = await Step("preflight", async () =>
            {
                var cur = await containers.ResolveAsync(p.ContainerId, ct);
                return cur.Name != p.Name || cur.Raw["Image"]?.GetValue<string>() != p.FromImageId
                    ? throw AppException.Conflict("The container changed since the request; nothing was done")
                    : cur;
            });
            var oldImage = await images.InspectAsync(p.FromImageId, ct) ?? throw AppException.Conflict("The previous image is gone");
            var rbTag = ImageReferences.RollbackTag("standalone", p.Name, runId);
            await Step("recovery_point", async () =>
            {
                await images.TagAsync(p.FromImageId, rbTag, ct);
                var now = Now();
                await rollback.PutAsync(new Domain.Updates.RollbackPoint(rbTag, runId, "standalone", p.Name, oldName, p.Ref, p.FromImageId, now, now + (long)config.RollbackRetention.TotalMilliseconds, false), ct);
                return true;
            });
            var stagedId = await Step("pull", async () =>
            {
                var tag = ImageReferences.StagedTag(image, p.ToDigest);
                if ((await images.InspectAsync(tag, ct))?["Id"]?.GetValue<string>() is { } have)
                {
                    return have;
                }

                try
                {
                    return (await images.PullByDigestAsync(image, p.ToDigest, Log, ct)).ImageId;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new AppException(502, ErrorCode.RegistryError, $"Pull failed: {e.Message}. Nothing was stopped, recreated or retagged.");
                }
            });
            var spec = CloneSpecBuilder.Build(c.Raw, oldImage, repoTag);
            intent = await runs.AddIntentAsync(new NewIntent(runId, p.Name, "standalone_clone", p.FromImageId, stagedId, [p.ContainerId]), Now(), CancellationToken.None);

            // the user-visible tag moves only now, immediately before the clone is created from it (RT-07)
            await images.TagAsync(stagedId, repoTag, ct);
            tagMoved = true;
            var wasRunning = c.Raw["State"]?["Running"]?.GetValue<bool>() == true;
            newId = await Step("create", async () =>
            {
                var id = await gateway.CreateCloneAsync(oldRef, tmpName, spec.Body, ct);
                newId = id;
                foreach (var n in spec.ExtraNetworks)
                {
                    await gateway.ConnectCloneNetworkAsync(oldRef, id, n.Network, n.ToEndpointConfig(), ct);
                }

                foreach (var note in spec.Notes)
                {
                    Log(note);
                }

                return id;
            });
            var cloneRef = new ContainerRef(newId, p.Name, null);
            await Step("swap", async () =>
            {
                try
                {
                    _ = await gateway.ExecuteContainerAsync(oldRef, new ContainerCommand(p.ContainerId, ContainerAction.Stop, 10), ct);
                    oldStopped = true;
                }
                catch (AppException e) when (e.Status == 404)
                {
                    throw;
                }

                // RT-12: the kept container must NOT come back after a daemon/host restart next to the clone, on the same volumes
                var hp = c.Raw["HostConfig"]?["RestartPolicy"];
                origPolicy = (hp?["Name"]?.GetValue<string>() is { Length: > 0 } pn ? pn : "no", hp?["MaximumRetryCount"]?.GetValue<int>() ?? 0);
                if (origPolicy.Item1 != "no")
                {
                    await gateway.SetRestartPolicyAsync(oldRef, "no", 0, ct);
                    policyChanged = true;
                }

                await gateway.RenameContainerAsync(oldRef, oldName, ct);
                oldRenamed = true;
                await gateway.RenameContainerAsync(cloneRef, p.Name, ct);
                newRenamed = true;
                if (wasRunning)
                {
                    _ = await gateway.ExecuteOnCloneAsync(oldRef, new ContainerCommand(newId, ContainerAction.Start), ct);
                }

                return true;
            });
            await gateway.RecordAsync(
                new AuditScope(new Actor("system", null), "maintenance.standalone.deploy", p.Name, null),
                AuditOutcome.Success,
                new { run = runId, from = Short(p.FromImageId), to = Short(stagedId), notes = spec.Notes },
                null,
                CancellationToken.None);

            if (wasRunning)
            {
                var nid = newId;
                var hcTest = c.Raw["Config"]?["Healthcheck"]?["Test"] as JsonArray;
                var hasHc = hcTest is { Count: > 0 } && hcTest[0]?.GetValue<string>() != "NONE";
                var pol = await resolver.ResolveAsync(null, null, p.Name, ct);
                var hcSeconds = hasHc
                    ? (((c.Raw["Config"]?["Healthcheck"]?["StartPeriod"]?.GetValue<double>() ?? 0) + ((c.Raw["Config"]?["Healthcheck"]?["Interval"]?.GetValue<double>() ?? 30e9) * (c.Raw["Config"]?["Healthcheck"]?["Retries"]?.GetValue<double>() ?? 3))) / 1e9) + 30
                    : 0;
                await Step("health", async () =>
                {
                    var r = await health.WaitAsync(
                        [nid],
                        new HealthSpec(hasHc ? "healthcheck" : "stability", (long)(Math.Max(pol.Health.TimeoutSeconds, hcSeconds) * 1000), (long)(pol.Health.StabilizationSeconds * 1000), pol.Health.Probe, stagedId),
                        ct);
                    return r.Ok ? r : throw new HealthFailure(r.Reason);
                });
            }

            await staged.DeleteAsync(ImageReferences.StagedTag(image, p.ToDigest), CancellationToken.None);
            await Try(() => images.RemoveAsync(ImageReferences.StagedTag(image, p.ToDigest), CancellationToken.None));
            var hours = Math.Round(config.RollbackRetention.TotalHours);
            reason = $"{p.Name} now runs {Short(p.ToDigest)}; the previous container is kept stopped as {oldName} for {hours} h"
                + (policyChanged
                    ? $" (its restart policy \"{origPolicy.Item1}\" was set to \"no\" so it cannot start next to the new container after a reboot; to go back: docker stop {p.Name} && docker rename {p.Name} {p.Name}-new && docker rename {oldName} {p.Name} && docker update --restart={origPolicy.Item1}{(origPolicy is ("on-failure", > 0) ? ":" + origPolicy.Item2 : string.Empty)} {p.Name} && docker start {p.Name})"
                    : string.Empty);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var why = e.Message;
            reason = why;
            status = RunStatuses.Failed;
            if (newId is { } id)
            {
                var cloneRef = new ContainerRef(id, p.Name, null);
                try
                {
                    await Step("rollback", async () =>
                    {
                        await Try(() => gateway.ExecuteOnCloneAsync(oldRef, new ContainerCommand(id, ContainerAction.Stop, 5), CancellationToken.None));
                        if (newRenamed)
                        {
                            await gateway.RenameContainerAsync(cloneRef, tmpName, CancellationToken.None);
                        }

                        _ = await gateway.ExecuteOnCloneAsync(oldRef, new ContainerCommand(id, ContainerAction.Remove), CancellationToken.None);
                        if (oldRenamed)
                        {
                            await gateway.RenameContainerAsync(oldRef, p.Name, CancellationToken.None);
                        }

                        if (oldStopped)
                        {
                            if (policyChanged)
                            {
                                await gateway.SetRestartPolicyAsync(oldRef, origPolicy.Item1, origPolicy.Item2, CancellationToken.None);
                            }

                            _ = await gateway.ExecuteContainerAsync(oldRef, new ContainerCommand(p.ContainerId, ContainerAction.Start), CancellationToken.None);
                        }

                        if (tagMoved)
                        {
                            await images.TagAsync(p.FromImageId, repoTag, CancellationToken.None);
                        }

                        return true;
                    });
                    status = e is HealthFailure ? RunStatuses.RolledBack : RunStatuses.Failed;
                    reason = $"{why}. The previous container was restored and is running again.";
                }
                catch (Exception undo) when (undo is not OperationCanceledException)
                {
                    guidance.AddRange(
                        $"Automatic undo failed ({undo.Message}). Restore by hand:",
                        $"docker rm -f {tmpName}   # the clone, if it exists",
                        $"docker rename {oldName} {p.Name}",
                        $"docker start {p.Name}");
                    notify.Notify(new NotificationEvent(
                        "service.left_down", "critical", $"Service left down: {p.Name}", $"The automatic undo of the update of {p.Name} failed: {undo.Message}", $"left-down:{runId}", null,
                        new Dictionary<string, object?> { ["run"] = runId, ["target"] = p.Name, ["services"] = new[] { p.Name } }));
                }
            }
            else if (tagMoved)
            {
                await Try(() => images.TagAsync(p.FromImageId, repoTag, CancellationToken.None));
            }
        }

        if (intent is { } iid)
        {
            await runs.CompleteIntentAsync(iid, Now(), CancellationToken.None);
        }

        var outcome = status switch { RunStatuses.Completed => "updated", RunStatuses.RolledBack => "rolled_back", _ => "failed" };
        await runs.FinishAsync(runId, status, new RunResult([new ServiceResult(p.Name, outcome, reason ?? string.Empty)], reason ?? status), guidance.Count > 0 ? guidance : null, reason, Now(), CancellationToken.None);
        containers.Invalidate();
        await gateway.RecordAsync(
            new AuditScope(new Actor(requestedBy, null), "maintenance.run.finish", p.Name, null),
            status == RunStatuses.Completed ? AuditOutcome.Success : AuditOutcome.Failure,
            new { run = runId, status, standalone = true },
            null,
            CancellationToken.None);
        notify.Notify(RunExecutor.NotificationFor(runId, p.Name, status, reason ?? status, guidance));
        ctx.Log($"run {runId} finished: {status}\n");
        return status;
    }

    private static async Task Try(Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // best-effort cleanup: the outcome is reported elsewhere
        catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _ = e;
        }
    }
}

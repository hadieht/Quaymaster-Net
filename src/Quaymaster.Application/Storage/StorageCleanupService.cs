using System.Text.Json.Nodes;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Safety;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Safety;
using Quaymaster.Domain.Storage;

namespace Quaymaster.Application.Storage;

public sealed record StoragePreviewRequest(IReadOnlyList<string> Categories, bool? IncludeComposeProjectVolumes, double? MinAgeHours);

public interface IStorageCleanupService
{
    Task<StorageCleanupPreviewDto> PreviewAsync(StoragePreviewRequest request, Actor actor, CancellationToken ct);

    Task<StorageCleanupResultDto> ExecuteAsync(string previewId, string confirm, Actor actor, CancellationToken ct);

    Task<ResourceRemoveResultDto> RemoveVolumeAsync(string name, string confirm, bool acknowledgeComposeProject, Actor actor, CancellationToken ct);

    Task<ResourceRemoveResultDto> RemoveNetworkAsync(string reference, string confirm, Actor actor, CancellationToken ct);
}

/// <summary>
/// Removal of unused volumes and networks (docs/ARCHITECTURE.md 7.6). Volume removal is permanent data loss, so these are the
/// strictest rules in the product:
/// a volume that any container references (running OR stopped) is never removed; Quaymaster's own volumes, QM_MANAGED_SCOPE,
/// read-only mode and the Compose project check are enforced here, server-side, whatever the UI sends; bulk removal executes only
/// candidates of a stored, unexpired preview created by the same admin and re-inspects every one right before removing it (a
/// volume recreated under the same name is skipped); the Engine call itself is never forced. Containers are never touched.
/// </summary>
public sealed class StorageCleanupService(
    IDockerEngine engine,
    IMutationGateway gateway,
    IComposeOwnershipProvider ownership,
    IDfMeasurer dfMeasurer,
    IReclaimMeasurer reclaim,
    QmConfig config,
    TimeProvider time) : IStorageCleanupService
{
    public static readonly IReadOnlyList<string> Categories = ["unused_networks", "anonymous_volumes", "named_volumes"];

    private readonly PreviewStore<StorageCleanupPreviewDto> _previews = new(time);

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    private SafetyPolicy Policy => gateway.Policy;

    private static AppException Refused(StorageRefusal r) =>
        r.Kind switch
        {
            RefusalKind.SelfProtected => new AppException(409, ErrorCode.SelfProtected, r.Reason),
            RefusalKind.OutOfScope => new AppException(409, ErrorCode.OutOfScope, r.Reason),
            _ => new AppException(409, ErrorCode.Conflict, r.Reason),
        };

    /// <summary>QM_VISIBLE_SCOPE: a resource outside it does not exist for the UI/API (RT3-02).</summary>
    private bool VisibleResource(string name, IReadOnlyDictionary<string, string> labels) =>
        !Policy.VisibilityRestricted
        || Policy.Visible(name)
        || (labels.TryGetValue(ComposeLabels.Project, out var p) && Policy.Visible(p));

    private static string NameOf(JsonObject c) =>
        DockerNames.CleanName(c.Arr("Names") is { Count: > 0 } n ? n[0].Str() : null, c.Str("Id") ?? string.Empty);

    /// <summary>Containers (any state, deliberately NOT filtered by QM_VISIBLE_SCOPE) that reference the volume.</summary>
    private static List<string> VolumeUsers(IEnumerable<JsonObject> containers, string volume) =>
        [.. containers.Where(c => c.Arr("Mounts").Objects().Any(m => m.Str("Name") == volume)).Select(NameOf)];

    /// <summary>A stopped container has no live endpoint, so it is matched by name/ID in its settings and in HostConfig.NetworkMode.</summary>
    private static bool UsesNetwork(JsonObject c, string id, string name)
    {
        var mode = c.Obj("HostConfig")?.Str("NetworkMode");
        if (mode == name || mode == id)
        {
            return true;
        }

        return (c.Obj("NetworkSettings")?.Obj("Networks") ?? []).Any(kv => kv.Key == name || (kv.Value.Str("NetworkID") is { Length: > 0 } nid && nid == id));
    }

    private static VolumeFacts VolumeFactsOf(JsonObject v) => new(v.Str("Name") ?? string.Empty, v.Obj("Labels").StringMap(), v.Str("CreatedAt"));

    private static NetworkFacts NetworkFactsOf(JsonObject n) =>
        new(n.Str("Id") ?? string.Empty, n.Str("Name") ?? string.Empty, n.Obj("Labels").StringMap(), n["Ingress"].Bool() == true, n.Obj("Containers")?.Count ?? 0, n.Str("Created"));

    private async Task<List<JsonObject>> AllContainersAsync(CancellationToken ct) => [.. (await engine.ListContainersAsync(ct)).Objects()];

    // ---- preview ------------------------------------------------------------------------------------------------------------

    public async Task<StorageCleanupPreviewDto> PreviewAsync(StoragePreviewRequest request, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var categories = request.Categories.Distinct(StringComparer.Ordinal).ToList();
        if (categories.Count == 0)
        {
            throw AppException.BadRequest("Choose at least one category");
        }

        foreach (var c in categories)
        {
            if (!Categories.Contains(c))
            {
                throw AppException.BadRequest($"Unknown category \"{c}\"");
            }
        }

        var includeCompose = request.IncludeComposeProjectVolumes == true;
        var minAgeMs = request.MinAgeHours is { } h ? (long)(Math.Max(h, 0) * 3_600_000) : (long)config.CleanupMinAge.TotalMilliseconds;
        var now = Now;
        var wantVolumes = categories.Contains("anonymous_volumes") || categories.Contains("named_volumes");
        var own = await ownership.OwnershipAsync(ct);
        var candidates = new List<StorageCandidateDto>();
        var prot = new List<StorageProtectedDto>();

        bool TooYoung(string name, string kind, int? age)
        {
            if (age is { } a && a * 3_600_000L < minAgeMs)
            {
                prot.Add(new StorageProtectedDto(name, kind, $"younger than the minimum age ({(long)Math.Round(minAgeMs / 3_600_000.0, MidpointRounding.AwayFromZero)} h)"));
                return true;
            }

            return false;
        }

        if (wantVolumes)
        {
            var vols = await engine.ListVolumesAsync(ct);
            var containers = await AllContainersAsync(ct);
            var df = await dfMeasurer.MeasureAsync(ct);
            foreach (var v in vols.Arr("Volumes").Objects())
            {
                var facts = VolumeFactsOf(v);
                if (!VisibleResource(facts.Name, facts.Labels))
                {
                    continue;
                }

                var anonymous = StorageRules.IsAnonymous(facts.Name, facts.Labels);
                var category = anonymous ? "anonymous_volumes" : "named_volumes";
                if (!categories.Contains(category))
                {
                    continue;
                }

                var refusal = StorageRules.VolumeRefusal(Policy, facts, VolumeUsers(containers, facts.Name), own, includeCompose);
                if (refusal is not null)
                {
                    prot.Add(new StorageProtectedDto(facts.Name, "volume", refusal.Reason));
                    continue;
                }

                var age = StorageRules.HoursSince(facts.CreatedAt, now);
                if (TooYoung(facts.Name, "volume", age))
                {
                    continue;
                }

                long? size = df is not null && df.Volumes.TryGetValue(facts.Name, out var s) ? s : null;
                var project = facts.Labels.GetValueOrDefault(ComposeLabels.Project);
                var volumeLabel = facts.Labels.GetValueOrDefault("com.docker.compose.volume");
                candidates.Add(new StorageCandidateDto(
                    $"volume:{facts.Name}",
                    "volume",
                    category,
                    facts.Name,
                    size,
                    facts.CreatedAt,
                    age,
                    anonymous,
                    project,
                    project is null ? null : $"Compose project {project}{(volumeLabel is null ? string.Empty : $" (volume \"{volumeLabel}\")")}",
                    anonymous
                        ? "anonymous volume (unnamed hash) that no container references"
                        : "named volume that no container references; its data is deleted permanently"));
            }
        }

        if (categories.Contains("unused_networks"))
        {
            var nets = await engine.ListNetworksAsync(ct);
            var containers = await AllContainersAsync(ct);
            foreach (var n in nets.Objects())
            {
                var facts = NetworkFactsOf(n);
                if (!VisibleResource(facts.Name, facts.Labels))
                {
                    continue;
                }

                var used = containers.Where(c => UsesNetwork(c, facts.Id, facts.Name)).Select(NameOf).ToList();
                var refusal = StorageRules.NetworkRefusal(Policy, facts, used, own);
                if (refusal is not null)
                {
                    // the daemon's own networks are not worth listing as "protected"
                    if (!StorageRules.PredefinedNetworks.Contains(facts.Name))
                    {
                        prot.Add(new StorageProtectedDto(facts.Name, "network", refusal.Reason));
                    }

                    continue;
                }

                var age = StorageRules.HoursSince(facts.Created, now);
                if (TooYoung(facts.Name, "network", age))
                {
                    continue;
                }

                var project = facts.Labels.GetValueOrDefault(ComposeLabels.Project);
                candidates.Add(new StorageCandidateDto(
                    $"network:{facts.Id}",
                    "network",
                    "unused_networks",
                    facts.Name,
                    0,
                    facts.Created,
                    age,
                    false,
                    project,
                    project is null ? null : $"Compose project {project}",
                    "network with no attached containers (networks free no disk space)"));
            }
        }

        var volCount = candidates.Count(c => c.Kind == "volume");
        var preview = new StorageCleanupPreviewDto(
            Guid.NewGuid().ToString(),
            ImageReferences.Iso((time.GetUtcNow() + PreviewStore<StorageCleanupPreviewDto>.Ttl).ToUnixTimeMilliseconds()),
            categories,
            includeCompose,
            (int)Math.Round(minAgeMs / 3_600_000.0, MidpointRounding.AwayFromZero),
            candidates,
            prot,
            candidates.Sum(c => c.Size ?? 0),
            volCount > 0 ? $"delete {volCount} {(volCount == 1 ? "volume" : "volumes")}" : null,
            volCount > 0);
        _previews.Put(preview.PreviewId, preview, actor.Name);
        await gateway.RecordAsync(
            new AuditScope(actor, "storage.cleanup.preview", string.Join(',', categories), null),
            AuditOutcome.Success,
            new { candidates = candidates.Count, @protected = prot.Count, includeCompose },
            null,
            ct);
        return preview;
    }

    // ---- bulk execute -------------------------------------------------------------------------------------------------------

    public async Task<StorageCleanupResultDto> ExecuteAsync(string previewId, string confirm, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(previewId);
        ArgumentNullException.ThrowIfNull(actor);
        var scope = new AuditScope(actor, "storage.cleanup.execute", previewId, null);
        async Task<AppException> DeniedAsync(AppException e)
        {
            await gateway.RecordAsync(scope, AuditOutcome.Denied, new { error = e.Message }, null, ct);
            return e;
        }

        if (Policy.ReadOnlyReason is { } ro)
        {
            throw await DeniedAsync(new AppException(409, ErrorCode.ReadOnly, $"Read-only mode: {ro}"));
        }

        var stored = _previews.Get(previewId);
        if (stored is null)
        {
            throw await DeniedAsync(new AppException(409, ErrorCode.Conflict, "No valid preview: run a storage cleanup preview first (previews expire after 10 minutes)"));
        }

        if (stored.Value.Actor != actor.Name)
        {
            throw await DeniedAsync(new AppException(403, ErrorCode.Forbidden, "That preview belongs to another user"));
        }

        var plan = stored.Value.Value;
        var expected = plan.ConfirmPhrase ?? previewId;
        if (confirm != expected)
        {
            throw await DeniedAsync(new AppException(
                409,
                ErrorCode.ConfirmationRequired,
                plan.ConfirmPhrase is not null ? $"This permanently deletes data. Type \"{expected}\" to confirm" : $"Confirm by sending {{\"confirm\":\"{expected}\"}}",
                new { expected }));
        }

        _previews.Remove(previewId);
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        var (value, report) = await reclaim.MeasureAsync(
            async before =>
            {
                var removed = new List<StorageRemovedDto>();
                var skipped = new List<SkippedItemDto>();
                long estimated = 0;
                long max = 0;
                var own = await ownership.OwnershipAsync(ct);
                foreach (var c in plan.Candidates)
                {
                    var reference = c.Id[(c.Id.IndexOf(':', StringComparison.Ordinal) + 1)..];
                    var action = $"{c.Kind}.remove";
                    try
                    {
                        if (c.Kind == "volume")
                        {
                            var info = await engine.InspectVolumeAsync(reference, ct) ?? throw new SkipException("already gone", AuditOutcome.Success);
                            var facts = VolumeFactsOf(info);
                            if (c.CreatedAt is not null && facts.CreatedAt != c.CreatedAt)
                            {
                                throw new SkipException("was recreated since the preview", AuditOutcome.Denied);
                            }

                            if (StorageRules.IsAnonymous(facts.Name, facts.Labels) != c.Anonymous)
                            {
                                throw new SkipException("changed kind since the preview", AuditOutcome.Denied);
                            }

                            var r = StorageRules.VolumeRefusal(Policy, facts, VolumeUsers(await AllContainersAsync(ct), reference), own, plan.IncludeComposeProjectVolumes);
                            if (r is not null)
                            {
                                throw new SkipException(r.Reason, AuditOutcome.Denied);
                            }

                            await gateway.RemoveVolumeAsync(reference, ct);
                            max += before?.Volumes.GetValueOrDefault(c.Name) ?? c.Size ?? 0;
                        }
                        else
                        {
                            var info = await engine.InspectNetworkAsync(reference, ct) ?? throw new SkipException("already gone", AuditOutcome.Success);
                            var facts = NetworkFactsOf(info);
                            if (facts.Id != reference)
                            {
                                throw new SkipException("changed since the preview", AuditOutcome.Denied);
                            }

                            var users = (await AllContainersAsync(ct)).Where(x => UsesNetwork(x, facts.Id, facts.Name)).Select(NameOf).ToList();
                            var r = StorageRules.NetworkRefusal(Policy, facts, users, own);
                            if (r is not null)
                            {
                                throw new SkipException(r.Reason, AuditOutcome.Denied);
                            }

                            await gateway.RemoveNetworkAsync(facts.Id, facts.Name, ct);
                        }

                        removed.Add(new StorageRemovedDto(c.Id, c.Name, c.Kind, c.Size ?? 0));
                        estimated += c.Size ?? 0;
                        await gateway.RecordAsync(new AuditScope(actor, action, c.Name, null), AuditOutcome.Success, new { via = "storage-cleanup", previewId, bytes = c.Size ?? 0, category = c.Category }, null, ct);
                    }
                    catch (Exception e) when (e is SkipException or AppException)
                    {
                        skipped.Add(new SkippedItemDto(c.Id, c.Name, e.Message));
                        await gateway.RecordAsync(
                            new AuditScope(actor, action, c.Name, null),
                            e is SkipException s ? s.Outcome : AuditOutcome.Failure,
                            new { via = "storage-cleanup", previewId, reason = e.Message },
                            null,
                            ct);
                    }
                }

                return new ReclaimRun<(List<StorageRemovedDto>, List<SkippedItemDto>)>((removed, skipped), estimated, max);
            },
            ct);
        await gateway.RecordAsync(scope, AuditOutcome.Success, new { removed = value.Item1.Count, skipped = value.Item2.Count, reclaimedBytes = report.ReclaimedBytes }, t0.ElapsedMilliseconds, ct);
        return new StorageCleanupResultDto(value.Item1, value.Item2, report);
    }

    private sealed class SkipException(string message, AuditOutcome outcome) : Exception(message)
    {
        public AuditOutcome Outcome { get; } = outcome;
    }

    // ---- single removal -----------------------------------------------------------------------------------------------------

    public async Task<ResourceRemoveResultDto> RemoveVolumeAsync(string name, string confirm, bool acknowledgeComposeProject, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(actor);
        if (!DockerNames.IsSafeRef(name, RefKind.Volume))
        {
            throw AppException.BadRequest("Invalid volume name");
        }

        var scope = new AuditScope(actor, "volume.remove", name, new { via = "single" });
        return await gateway.AuditedAsync(
            scope,
            async () =>
            {
                if (Policy.ReadOnlyReason is { } ro)
                {
                    throw new AppException(409, ErrorCode.ReadOnly, $"Read-only mode: {ro}");
                }

                if (confirm != name)
                {
                    throw AppException.Conflict("This permanently deletes the volume's data. Type the exact volume name to confirm", ErrorCode.ConfirmationRequired, new { expected = name });
                }

                var info = await engine.InspectVolumeAsync(name, ct);
                var facts = info is null ? null : VolumeFactsOf(info);
                if (facts is null || !VisibleResource(facts.Name, facts.Labels))
                {
                    throw AppException.NotFound("Volume");
                }

                var own = await ownership.OwnershipAsync(ct);
                var r = StorageRules.VolumeRefusal(Policy, facts, VolumeUsers(await AllContainersAsync(ct), name), own, acknowledgeComposeProject);
                if (r is not null)
                {
                    var message = r.Kind == RefusalKind.Conflict ? $"Refused: volume {name} is {r.Reason}" : r.Reason;
                    throw new AppException(
                        409,
                        r.Kind switch { RefusalKind.SelfProtected => ErrorCode.SelfProtected, RefusalKind.OutOfScope => ErrorCode.OutOfScope, _ => ErrorCode.Conflict },
                        message,
                        r.ComposeOptIn ? new { requires = "acknowledgeComposeProject" } : null);
                }

                var (_, report) = await reclaim.MeasureAsync<object?>(
                    async before =>
                    {
                        await gateway.RemoveVolumeAsync(name, ct);
                        long? size = before is not null && before.Volumes.TryGetValue(name, out var s) ? s : null;
                        return new ReclaimRun<object?>(null, size ?? 0, size);
                    },
                    ct);
                scope.Params = new { via = "single", bytes = report.ReclaimedBytes, estimatedBytes = report.EstimatedBytes };
                return new ResourceRemoveResultDto("volume", name, "deleted", [], [], report);
            },
            ct);
    }

    public async Task<ResourceRemoveResultDto> RemoveNetworkAsync(string reference, string confirm, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actor);
        if (!DockerNames.IsSafeRef(reference, RefKind.Network))
        {
            throw AppException.BadRequest("Invalid network reference");
        }

        var scope = new AuditScope(actor, "network.remove", reference, new { via = "single" });
        return await gateway.AuditedAsync(
            scope,
            async () =>
            {
                if (Policy.ReadOnlyReason is { } ro)
                {
                    throw new AppException(409, ErrorCode.ReadOnly, $"Read-only mode: {ro}");
                }

                var info = await engine.InspectNetworkAsync(reference, ct);
                var facts = info is null ? null : NetworkFactsOf(info);
                if (facts is null || !VisibleResource(facts.Name, facts.Labels))
                {
                    throw AppException.NotFound("Network");
                }

                scope.Target = facts.Name;
                if (confirm != facts.Name)
                {
                    throw AppException.Conflict($"Confirm by sending {{\"confirm\":\"{facts.Name}\"}}", ErrorCode.ConfirmationRequired, new { expected = facts.Name });
                }

                var own = await ownership.OwnershipAsync(ct);
                var users = (await AllContainersAsync(ct)).Where(x => UsesNetwork(x, facts.Id, facts.Name)).Select(NameOf).ToList();
                var r = StorageRules.NetworkRefusal(Policy, facts, users, own);
                if (r is not null)
                {
                    throw r.Kind == RefusalKind.Conflict ? AppException.Conflict($"Refused: network {facts.Name}: {r.Reason}") : Refused(r);
                }

                var (_, report) = await reclaim.MeasureAsync<object?>(
                    async _ =>
                    {
                        await gateway.RemoveNetworkAsync(facts.Id, facts.Name, ct);
                        return new ReclaimRun<object?>(null, 0);
                    },
                    ct);
                scope.Params = new { via = "single", id = facts.Id, bytes = 0 };
                return new ResourceRemoveResultDto("network", facts.Name, "deleted", [], [], report with { ReclaimedBytes = 0, Basis = "estimate", Note = "Networks use no disk space." });
            },
            ct);
    }
}

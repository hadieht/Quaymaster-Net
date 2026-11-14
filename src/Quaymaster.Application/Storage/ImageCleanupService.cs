using System.Globalization;
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
using Quaymaster.Domain.Updates;

namespace Quaymaster.Application.Storage;

public sealed record CleanupPreviewRequest(IReadOnlyList<string>? Categories, double? MinAgeHours, string? LabelFilter);

public interface IImageCleanupService
{
    Task<CleanupPreviewDto> PreviewAsync(CleanupPreviewRequest request, Actor actor, CancellationToken ct);

    Task<CleanupResultDto> ExecuteAsync(string previewId, string confirm, Actor actor, CancellationToken ct);

    /// <summary>Removes one image, or one tag of it (<paramref name="tag"/>).</summary>
    Task<ResourceRemoveResultDto> RemoveImageAsync(string reference, string confirm, string? tag, Actor actor, CancellationToken ct);
}

/// <summary>
/// Image / stopped-container / expired rollback cleanup (spec 7.6 + RT-24). A preview is MANDATORY: removal executes only the
/// candidates of a preview the same admin created in the last 10 minutes, and every candidate is re-verified right before it is
/// removed. Never removed: images used by any container (even an invisible one), images named by a discovered Compose project,
/// rollback / staged tags within retention, anything younger than the minimum age, anything outside QM_MANAGED_SCOPE, Quaymaster
/// itself. Volumes and networks are not handled here (see <see cref="IStorageCleanupService"/>); they are only listed in
/// <c>reportOnly</c>.
/// </summary>
public sealed class ImageCleanupService(
    IDockerEngine engine,
    IContainerService containers,
    IImageOps images,
    IMutationGateway gateway,
    IRollbackStore rollback,
    IStagedStore staged,
    IComposeOwnershipProvider ownership,
    IDfMeasurer dfMeasurer,
    IReclaimMeasurer reclaim,
    QmConfig config,
    TimeProvider time) : IImageCleanupService
{
    public static readonly IReadOnlyList<string> AllCategories = ["dangling_images", "unused_images", "stopped_containers", "expired_rollback"];

    private readonly PreviewStore<StoredPreview> _previews = new(time);

    private sealed record StoredPreview(CleanupPreviewDto Preview, LabelFilter? Filter);

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    private SafetyPolicy Policy => gateway.Policy;

    private static string ContainerLabel(ContainerInfo c) => c.Name;

    // ---- preview ----------------------------------------------------------------------------------------------------------

    public async Task<CleanupPreviewDto> PreviewAsync(CleanupPreviewRequest request, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        IReadOnlyList<string> categories = request.Categories is { Count: > 0 } ? request.Categories : AllCategories;
        foreach (var c in categories)
        {
            if (!AllCategories.Contains(c))
            {
                throw AppException.BadRequest($"Unknown category \"{c}\"");
            }
        }

        var minAgeMs = request.MinAgeHours is { } h ? (long)(Math.Max(h, 0) * 3_600_000) : (long)config.CleanupMinAge.TotalMilliseconds;
        LabelFilter? filter;
        try
        {
            filter = LabelFilter.Parse(request.LabelFilter);
        }
        catch (FormatException e)
        {
            throw AppException.BadRequest(e.Message);
        }

        var now = Now;
        var candidates = new List<CleanupCandidateDto>();
        var prot = new List<CleanupProtectedDto>();

        // RT3-03: "in use" is computed from EVERY container on the daemon; QM_VISIBLE_SCOPE only limits what we show or remove.
        var all = (await engine.ListContainersAsync(ct)).Objects().Select(ContainerInfo.From).ToList();
        var visible = await containers.ListRawAllAsync(true, ct);
        var usedBy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in all)
        {
            usedBy.TryAdd(c.ImageId, ContainerLabel(c));
        }

        var referenced = await ownership.ProjectImageRefsAsync(ct);
        var rollbackActive = (await rollback.ActiveAsync(ct)).ToDictionary(r => r.Tag, StringComparer.Ordinal);
        var rollbackImageIds = rollbackActive.Values.Where(r => r.ExpiresAt > now).Select(r => r.ImageId).ToHashSet(StringComparer.Ordinal);
        var ctx = new ImageContext(Policy, now, minAgeMs, filter, usedBy, rollbackImageIds, referenced);
        var retentionMs = (long)config.RollbackRetention.TotalMilliseconds;

        var wantImages = categories.Any(c => c is "dangling_images" or "unused_images" or "expired_rollback");
        var listed = wantImages ? (await engine.ListImagesAsync(ct)).Objects().ToList() : [];
        var df = wantImages ? await dfMeasurer.MeasureAsync(ct) : null;
        foreach (var img in listed)
        {
            var id = img.Str("Id") ?? string.Empty;
            var tags = (img.Arr("RepoTags")?.Select(t => t.Str()).OfType<string>() ?? []).Where(t => t != "<none>:<none>").ToList();
            var isDangling = tags.Count == 0;
            var name = tags.Count > 0 ? tags[0] : DockerNames.ShortId(id);
            var category = isDangling ? "dangling_images" : "unused_images";
            var internalTags = tags.Where(ImageReferences.IsInternalTag).ToList();
            var userTags = tags.Where(t => !ImageReferences.IsInternalTag(t)).ToList();

            // expired rollback / staged tags are their own category: only the internal tag is removed
            if (categories.Contains("expired_rollback"))
            {
                foreach (var t in internalTags)
                {
                    rollbackActive.TryGetValue(t, out var rb);
                    var st = await staged.GetAsync(t, ct);
                    var (protect, age) = ImageCleanupRules.ExpiredTag(t, rb, st, retentionMs, now, Policy, filter);
                    if (protect is not null)
                    {
                        prot.Add(new CleanupProtectedDto(t, "expired_rollback", protect));
                        continue;
                    }

                    candidates.Add(new CleanupCandidateDto($"tag:{t}", "expired_rollback", t, null, null, age, "expired; only this tag is removed (the image is deleted only when nothing else uses it)"));
                }
            }

            if (!categories.Contains(category) || (internalTags.Count > 0 && userTags.Count == 0))
            {
                continue; // internal-only images are handled above, never an "unused image"
            }

            var inspect = await engine.InspectImageAsync(id, ct);
            if (inspect is null)
            {
                continue;
            }

            var labels = inspect.Obj("Config")?.Obj("Labels").StringMap() ?? [];
            var lastTagText = inspect.Obj("Metadata")?.Str("LastTagTime");
            var lastTag = lastTagText is not null && !lastTagText.StartsWith("0001", StringComparison.Ordinal) && DateTimeOffset.TryParse(lastTagText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lt)
                ? lt.ToUnixTimeMilliseconds()
                : 0;
            var created = DateTimeOffset.TryParse(inspect.Str("Created"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var cr) ? cr.ToUnixTimeMilliseconds() : 0;
            var refTime = Math.Max(created, lastTag);
            var reason = ImageCleanupRules.ProtectReason(new ImageFacts(id, tags, labels, refTime), ctx);
            if (reason is not null)
            {
                prot.Add(new CleanupProtectedDto(name, category, reason));
                continue;
            }

            long? shared = df is not null && df.Images.TryGetValue(id, out var e) ? e.Shared : null;
            long? dfSize = df is not null && df.Images.TryGetValue(id, out var e2) ? e2.Size : null;
            var size = ImageCleanupRules.UniqueSize(dfSize, shared, img.Num("Size") ?? 0);
            candidates.Add(new CleanupCandidateDto(
                $"image:{id}",
                category,
                isDangling ? name : string.Join(", ", tags),
                size,
                img.Num("Size"),
                (int)Math.Round((now - refTime) / 3_600_000.0, MidpointRounding.AwayFromZero),
                (isDangling ? "untagged image" : "tagged image that no container uses") + "; frees its own layers only, shared layers stay until the last image using them goes"));
        }

        if (categories.Contains("stopped_containers"))
        {
            foreach (var c in visible)
            {
                if (c.State is not ("exited" or "created" or "dead"))
                {
                    continue;
                }

                var project = c.Project;
                var refusal = Policy.ContainerRefusal(new ContainerRef(c.Id, c.Name, project));
                var add = (string why) => prot.Add(new CleanupProtectedDto(c.Name, "stopped_containers", why));
                if (refusal is not null)
                {
                    add(refusal.Message);
                    continue;
                }

                if (project is not null)
                {
                    add($"part of Compose project \"{project}\" (use compose down); a stopped project is not garbage");
                    continue;
                }

                if (filter is not null && !filter.Matches(c.Labels))
                {
                    add($"does not match label filter {request.LabelFilter}");
                    continue;
                }

                var ageMs = now - (c.Created * 1000);
                if ((c.Name.StartsWith("qm-old-", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(c.Name, @"-qm-old-\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) && ageMs < retentionMs)
                {
                    add("standalone rollback point within retention");
                    continue;
                }

                if (ageMs < minAgeMs)
                {
                    add($"younger than the minimum age ({(long)Math.Round(minAgeMs / 3_600_000.0, MidpointRounding.AwayFromZero)} h)");
                    continue;
                }

                candidates.Add(new CleanupCandidateDto($"container:{c.Id}", "stopped_containers", c.Name, null, null, (int)Math.Round(ageMs / 3_600_000.0, MidpointRounding.AwayFromZero), "stopped standalone container (volumes are kept)"));
            }
        }

        // only counted here; their removal is the storage cleanup
        var mounted = all.SelectMany(c => c.Mounts.Select(m => m.Name)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var netUsed = all.SelectMany(c => c.Networks.Values).OfType<string>().ToHashSet(StringComparer.Ordinal);
        bool LabelOk(JsonObject o) => filter is null || filter.Matches(o.Obj("Labels").StringMap());
        var unusedVolumes = (await engine.ListVolumesAsync(ct)).Arr("Volumes").Objects()
            .Where(v => !mounted.Contains(v.Str("Name") ?? string.Empty) && Policy.InScope(v.Str("Name") ?? string.Empty) && LabelOk(v))
            .Select(v => v.Str("Name") ?? string.Empty).ToList();
        var unusedNetworks = (await engine.ListNetworksAsync(ct)).Objects()
            .Where(n => !StorageRules.PredefinedNetworks.Contains(n.Str("Name") ?? string.Empty) && !netUsed.Contains(n.Str("Id") ?? string.Empty) && Policy.InScope(n.Str("Name") ?? string.Empty) && LabelOk(n))
            .Select(n => n.Str("Name") ?? string.Empty).ToList();

        var previewId = Guid.NewGuid().ToString();
        var preview = new CleanupPreviewDto(
            previewId,
            ImageReferences.Iso((time.GetUtcNow() + PreviewStore<StoredPreview>.Ttl).ToUnixTimeMilliseconds()),
            categories,
            (int)Math.Round(minAgeMs / 3_600_000.0, MidpointRounding.AwayFromZero),
            candidates,
            prot,
            new ReportOnlyDto(unusedVolumes, unusedNetworks),
            candidates.Sum(c => c.Size ?? 0));
        _previews.Put(previewId, new StoredPreview(preview, filter), actor.Name);
        await gateway.RecordAsync(
            new AuditScope(actor, "cleanup.preview", string.Join(',', categories), null),
            AuditOutcome.Success,
            new { candidates = candidates.Count, @protected = prot.Count, labelFilter = request.LabelFilter },
            null,
            ct);
        return preview;
    }

    // ---- execute ----------------------------------------------------------------------------------------------------------

    public async Task<CleanupResultDto> ExecuteAsync(string previewId, string confirm, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(previewId);
        ArgumentNullException.ThrowIfNull(actor);
        var scope = new AuditScope(actor, "cleanup.execute", previewId, null);

        // checks first, each recorded as denied
        var stored = _previews.Get(previewId);
        if (stored is null)
        {
            throw await DeniedAsync(scope, new AppException(409, ErrorCode.Conflict, "No valid preview: run a cleanup preview first (previews expire after 10 minutes)"), ct);
        }

        if (stored.Value.Actor != actor.Name)
        {
            throw await DeniedAsync(scope, new AppException(403, ErrorCode.Forbidden, "That preview belongs to another user"), ct);
        }

        if (confirm != previewId)
        {
            throw await DeniedAsync(scope, new AppException(409, ErrorCode.ConfirmationRequired, $"Confirm by sending {{\"confirm\":\"{previewId}\"}}", new { expected = previewId }), ct);
        }

        if (Policy.ReadOnlyReason is { } ro)
        {
            throw await DeniedAsync(scope, new AppException(409, ErrorCode.ReadOnly, $"Read-only mode: {ro}"), ct);
        }

        _previews.Remove(previewId);
        var plan = stored.Value.Value;
        var removed = new List<RemovedItemDto>();
        var skipped = new List<SkippedItemDto>();
        long estimated = 0;
        var t0 = System.Diagnostics.Stopwatch.StartNew();

        var (_, report) = await reclaim.MeasureAsync<object?>(
            async _ =>
            {
                var visible = await containers.ListRawAllAsync(true, ct);
                var allNow = (await engine.ListContainersAsync(ct)).Objects().Select(ContainerInfo.From).ToList();
                var usedNow = allNow.Select(c => c.ImageId).ToHashSet(StringComparer.Ordinal);
                var referencedNow = await ownership.ProjectImageRefsAsync(ct);
                var now = Now;
                var rollbackIdsNow = (await rollback.ActiveAsync(ct)).Where(r => r.ExpiresAt > now).Select(r => r.ImageId).ToHashSet(StringComparer.Ordinal);
                var retentionMs = (long)config.RollbackRetention.TotalMilliseconds;
                foreach (var c in plan.Preview.Candidates)
                {
                    var colon = c.Id.IndexOf(':', StringComparison.Ordinal);
                    var kind = c.Id[..colon];
                    var reference = c.Id[(colon + 1)..];
                    var action = kind == "container" ? "container.remove" : "image.remove";
                    try
                    {
                        if (kind == "image")
                        {
                            // re-verify right before removal: a container may have started on it since the preview
                            if (usedNow.Contains(reference))
                            {
                                throw new SkipException("now used by a container");
                            }

                            var info = await images.InspectAsync(reference, ct) ?? throw new SkipException("already gone");
                            var tagsNow = (info.Arr("RepoTags")?.Select(t => t.Str()).OfType<string>() ?? []).Where(t => t != "<none>:<none>").ToList();
                            if (tagsNow.Any(ImageReferences.IsInternalTag))
                            {
                                throw new SkipException("carries a rollback/staged tag");
                            }

                            // RT3-04: the tags are re-read here and ALL of them are removed, so every tag-dependent rule is repeated
                            if (ImageCleanupRules.ScopeProblem(Policy, tagsNow, plan.Filter) is { } sp)
                            {
                                throw new SkipException($"now {sp}");
                            }

                            if (rollbackIdsNow.Contains(reference))
                            {
                                throw new SkipException("now a rollback point within retention");
                            }

                            if (ImageCleanupRules.NamedByProject(tagsNow, referencedNow))
                            {
                                throw new SkipException("now referenced by a discovered Compose project");
                            }

                            foreach (var t in tagsNow)
                            {
                                await images.RemoveAsync(t, ct);
                            }

                            if (await images.InspectAsync(reference, ct) is not null)
                            {
                                await images.RemoveAsync(reference, ct);
                            }
                        }
                        else if (kind == "tag")
                        {
                            var rb = await rollback.GetAsync(reference, ct);
                            if (rb is not null && rb.ExpiresAt > now)
                            {
                                throw new SkipException("rollback point is still within retention");
                            }

                            var st = await staged.GetAsync(reference, ct);
                            if (st is not null && st.CreatedAt + retentionMs > now)
                            {
                                throw new SkipException("staged image is still within retention");
                            }

                            await images.RemoveAsync(reference, ct);
                            await rollback.ReleaseAsync(reference, ct);
                            await staged.DeleteAsync(reference, ct);
                        }
                        else if (kind == "container")
                        {
                            var cont = visible.FirstOrDefault(x => x.Id == reference) ?? throw new SkipException("already gone");
                            if (cont.State is not ("exited" or "created" or "dead"))
                            {
                                throw new SkipException("not stopped anymore");
                            }

                            await containers.ActAsync(reference, "remove", new ActionRequest(Confirm: cont.Name), actor, ct);
                        }

                        removed.Add(new RemovedItemDto(c.Id, c.Name));
                        estimated += c.Size ?? 0;
                        await gateway.RecordAsync(new AuditScope(actor, action, c.Name, null), AuditOutcome.Success, new { via = "cleanup", previewId, id = c.Id, estimatedBytes = c.Size ?? 0 }, null, ct);
                    }
                    catch (Exception e) when (e is SkipException or AppException)
                    {
                        skipped.Add(new SkippedItemDto(c.Id, c.Name, e.Message));
                        await gateway.RecordAsync(new AuditScope(actor, action, c.Name, null), AuditOutcome.Denied, new { via = "cleanup", previewId, id = c.Id, reason = e.Message }, null, ct);
                    }
                }

                // RT3-06: nothing removed => nothing reclaimed, whatever else changed on the host meanwhile
                return new ReclaimRun<object?>(null, estimated, removed.Count == 0 ? 0 : null);
            },
            ct);
        containers.Invalidate();
        await gateway.RecordAsync(scope, AuditOutcome.Success, new { removed = removed.Count, skipped = skipped.Count }, t0.ElapsedMilliseconds, ct);
        return new CleanupResultDto(removed, skipped, report.ReclaimedBytes, report);
    }

    private sealed class SkipException(string message) : Exception(message);

    private async Task<AppException> DeniedAsync(AuditScope scope, AppException e, CancellationToken ct)
    {
        await gateway.RecordAsync(scope, AuditOutcome.Denied, new { error = e.Message }, null, ct);
        return e;
    }

    // ---- single image removal --------------------------------------------------------------------------------------------

    public async Task<ResourceRemoveResultDto> RemoveImageAsync(string reference, string confirm, string? tag, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actor);
        if (!DockerNames.IsSafeRef(reference, RefKind.Image))
        {
            throw AppException.BadRequest("Invalid image reference");
        }

        if (tag is not null && !DockerNames.IsSafeRef(tag, RefKind.Image))
        {
            throw AppException.BadRequest("Invalid tag");
        }

        var scope = new AuditScope(actor, "image.remove", reference, new { via = "single", tag });
        return await gateway.AuditedAsync(scope, () => RemoveImageCoreAsync(scope, reference, confirm, tag, ct), ct);
    }

    private async Task<ResourceRemoveResultDto> RemoveImageCoreAsync(AuditScope scope, string reference, string confirm, string? tag, CancellationToken ct)
    {
        if (Policy.ReadOnlyReason is { } ro)
        {
            throw new AppException(409, ErrorCode.ReadOnly, $"Read-only mode: {ro}");
        }

        var info = await images.InspectAsync(reference, ct) ?? throw AppException.NotFound("Image");
        var id = info.Str("Id") ?? string.Empty;
        var shortId = DockerNames.ShortId(id);
        var tags = (info.Arr("RepoTags")?.Select(t => t.Str()).OfType<string>() ?? []).Where(t => t != "<none>:<none>").ToList();
        if (tag is not null && !tags.Contains(tag))
        {
            throw AppException.Conflict($"Image {shortId} does not carry the tag {tag}");
        }

        var expected = tag ?? (tags.Count > 0 ? tags[0] : shortId);
        if (confirm != expected && confirm != shortId)
        {
            throw AppException.Conflict($"Confirm by sending {{\"confirm\":\"{expected}\"}}", ErrorCode.ConfirmationRequired, new { expected });
        }

        if (ImageCleanupRules.ScopeProblem(Policy, tags, null) is { } sp)
        {
            throw AppException.Conflict($"Refused: {sp}", ErrorCode.OutOfScope);
        }

        var all = (await engine.ListContainersAsync(ct)).Objects().Select(ContainerInfo.From).ToList();
        var now = Now;
        var retentionMs = (long)config.RollbackRetention.TotalMilliseconds;
        var referenced = await ownership.ProjectImageRefsAsync(ct);
        var active = (await rollback.ActiveAsync(ct)).ToDictionary(r => r.Tag, StringComparer.Ordinal);
        async Task<string?> ProtectedTagAsync(string t)
        {
            active.TryGetValue(t, out var rb);
            return ImageCleanupRules.ProtectedTag(t, rb, await staged.GetAsync(t, ct), retentionMs, now, referenced);
        }

        var remaining = tag is null ? [] : tags.Where(t => t != tag).ToList();
        var wholeImage = tag is null || remaining.Count == 0;
        if (!wholeImage)
        {
            if (await ProtectedTagAsync(tag!) is { } why)
            {
                throw AppException.Conflict($"Refused: {why}");
            }

            // RT3-05: compare normalised references. A container created as `nginx` stores Image="nginx" while the tag is "nginx:latest".
            var want = ImageReferences.Parse(tag);
            var byTag = all.Where(c => c.Image == tag || (want is not null && ImageReferences.Parse(c.Image) is { Digest: null } p && ImageReferences.CacheKey(p) == ImageReferences.CacheKey(want)))
                .Select(ContainerLabel).ToList();
            if (byTag.Count > 0)
            {
                throw AppException.Conflict($"Refused: container {string.Join(", ", byTag)} was created from {tag}");
            }
        }
        else
        {
            var users = all.Where(c => c.ImageId == id).Select(ContainerLabel).ToList();
            if (users.Count > 0)
            {
                throw AppException.Conflict($"Refused: image {shortId} is used by {(users.Count == 1 ? "container" : "containers")} {string.Join(", ", users)} (a stopped container counts)");
            }

            if (active.Values.Any(r => r.ExpiresAt > now && r.ImageId == id))
            {
                throw AppException.Conflict($"Refused: image {shortId} is a rollback point within retention");
            }

            foreach (var t in tags)
            {
                if (await ProtectedTagAsync(t) is { } why)
                {
                    throw AppException.Conflict($"Refused: {why}");
                }
            }
        }

        var (_, report) = await reclaim.MeasureAsync<object?>(
            async before =>
            {
                if (!wholeImage)
                {
                    await images.RemoveAsync(tag!, ct);
                    await rollback.ReleaseAsync(tag!, ct);
                    await staged.DeleteAsync(tag!, ct);
                    return new ReclaimRun<object?>(null, 0, 0); // untagging frees nothing (RT3-06)
                }

                foreach (var t in tags)
                {
                    await images.RemoveAsync(t, ct);
                }

                if (await images.InspectAsync(id, ct) is not null)
                {
                    await images.RemoveAsync(id, ct);
                }

                foreach (var t in tags)
                {
                    await rollback.ReleaseAsync(t, ct);
                    await staged.DeleteAsync(t, ct);
                }

                long? size = before is not null && before.Images.TryGetValue(id, out var e) ? e.Size : null;
                long? shared = before is not null && before.Images.TryGetValue(id, out var e2) ? e2.Shared : null;
                return new ReclaimRun<object?>(null, ImageCleanupRules.UniqueSize(size, shared, 0), size);
            },
            ct);
        containers.Invalidate();
        scope.Params = new
        {
            via = "single",
            action = wholeImage ? "deleted" : "untagged",
            tags,
            bytes = report.ReclaimedBytes,
            estimatedBytes = report.EstimatedBytes,
        };
        return new ResourceRemoveResultDto("image", expected, wholeImage ? "deleted" : "untagged", wholeImage ? tags : [tag!], remaining, report);
    }
}

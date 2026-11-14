using FluentValidation;
using Quaymaster.Api.Http;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Storage;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Api.Endpoints;

/// <summary>
/// Phase-2 updates and storage: <c>/updates*</c>, standalone pulls and the staged-image list, image / volume / network removal,
/// image and storage cleanup (preview, then execute) and registry credentials. Routes only validate and delegate: every rule
/// (scope, read-only, self-protection, in-use, Compose ownership, confirmation, TOCTOU re-check, audit) lives in the services.
/// </summary>
public static class UpdateEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var admin = api.MapGroup(string.Empty).RequireCapability(Capability.Write);

        // ---- update detection ------------------------------------------------------------------------------------------------
        api.MapGet("/updates", async (HttpContext http, IUpdateService updates) => new UpdatesDto(await updates.StatusesAsync(false, http.RequestAborted)));

        // Forces a fresh registry lookup (outbound requests, throttled): admin only.
        admin.MapPost("/updates/check", async (HttpContext http, IUpdateService updates, Quaymaster.Application.Safety.IMutationGateway gateway) =>
        {
            var images = await updates.StatusesAsync(true, http.RequestAborted);
            await gateway.RecordAsync(
                new Quaymaster.Application.Safety.AuditScope(http.ActorOf(), "updates.check", "all", null),
                Quaymaster.Domain.Auditing.AuditOutcome.Success,
                new { images = images.Count, updates = images.Count(i => i.State.StartsWith("update", StringComparison.Ordinal)) },
                null,
                http.RequestAborted);
            return new UpdatesDto(images);
        });

        api.MapGet("/updates/quarantine", async (HttpContext http, IQuarantineService quarantine) =>
            new QuarantineListDto([.. (await quarantine.ListAsync(http.RequestAborted)).Select(q =>
                new QuarantineEntryDto(q.Target, q.Digest, q.RunId, q.Reason, Quaymaster.Domain.Registry.ImageReferences.Iso(q.CreatedAt)))]));

        admin.MapPost("/updates/quarantine/clear", async (HttpContext http, IValidator<QuarantineClearBody> validator, IQuarantineService quarantine, Quaymaster.Application.Safety.IMutationGateway gateway) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            var n = await quarantine.ClearAsync(b.Target!, b.Digest, http.RequestAborted);
            await gateway.RecordAsync(
                new Quaymaster.Application.Safety.AuditScope(http.ActorOf(), "updates.quarantine.clear", b.Target, null),
                Quaymaster.Domain.Auditing.AuditOutcome.Success,
                new { digest = b.Digest, cleared = n },
                null,
                http.RequestAborted);
            return new ClearedDto(n);
        });

        // ---- pulls, staged images -------------------------------------------------------------------------------------------
        admin.MapPost("/images/pull", async (HttpContext http, IValidator<ImagePullRequestBody> validator, IPullService pulls) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            var id = await pulls.StartAsync(b.Ref!, http.ActorOf(), http.RequestAborted);
            return Results.Json(new Quaymaster.Application.Docker.OperationIdDto(id), statusCode: StatusCodes.Status202Accepted);
        });

        // registered before GET /images/{reference}: the literal segment wins either way, but keep the intent explicit
        api.MapGet("/images/staged", async (HttpContext http, IStagedStore staged, IRollbackStore rollback) =>
        {
            var stagedList = await staged.ListAsync(http.RequestAborted);
            var points = await rollback.ListAsync(200, http.RequestAborted);
            return new StagedListDto(
                [.. stagedList.Select(s => new StagedImageDto(s.Tag, s.Ref, s.Digest, s.ImageId, Quaymaster.Domain.Registry.ImageReferences.Iso(s.CreatedAt)))],
                [.. points.Select(r => new RollbackPointDto(
                    r.Tag, r.RunId, r.Project, r.Service, r.Ref, r.ImageId,
                    Quaymaster.Domain.Registry.ImageReferences.Iso(r.CreatedAt), Quaymaster.Domain.Registry.ImageReferences.Iso(r.ExpiresAt), r.Released))]);
        });

        // ---- image cleanup: preview is mandatory -----------------------------------------------------------------------------
        admin.MapPost("/images/cleanup/preview", async (HttpContext http, IValidator<CleanupPreviewBody> validator, IImageCleanupService cleanup) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await cleanup.PreviewAsync(new CleanupPreviewRequest(b.Categories, b.MinAgeHours, b.LabelFilter), http.ActorOf(), http.RequestAborted);
        });

        admin.MapPost("/images/cleanup/execute", async (HttpContext http, IValidator<ExecuteBody> validator, IImageCleanupService cleanup) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await cleanup.ExecuteAsync(b.PreviewId!, b.Confirm!, http.ActorOf(), http.RequestAborted);
        });

        admin.MapPost("/images/{reference}/remove", async (HttpContext http, string reference, IValidator<ImageRemoveBody> validator, IImageCleanupService cleanup) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await cleanup.RemoveImageAsync(Uri.UnescapeDataString(reference), b.Confirm!, b.Tag, http.ActorOf(), http.RequestAborted);
        });

        // ---- volumes / networks / storage cleanup ----------------------------------------------------------------------------
        admin.MapPost("/storage/cleanup/preview", async (HttpContext http, IValidator<StoragePreviewBody> validator, IStorageCleanupService storage) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await storage.PreviewAsync(new StoragePreviewRequest(b.Categories!, b.IncludeComposeProjectVolumes, b.MinAgeHours), http.ActorOf(), http.RequestAborted);
        });

        admin.MapPost("/storage/cleanup/execute", async (HttpContext http, IValidator<ExecuteBody> validator, IStorageCleanupService storage) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await storage.ExecuteAsync(b.PreviewId!, b.Confirm!, http.ActorOf(), http.RequestAborted);
        });

        admin.MapPost("/volumes/{name}/remove", async (HttpContext http, string name, IValidator<VolumeRemoveBody> validator, IStorageCleanupService storage) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await storage.RemoveVolumeAsync(name, b.Confirm!, b.AcknowledgeComposeProject == true, http.ActorOf(), http.RequestAborted);
        });

        admin.MapPost("/networks/{reference}/remove", async (HttpContext http, string reference, IValidator<ConfirmBody> validator, IStorageCleanupService storage) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await storage.RemoveNetworkAsync(reference, b.Confirm!, http.ActorOf(), http.RequestAborted);
        });

        // ---- registry credentials (encrypted at rest, never returned; admin only, reads included) ----------------------------------
        admin.MapGet("/registries/credentials", (HttpContext http, IRegistryCredentialService creds) => creds.ListAsync(http.RequestAborted));

        admin.MapPut("/registries/credentials", async (HttpContext http, IValidator<CredentialBody> validator, IRegistryCredentialService creds) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await creds.PutAsync(b.Host!, b.Username!, b.Password!, http.ActorOf(), http.RequestAborted);
        });

        admin.MapDelete("/registries/credentials/{host}", async (HttpContext http, string host, IRegistryCredentialService creds) =>
        {
            await creds.DeleteAsync(host, http.ActorOf(), http.RequestAborted);
            return new OkDto(true);
        });
    }
}

using Quaymaster.Application.Auth;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Registry;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Registry;

namespace Quaymaster.Application.Updates;

public interface IPullService
{
    /// <summary>Starts a standalone pull as an operation (202) and returns the operation id.</summary>
    Task<string> StartAsync(string raw, Actor actor, CancellationToken ct);
}

/// <summary>
/// Standalone pull (RT-07/RT-20). Resolves the registry digest, pulls BY DIGEST and stages the result. It never restarts, recreates
/// or retags anything that is in use. If the tag does not exist locally yet there is nothing to move, so the tag is added too.
/// With a managed scope only references used by an in-scope container (or whose repository name matches the scope) may be pulled.
/// </summary>
public sealed class PullService(
    IMutationGateway gateway,
    IOperationManager operations,
    IContainerService containers,
    IRegistryClient registry,
    IImageOps images,
    IImageChecksStore checks,
    ISystemService system,
    Configuration.QmConfig config) : IPullService
{
    public async Task<string> StartAsync(string raw, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var image = ImageReferences.Parse(raw);
        if (image is null || image.Digest is not null)
        {
            throw AppException.BadRequest("Give a tag reference such as nginx:1.27 (not a digest)");
        }

        var policy = gateway.Policy;
        if (policy.ReadOnlyReason is { } ro)
        {
            throw new AppException(409, ErrorCode.ReadOnly, $"Read-only mode: {ro}");
        }

        var wanted = ImageReferences.Format(image);
        if (policy.Scope.Count > 0)
        {
            var used = policy.InScope(ImageReferences.RepoBaseName(image))
                || (await containers.ListRawAllAsync(true, ct)).Any(c =>
                    policy.ContainerInScope(c.Name, c.Project)
                    && ImageReferences.Parse(c.Image) is { } cr
                    && ImageReferences.Format(cr) == wanted);
            if (!used)
            {
                throw new AppException(403, ErrorCode.OutOfScope, $"Out of managed scope: {wanted} is not used by any in-scope container (QM_MANAGED_SCOPE={string.Join(',', policy.Scope)})");
            }
        }

        return await operations.StartAsync(
            new OperationStart(
                "image.pull",
                wanted,
                actor.Name,
                new { @ref = raw },
                $"pull:{ImageReferences.CacheKey(image)}",
                op => RunAsync(op, image, wanted, raw, actor)),
            ct);
    }

    private async Task RunAsync(IOperationContext op, ImageRef image, string wanted, string raw, Actor actor)
    {
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        var scope = new AuditScope(actor, "image.pull", wanted, new { @ref = raw, op = op.Id });
        try
        {
            var free = await system.DockerRootSpaceAsync(CancellationToken.None)
                ?? throw new AppException(409, ErrorCode.InsufficientDisk, "Free space on the Docker root cannot be determined (mount it and set QM_DOCKER_ROOT_MOUNT); refusing to pull");
            var head = await registry.HeadManifestAsync(image, null, CancellationToken.None);
            var size = 0L;
            try
            {
                size = await registry.CompressedSizeAsync(image, head.Digest, CancellationToken.None);
            }
            catch (RegistryException)
            {
                // size unknown: the floor alone applies
            }

            var floor = Math.Max(config.DiskFloorBytes, (long)Math.Floor(free.Total * 0.1));
            var need = (long)Math.Ceiling(2.5 * size) + floor;
            if (free.Free < need)
            {
                throw new AppException(409, ErrorCode.InsufficientDisk, $"Not enough free disk space: {free.Free / 1048576} MiB free, need {need / 1048576} MiB");
            }

            op.Log($"registry digest for {wanted}: {head.Digest}\n");
            var hadTag = await images.InspectAsync(wanted, CancellationToken.None) is not null;
            var res = await images.PullByDigestAsync(image, head.Digest, op.Log, CancellationToken.None);
            op.Log($"staged as {res.StagedTag}\n");
            if (!hadTag)
            {
                await images.TagAsync(res.ImageId, $"{ImageReferences.EngineName(image)}:{image.Tag}", CancellationToken.None);
                op.Log($"tag {wanted} did not exist locally; added it (nothing was moved)\n");
            }
            else
            {
                op.Log("the existing tag was NOT moved: deploy happens in a maintenance run or an explicit redeploy\n");
            }

            await checks.InvalidateAsync(ImageReferences.CacheKey(image), CancellationToken.None);
            await gateway.RecordAsync(scope, AuditOutcome.Success, new { digest = head.Digest, op = op.Id }, t0.ElapsedMilliseconds, CancellationToken.None);
        }
        catch (Exception e)
        {
            await gateway.RecordAsync(scope, AuditOutcome.Failure, new { error = e.Message, op = op.Id }, t0.ElapsedMilliseconds, CancellationToken.None);
            throw;
        }
    }
}

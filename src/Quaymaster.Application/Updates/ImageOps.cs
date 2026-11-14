using System.Text.Json.Nodes;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Registry;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Updates;

namespace Quaymaster.Application.Updates;

public sealed record PullResult(string ImageId, string Digest, string StagedTag);

/// <summary>
/// The only code that pulls, tags and untags images (RT-03/RT-07/RT-20), always through the mutation gateway:
/// pulls are BY DIGEST (with the stored credentials) and then tagged <c>quaymaster-staged/&lt;hash&gt;:&lt;digest12&gt;</c>; the
/// tag the user uses is never moved here. A pull never starts, stops, recreates or retags anything that is in use.
/// </summary>
public interface IImageOps
{
    Task<JsonObject?> InspectAsync(string nameOrId, CancellationToken ct);

    Task<PullResult> PullByDigestAsync(ImageRef image, string digest, Action<string>? log, CancellationToken ct);

    Task TagAsync(string imageId, string repoTag, CancellationToken ct);

    Task RemoveAsync(string nameOrId, CancellationToken ct);
}

public sealed class ImageOps(
    IDockerEngine engine,
    IMutationGateway gateway,
    IRegistryCredentialStore credentials,
    IStagedStore staged,
    ISystemService system,
    Configuration.QmConfig config,
    TimeProvider time) : IImageOps
{
    private static readonly TimeSpan GuardInterval = TimeSpan.FromMilliseconds(1500);

    public async Task<JsonObject?> InspectAsync(string nameOrId, CancellationToken ct)
    {
        if (!DockerNames.IsSafeRef(nameOrId, RefKind.Image))
        {
            throw AppException.BadRequest("Invalid image reference");
        }

        return await engine.InspectImageAsync(nameOrId, ct);
    }

    private async Task<RegistryAuth?> AuthForAsync(ImageRef image, CancellationToken ct)
    {
        var cred = await credentials.GetAsync(ImageReferences.ApiHost(image), ct) ?? await credentials.GetAsync(image.Registry, ct);
        return cred is null
            ? null
            : new RegistryAuth(cred.Username, cred.Password, image.Registry == "docker.io" ? "https://index.docker.io/v1/" : image.Registry);
    }

    /// <summary>
    /// Pulls <c>repo@digest</c> and stages it. Aborts (leaving whatever layers arrived) when free space on the Docker root falls
    /// below the floor while pulling (RT-13).
    /// </summary>
    public async Task<PullResult> PullByDigestAsync(ImageRef image, string digest, Action<string>? log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        var repo = ImageReferences.EngineName(image with { Tag = null, Digest = null });
        var auth = await AuthForAsync(image, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? aborted = null;
        var guard = Task.Run(
            async () =>
            {
                using var timer = new PeriodicTimer(GuardInterval);
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    var free = (await system.DockerRootSpaceAsync(cts.Token))?.Free;
                    if (free is { } f && f < config.DiskFloorBytes)
                    {
                        aborted = $"free space on the Docker root fell below the floor ({f} < {config.DiskFloorBytes} bytes)";
                        await cts.CancelAsync();
                        return;
                    }
                }
            },
            CancellationToken.None);
        try
        {
            var progress = new Progress<PullProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.Status))
                {
                    log?.Invoke($"{(p.Id is null ? string.Empty : p.Id + ": ")}{p.Status}{(string.IsNullOrEmpty(p.Progress) ? string.Empty : " " + p.Progress)}\n");
                }
            });
            await gateway.PullImageAsync(new ImagePull(repo, digest, auth), progress, cts.Token);
        }
        catch (OperationCanceledException) when (aborted is not null)
        {
            throw new AppException(507, ErrorCode.InsufficientDisk, aborted);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new AppException(502, ErrorCode.RegistryError, $"pull of {repo}@{digest[..19]} failed: {e.Message}");
        }
        finally
        {
            await cts.CancelAsync();
            await guard.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (aborted is not null)
        {
            throw new AppException(507, ErrorCode.InsufficientDisk, aborted);
        }

        var info = await InspectAsync($"{repo}@{digest}", ct)
            ?? throw new AppException(502, ErrorCode.RegistryError, "the pulled image is not present after the pull");
        var id = info.Str("Id") ?? string.Empty;
        var tag = ImageReferences.StagedTag(image, digest);
        var (stagedRepo, stagedTagName) = ImageReferences.SplitRepoTag(tag);
        await gateway.TagImageAsync(id, stagedRepo, stagedTagName, ct);
        await staged.PutAsync(
            new StagedImage(tag, $"{image.Registry}/{image.Repo}:{image.Tag ?? "latest"}", digest, id, time.GetUtcNow().ToUnixTimeMilliseconds()),
            ct);
        return new PullResult(id, digest, tag);
    }

    public Task TagAsync(string imageId, string repoTag, CancellationToken ct)
    {
        var (repo, tag) = ImageReferences.SplitRepoTag(repoTag);
        return gateway.TagImageAsync(imageId, repo, tag, ct);
    }

    public Task RemoveAsync(string nameOrId, CancellationToken ct) => gateway.RemoveImageAsync(nameOrId, ct);
}

using System.Text.Json.Nodes;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Errors;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.Application.Docker;

/// <summary>Read-only image / volume / network inventory (phase 1: list, inspect, usage).</summary>
public interface IInventoryService
{
    Task<IReadOnlyList<ImageSummaryDto>> ListImagesAsync(CancellationToken ct);

    Task<JsonNode?> InspectImageAsync(string reference, CancellationToken ct);

    Task<IReadOnlyList<VolumeSummaryDto>> ListVolumesAsync(CancellationToken ct);

    Task<JsonNode?> InspectVolumeAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<NetworkSummaryDto>> ListNetworksAsync(CancellationToken ct);

    Task<JsonNode?> InspectNetworkAsync(string reference, CancellationToken ct);
}

public sealed class InventoryService(
    IDockerEngine engine,
    IContainerService containers,
    IDiskUsageService usage,
    Scrubber scrubber,
    QmConfig config) : IInventoryService
{
    private static void CheckRef(string reference, RefKind kind)
    {
        if (!DockerNames.IsSafeRef(reference, kind))
        {
            throw AppException.BadRequest($"Invalid {kind.ToString().ToLowerInvariant()} reference");
        }
    }

    public async Task<IReadOnlyList<ImageSummaryDto>> ListImagesAsync(CancellationToken ct)
    {
        var imagesTask = engine.ListImagesAsync(ct);
        var all = await containers.ListRawAllAsync(false, ct);
        var images = (await imagesTask).Objects().ToList();
        var policy = containers.Policy;
        var shown = !policy.VisibilityRestricted
            ? images
            : images.Where(img =>
                all.Any(c => c.ImageId == img.Str("Id"))
                || (img.Arr("RepoTags")?.Select(t => t.Str()).OfType<string>() ?? []).Any(t =>
                    policy.Visible((t.Split(':')[0]).Split('/')[^1]))).ToList();
        return shown.Select(img =>
        {
            var id = img.Str("Id") ?? string.Empty;
            var users = all.Where(c => c.ImageId == id).ToList();
            return new ImageSummaryDto(
                id,
                DockerNames.ShortId(id),
                [.. (img.Arr("RepoTags")?.Select(t => t.Str()).OfType<string>() ?? []).Where(t => t != "<none>:<none>")],
                [.. img.Arr("RepoDigests")?.Select(t => t.Str()).OfType<string>() ?? []],
                img.Num("Size") ?? 0,
                Iso.Format(DateTimeOffset.FromUnixTimeSeconds(img.Num("Created") ?? 0)),
                [.. users.Select(c => new ImageContainerDto(c.Id, c.Name, c.State, c.Project, c.Service))],
                users.Count > 0);
        }).ToList();
    }

    public async Task<JsonNode?> InspectImageAsync(string reference, CancellationToken ct)
    {
        CheckRef(reference, RefKind.Image);
        var raw = await engine.InspectImageAsync(reference, ct) ?? throw AppException.NotFound("Image");
        scrubber.AddAll(Redactor.CollectSecretValues(raw["Config"], config.RedactAllEnv));
        return Redactor.RedactDeep(raw, new RedactOptions(config.RedactAllEnv));
    }

    public async Task<IReadOnlyList<VolumeSummaryDto>> ListVolumesAsync(CancellationToken ct)
    {
        var volsTask = engine.ListVolumesAsync(ct);
        var all = await containers.ListRawAllAsync(false, ct);
        var res = await volsTask;
        var sizes = usage.Cached?.Volumes;
        var policy = containers.Policy;
        var vols = res.Arr("Volumes").Objects().Where(v =>
        {
            var labels = v.Obj("Labels").StringMap();
            return policy.Visible(v.Str("Name") ?? string.Empty)
                || (labels.TryGetValue(ComposeLabels.Project, out var p) && policy.Visible(p));
        });
        return vols.Select(v =>
        {
            var name = v.Str("Name") ?? string.Empty;
            var labels = v.Obj("Labels").StringMap();
            var users = all.Where(c => c.Mounts.Any(m => m.Type == "volume" && m.Name == name)).Select(c => c.Name).ToList();
            long? size = sizes is not null && sizes.TryGetValue(name, out var s) ? s : null;
            return new VolumeSummaryDto(
                name,
                v.Str("Driver") ?? string.Empty,
                v.Str("Mountpoint") ?? string.Empty,
                v.Str("CreatedAt"),
                Redactor.RedactLabels(labels),
                labels.GetValueOrDefault(ComposeLabels.Project),
                users,
                name.Length == 64 && name.All(char.IsAsciiHexDigitLower) && !labels.ContainsKey(ComposeLabels.Project),
                size);
        }).ToList();
    }

    public async Task<JsonNode?> InspectVolumeAsync(string name, CancellationToken ct)
    {
        CheckRef(name, RefKind.Volume);
        var raw = await engine.InspectVolumeAsync(name, ct) ?? throw AppException.NotFound("Volume");
        return Redactor.RedactDeep(raw);
    }

    public async Task<IReadOnlyList<NetworkSummaryDto>> ListNetworksAsync(CancellationToken ct)
    {
        var netsTask = engine.ListNetworksAsync(ct);
        var all = await containers.ListRawAllAsync(false, ct);
        var nets = (await netsTask).Objects().ToList();
        var policy = containers.Policy;
        return nets
            .Where(n =>
            {
                var labels = n.Obj("Labels").StringMap();
                return policy.Visible(n.Str("Name") ?? string.Empty)
                    || (labels.TryGetValue(ComposeLabels.Project, out var p) && policy.Visible(p));
            })
            .Select(n =>
            {
                var name = n.Str("Name") ?? string.Empty;
                var id = n.Str("Id") ?? string.Empty;
                var labels = n.Obj("Labels").StringMap();
                var members = all
                    .Where(c => c.Networks.Any(kv => kv.Key == name || kv.Value == id))
                    .Select(c => c.Name)
                    .ToList();
                return new NetworkSummaryDto(
                    id,
                    name,
                    n.Str("Driver") ?? string.Empty,
                    n.Str("Scope") ?? string.Empty,
                    n["Internal"].Bool() == true,
                    labels.GetValueOrDefault(ComposeLabels.Project),
                    members,
                    Redactor.RedactLabels(labels));
            }).ToList();
    }

    public async Task<JsonNode?> InspectNetworkAsync(string reference, CancellationToken ct)
    {
        CheckRef(reference, RefKind.Network);
        var raw = await engine.InspectNetworkAsync(reference, ct) ?? throw AppException.NotFound("Network");
        return Redactor.RedactDeep(raw);
    }
}

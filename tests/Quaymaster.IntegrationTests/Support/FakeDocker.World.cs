using System.Text.Json.Nodes;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Domain.Stats;

namespace Quaymaster.IntegrationTests.Support;

/// <summary>The mutable image / volume / network world of <see cref="FakeDocker"/> plus the T3 mutation seams.</summary>
public sealed partial class FakeDocker
{
    public sealed class Img
    {
        public required string Id { get; init; }

        public List<string> Tags { get; init; } = [];

        public List<string> RepoDigests { get; init; } = [];

        public long Size { get; set; } = 10_000;

        public long Shared { get; set; }

        public DateTimeOffset Created { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        public Dictionary<string, string> Labels { get; init; } = [];
    }

    public static readonly string NginxId = "sha256:" + new string('e', 64);

    public List<Img> ImageList { get; } =
    [
        new Img { Id = NginxId, Tags = ["nginx:1.27"], RepoDigests = ["nginx@sha256:" + new string('d', 64)], Size = 123456 },
    ];

    public List<string> ImageRefsAsked { get; } = [];

    /// <summary>Everything the mutation seam was asked to do, in order (<c>pull</c>, <c>tag</c>, <c>rmi</c>, <c>rmv</c>, <c>rmn</c>).</summary>
    public List<string> Mutations { get; } = [];

    public List<RegistryAuthSeen> PullAuths { get; } = [];

    public sealed record RegistryAuthSeen(string Repository, string Username, string ServerAddress);

    /// <summary>When set, every pull fails with this registry error.</summary>
    public string? PullFailure { get; set; }

    public Dictionary<string, string> DistributionDigests { get; } = new(StringComparer.Ordinal);

    public long TotalBytes { get; set; } = 1000;

    public long FreeBytes { get; set; } = 400;

    public Dictionary<string, long> VolumeSizes { get; } = new(StringComparer.Ordinal);

    public JsonArray VolumeList { get; } =
    [
        new JsonObject
        {
            ["Name"] = "shop_data",
            ["Driver"] = "local",
            ["Mountpoint"] = "/var/lib/docker/volumes/shop_data/_data",
            ["CreatedAt"] = "2026-01-01T00:00:00Z",
            ["Labels"] = new JsonObject { ["com.docker.compose.project"] = "shop" },
            ["Scope"] = "local",
        },
    ];

    public JsonArray NetworkList { get; } =
    [
        new JsonObject
        {
            ["Id"] = new string('9', 64),
            ["Name"] = "shop_default",
            ["Driver"] = "bridge",
            ["Scope"] = "local",
            ["Internal"] = false,
            ["Labels"] = new JsonObject { ["com.docker.compose.project"] = "shop" },
            ["IPAM"] = new JsonObject { ["Config"] = new JsonArray() },
            ["Created"] = "2026-01-01T00:00:00Z",
        },
    ];

    public static string ShortOf(string id) => id.Replace("sha256:", string.Empty, StringComparison.Ordinal)[..12];

    // ---- images -----------------------------------------------------------------------------------------------------

    private Img? FindImage(string reference) =>
        ImageList.FirstOrDefault(i =>
            i.Id == reference
            || i.Id.StartsWith("sha256:" + reference, StringComparison.Ordinal)
            || i.Tags.Contains(reference)
            || i.RepoDigests.Contains(reference));

    public Task<JsonArray> ListImagesAsync(CancellationToken ct)
    {
        var array = new JsonArray();
        foreach (var i in ImageList)
        {
            array.Add(new JsonObject
            {
                ["Id"] = i.Id,
                ["RepoTags"] = new JsonArray(i.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["RepoDigests"] = new JsonArray(i.RepoDigests.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["Created"] = i.Created.ToUnixTimeSeconds(),
                ["Size"] = i.Size,
                [ContainersKey] = Containers.OfType<JsonObject>().Count(c => c["ImageID"]!.GetValue<string>() == i.Id),
            });
        }

        return Task.FromResult(array);
    }

    public Task<JsonObject?> InspectImageAsync(string reference, CancellationToken ct)
    {
        ImageRefsAsked.Add(reference);
        if (reference is "nginx:1.27" or "reg.io/team/app:tag" or "sha256:" + "eeee")
        {
            return Task.FromResult<JsonObject?>(new JsonObject
            {
                ["Id"] = NginxId,
                ["RepoTags"] = new JsonArray(reference),
                ["RepoDigests"] = new JsonArray("nginx@sha256:" + new string('d', 64)),
                ["Created"] = "2026-01-01T00:00:00Z",
                ["Size"] = 123456,
                ["Config"] = new JsonObject { ["Env"] = new JsonArray("API_TOKEN=" + Secret), ["Labels"] = new JsonObject() },
            });
        }

        var img = FindImage(reference);
        return Task.FromResult<JsonObject?>(img is null
            ? null
            : new JsonObject
            {
                ["Id"] = img.Id,
                ["RepoTags"] = new JsonArray(img.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["RepoDigests"] = new JsonArray(img.RepoDigests.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["Created"] = img.Created.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["Size"] = img.Size,
                ["Config"] = new JsonObject
                {
                    ["Env"] = new JsonArray(),
                    ["Labels"] = new JsonObject(img.Labels.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value)).ToArray()),
                },
                ["Metadata"] = new JsonObject { ["LastTagTime"] = "0001-01-01T00:00:00Z" },
            });
    }

    public Task<string?> DistributionDigestAsync(string name, CancellationToken ct)
    {
        Up();
        return Task.FromResult(DistributionDigests.TryGetValue(name, out var d) ? d : null);
    }

    // ---- volumes / networks -------------------------------------------------------------------------------------------

    public Task<JsonObject> ListVolumesAsync(CancellationToken ct) =>
        Task.FromResult(new JsonObject { ["Volumes"] = VolumeList.DeepClone() });

    public Task<JsonObject?> InspectVolumeAsync(string name, CancellationToken ct)
    {
        if (name == "shop_data")
        {
            return Task.FromResult<JsonObject?>(new JsonObject { ["Name"] = "shop_data", ["Driver"] = "local", ["Mountpoint"] = "/x", ["Labels"] = new JsonObject(), ["Scope"] = "local" });
        }

        var hit = VolumeList.OfType<JsonObject>().FirstOrDefault(v => v["Name"]!.GetValue<string>() == name);
        return Task.FromResult(hit?.DeepClone() as JsonObject);
    }

    public Task<JsonArray> ListNetworksAsync(CancellationToken ct) => Task.FromResult((JsonArray)NetworkList.DeepClone());

    public Task<JsonObject?> InspectNetworkAsync(string reference, CancellationToken ct)
    {
        if (reference == "shop_default")
        {
            return Task.FromResult<JsonObject?>(new JsonObject { ["Id"] = new string('9', 64), ["Name"] = "shop_default", ["Driver"] = "bridge", [ContainersKey] = new JsonObject(), ["Labels"] = new JsonObject() });
        }

        var hit = NetworkList.OfType<JsonObject>().FirstOrDefault(n => n["Name"]!.GetValue<string>() == reference || n["Id"]!.GetValue<string>() == reference);
        if (hit?.DeepClone() is not JsonObject clone)
        {
            return Task.FromResult<JsonObject?>(null);
        }

        var members = new JsonObject();
        foreach (var c in Containers.OfType<JsonObject>())
        {
            if (c["NetworkSettings"]?["Networks"] is JsonObject nets && nets.ContainsKey(clone["Name"]!.GetValue<string>()) && c["State"]!.GetValue<string>() == "running")
            {
                members[c["Id"]!.GetValue<string>()] = new JsonObject();
            }
        }

        clone[ContainersKey] = members;
        return Task.FromResult<JsonObject?>(clone);
    }

    public Task<JsonObject> DiskUsageAsync(string type, CancellationToken ct)
    {
        Interlocked.Increment(ref _dfCalls);
        return Task.FromResult(type switch
        {
            "container" => new JsonObject { ["ContainersDiskUsage"] = new JsonObject { ["TotalSize"] = 10, ["Reclaimable"] = 1, ["TotalCount"] = 3, ["ActiveCount"] = 2 } },
            "image" => new JsonObject
            {
                ["ImagesDiskUsage"] = new JsonObject { ["TotalSize"] = 100, ["Reclaimable"] = 50, ["TotalCount"] = 1, ["ActiveCount"] = 1 },
                ["LayersSize"] = ImageList.Sum(i => i.Size - i.Shared),
                ["Images"] = new JsonArray(ImageList.Select(i => (JsonNode)new JsonObject
                {
                    ["Id"] = i.Id,
                    ["Size"] = i.Size,
                    ["SharedSize"] = i.Shared,
                    [ContainersKey] = Containers.OfType<JsonObject>().Count(c => c["ImageID"]!.GetValue<string>() == i.Id),
                }).ToArray()),
            },
            "volume" => new JsonObject
            {
                ["VolumesDiskUsage"] = new JsonObject { ["TotalSize"] = 20, ["Reclaimable"] = 0, ["TotalCount"] = 1, ["ActiveCount"] = 1 },
                ["Volumes"] = new JsonArray(VolumeList.OfType<JsonObject>().Select(v => (JsonNode)new JsonObject
                {
                    ["Name"] = v["Name"]!.GetValue<string>(),
                    ["UsageData"] = new JsonObject { ["Size"] = VolumeSizes.GetValueOrDefault(v["Name"]!.GetValue<string>(), 1000), ["RefCount"] = 0 },
                }).ToArray()),
            },
            _ => new JsonObject { ["BuildCacheDiskUsage"] = new JsonObject { ["TotalSize"] = 5, ["Reclaimable"] = 5, ["TotalCount"] = 0, ["ActiveCount"] = 0 } },
        });
    }

    public (long Total, long Free) Statfs(string path) => (TotalBytes, FreeBytes);

    // ---- IDockerCommands ------------------------------------------------------------------------------------------------

    public Task<bool> ExecuteAsync(ContainerCommand command, CancellationToken ct)
    {
        lock (Commands)
        {
            Commands.Add(command);
        }

        return Task.FromResult(true);
    }

    public Task<string> CreateContainerAsync(string name, JsonObject body, CancellationToken ct)
    {
        lock (Mutations)
        {
            Mutations.Add($"create {name} image={body["Image"]}");
        }

        return Task.FromResult(CloneId);
    }

    public Task ConnectNetworkAsync(string network, string containerId, JsonObject endpoint, CancellationToken ct)
    {
        lock (Mutations)
        {
            Mutations.Add($"connect {network} {ShortOf(containerId)}");
        }

        return Task.CompletedTask;
    }

    public Task RenameContainerAsync(string containerId, string newName, CancellationToken ct)
    {
        lock (Mutations)
        {
            Mutations.Add($"rename {ShortOf(containerId)} {newName}");
        }

        return Task.CompletedTask;
    }

    public Task SetRestartPolicyAsync(string containerId, string policy, int maximumRetryCount, CancellationToken ct)
    {
        lock (Mutations)
        {
            Mutations.Add($"restart-policy {ShortOf(containerId)} {policy}:{maximumRetryCount}");
        }

        return Task.CompletedTask;
    }

    /// <summary>The id the fake daemon hands out for a created clone.</summary>
    public static readonly string CloneId = new('c', 64);

    public Task PullImageAsync(ImagePull request, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        Up();
        lock (Mutations)
        {
            Mutations.Add($"pull {request.Repository}@{request.Digest}");
            if (request.Auth is { } a)
            {
                PullAuths.Add(new RegistryAuthSeen(request.Repository, a.Username, a.ServerAddress));
            }
        }

        if (PullFailure is not null)
        {
            throw new AppException(502, ErrorCode.RegistryError, PullFailure);
        }

        progress?.Report(new PullProgress("Pulling fs layer", "abc123", null));
        var rd = $"{request.Repository}@{request.Digest}";
        if (FindImage(rd) is null)
        {
            ImageList.Add(new Img { Id = "sha256:" + request.Digest.Replace("sha256:", string.Empty, StringComparison.Ordinal), RepoDigests = [rd], Size = 5000 });
        }

        return Task.CompletedTask;
    }

    public Task TagImageAsync(string imageId, string repo, string tag, CancellationToken ct)
    {
        lock (Mutations)
        {
            Mutations.Add($"tag {ShortOf(imageId)} {repo}:{tag}");
        }

        var img = FindImage(imageId) ?? throw AppException.NotFound("Image");
        img.Tags.Add($"{repo}:{tag}");
        return Task.CompletedTask;
    }

    private bool ImageInUse(Img img) => Containers.OfType<JsonObject>().Any(c => c["ImageID"]!.GetValue<string>() == img.Id);

    public Task RemoveImageAsync(string nameOrId, CancellationToken ct)
    {
        lock (Mutations)
        {
            Mutations.Add($"rmi {nameOrId}");
        }

        var img = FindImage(nameOrId) ?? throw AppException.NotFound("Image");
        if (img.Tags.Contains(nameOrId) && img.Tags.Count > 1)
        {
            img.Tags.Remove(nameOrId); // untag only
            return Task.CompletedTask;
        }

        if (ImageInUse(img))
        {
            throw AppException.Conflict($"conflict: unable to delete {ShortOf(img.Id)} (cannot be forced) - image is being used by a container");
        }

        ImageList.Remove(img);
        FreeBytes += img.Size - img.Shared;
        return Task.CompletedTask;
    }

    public Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        lock (Mutations)
        {
            Mutations.Add($"rmv {name}");
        }

        var hit = VolumeList.OfType<JsonObject>().FirstOrDefault(v => v["Name"]!.GetValue<string>() == name) ?? throw AppException.NotFound("Volume");
        if (Containers.OfType<JsonObject>().Any(c => c["Mounts"] is JsonArray m && m.OfType<JsonObject>().Any(x => x["Name"]?.GetValue<string>() == name)))
        {
            throw AppException.Conflict("volume is in use");
        }

        VolumeList.Remove(hit);
        FreeBytes += VolumeSizes.GetValueOrDefault(name, 1000);
        return Task.CompletedTask;
    }

    public Task RemoveNetworkAsync(string id, CancellationToken ct)
    {
        lock (Mutations)
        {
            Mutations.Add($"rmn {id}");
        }

        var hit = NetworkList.OfType<JsonObject>().FirstOrDefault(n => n["Id"]!.GetValue<string>() == id) ?? throw AppException.NotFound("Network");
        NetworkList.Remove(hit);
        return Task.CompletedTask;
    }
}

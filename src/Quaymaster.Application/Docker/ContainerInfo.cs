using System.Text.Json.Nodes;
using Quaymaster.Domain.Docker;

namespace Quaymaster.Application.Docker;

public sealed record MountInfo(string? Type, string? Name);

/// <summary>A parsed <c>GET /containers/json</c> entry.</summary>
public sealed record ContainerInfo(
    string Id,
    string Name,
    string Image,
    string ImageId,
    string State,
    string Status,
    long Created,
    IReadOnlyList<PortInfoDto> Ports,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<MountInfo> Mounts,
    IReadOnlyDictionary<string, string?> Networks)
{
    public string? Project => Labels.GetValueOrDefault(ComposeLabels.Project) is { Length: > 0 } p ? p : null;

    public string? Service => Labels.GetValueOrDefault(ComposeLabels.Service) is { Length: > 0 } s ? s : null;

    public static ContainerInfo From(JsonObject c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var id = c.Str("Id") ?? string.Empty;
        var names = c.Arr("Names");
        var first = names is { Count: > 0 } ? names[0].Str() : null;
        var ports = c.Arr("Ports").Objects()
            .Select(p => new PortInfoDto(p.Str("IP"), (int)(p.Num("PrivatePort") ?? 0), (int?)p.Num("PublicPort"), p.Str("Type") ?? "tcp"))
            .ToList();
        var mounts = c.Arr("Mounts").Objects().Select(m => new MountInfo(m.Str("Type"), m.Str("Name"))).ToList();
        var networks = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (c.Obj("NetworkSettings")?.Obj("Networks") is { } nets)
        {
            foreach (var (name, v) in nets)
            {
                networks[name] = v.Str("NetworkID");
            }
        }

        return new ContainerInfo(
            id,
            DockerNames.CleanName(first, id),
            c.Str("Image") ?? string.Empty,
            c.Str("ImageID") ?? string.Empty,
            c.Str("State") ?? string.Empty,
            c.Str("Status") ?? string.Empty,
            c.Num("Created") ?? 0,
            ports,
            c.Obj("Labels").StringMap(),
            mounts,
            networks);
    }
}

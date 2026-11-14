using System.Text.Json.Nodes;
using Quaymaster.Domain.Stats;

namespace Quaymaster.Application.Docker;

/// <param name="Version">Engine version (29.x).</param>
/// <param name="DaemonApiVersion">Highest API version the daemon speaks.</param>
/// <param name="MinApiVersion">Lowest API version the daemon still accepts.</param>
/// <param name="NegotiatedApiVersion">What Quaymaster uses: min(daemon, tested maximum).</param>
public sealed record EngineVersion(string Version, string DaemonApiVersion, string? MinApiVersion, string NegotiatedApiVersion);

/// <summary>One demultiplexed chunk of a container log stream.</summary>
public sealed record LogFrame(bool Stderr, ReadOnlyMemory<byte> Data);

public sealed record LogRequest(bool Follow, int? Tail, long? Since, bool Timestamps);

/// <summary>
/// Read side of the Docker Engine API. Everything returns the Engine's own JSON (PascalCase) so inspect-style endpoints
/// pass the daemon's shape through unchanged (after redaction). Mutations are NOT here: see <see cref="IDockerCommands"/>.
/// Implementations map transport failures to <c>docker_unavailable</c> and unknown resources to <c>not_found</c>.
/// </summary>
public interface IDockerEngine
{
    Task<EngineVersion> GetVersionAsync(CancellationToken ct);

    Task PingAsync(CancellationToken ct);

    /// <summary>All containers (<c>GET /containers/json?all=1</c>).</summary>
    Task<JsonArray> ListContainersAsync(CancellationToken ct);

    /// <summary>Inspect by id, id prefix or name. Null when the daemon says 404.</summary>
    Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct);

    Task<JsonObject> InfoAsync(CancellationToken ct);

    Task<JsonArray> ListImagesAsync(CancellationToken ct);

    Task<JsonObject?> InspectImageAsync(string reference, CancellationToken ct);

    /// <summary><c>{"Volumes":[...],"Warnings":...}</c> as the daemon returns it.</summary>
    Task<JsonObject> ListVolumesAsync(CancellationToken ct);

    Task<JsonObject?> InspectVolumeAsync(string name, CancellationToken ct);

    Task<JsonArray> ListNetworksAsync(CancellationToken ct);

    Task<JsonObject?> InspectNetworkAsync(string reference, CancellationToken ct);

    /// <summary><c>GET /system/df?type=</c> (one type per request: the unfiltered call can take minutes).</summary>
    Task<JsonObject> DiskUsageAsync(string type, CancellationToken ct);

    /// <summary>Container logs. For a TTY container everything arrives as stdout.</summary>
    IAsyncEnumerable<LogFrame> LogsAsync(string containerId, bool tty, LogRequest request, CancellationToken ct);

    /// <summary>One stats sample, or null when the container vanished.</summary>
    Task<RawStats?> StatsOnceAsync(string containerId, CancellationToken ct);

    /// <summary>
    /// <c>GET /distribution/{name}/json</c>: the digest the registry serves for <paramref name="name"/> (<c>repo:tag</c>), asked through the
    /// daemon with the daemon's own credentials. Null when the daemon cannot tell. Read-only.
    /// </summary>
    Task<string?> DistributionDigestAsync(string name, CancellationToken ct);

    /// <summary>Long-lived <c>/events</c> stream (container/image/volume/network). Ends when the daemon closes it.</summary>
    IAsyncEnumerable<JsonObject> EventsAsync(DateTimeOffset since, CancellationToken ct);
}

public enum ContainerAction
{
    Start,
    Stop,
    Restart,
    Kill,
    Pause,
    Unpause,
    Remove,
}

public sealed record ContainerCommand(string ContainerId, ContainerAction Action, int? TimeoutSeconds = null, string? Signal = null);

/// <summary>
/// Engine mutations. Only <c>MutationGateway</c> may depend on this interface (enforced by an architecture test):
/// it refuses anything but a full 64-hex id, never forces and never removes volumes.
/// </summary>
public interface IDockerCommands
{
    /// <summary>Pulls <c>repository@digest</c> (never a moving tag). Throws <c>registry_error</c> when the pull fails.</summary>
    Task PullImageAsync(ImagePull request, IProgress<PullProgress>? progress, CancellationToken ct);

    /// <summary>Adds <c>repo:tag</c> to an image. Never removes anything.</summary>
    Task TagImageAsync(string imageId, string repo, string tag, CancellationToken ct);

    /// <summary>Removes a tag (or the image when given an id). Never forced: Docker refuses an image a container uses (409).</summary>
    Task RemoveImageAsync(string nameOrId, CancellationToken ct);

    /// <summary>Never forced.</summary>
    Task RemoveVolumeAsync(string name, CancellationToken ct);

    Task RemoveNetworkAsync(string id, CancellationToken ct);

    /// <summary>Returns false when the container was already in the requested state (HTTP 304).</summary>
    Task<bool> ExecuteAsync(ContainerCommand command, CancellationToken ct);

    /// <summary>Standalone config clone: <c>POST /containers/create?name=</c> with the Engine's own body. Returns the new full id; the clone is never started here.</summary>
    Task<string> CreateContainerAsync(string name, System.Text.Json.Nodes.JsonObject body, CancellationToken ct);

    /// <summary><c>POST /networks/{network}/connect</c> for a (stopped) clone; <paramref name="endpoint"/> is the <c>EndpointConfig</c>.</summary>
    Task ConnectNetworkAsync(string network, string containerId, System.Text.Json.Nodes.JsonObject endpoint, CancellationToken ct);

    Task RenameContainerAsync(string containerId, string newName, CancellationToken ct);

    /// <summary><c>POST /containers/{id}/update</c> with only a restart policy.</summary>
    Task SetRestartPolicyAsync(string containerId, string policy, int maximumRetryCount, CancellationToken ct);
}

public sealed record ImagePull(string Repository, string Digest, RegistryAuth? Auth);

public sealed record RegistryAuth(string Username, string Password, string ServerAddress);

public sealed record PullProgress(string? Status, string? Id, string? Progress);

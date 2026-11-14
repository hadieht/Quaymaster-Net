using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Docker.DotNet;
using Docker.DotNet.Models;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Stats;
using ContainerAction = Quaymaster.Application.Docker.ContainerAction;

namespace Quaymaster.Infrastructure.Docker;

/// <summary>
/// The Docker Engine adapter. Reads go through a small GET-only HTTP client so the daemon's JSON is passed through
/// untouched; lifecycle commands, ping and version go through <b>Docker.DotNet</b> (typed, by full container id only).
/// The Engine API version is negotiated once: min(daemon, tested maximum).
/// </summary>
internal sealed class DockerEngineAdapter : IDockerEngine, IDockerCommands, IDisposable
{
    private readonly string _socket;
    private readonly DockerHttp _http;
    private readonly SemaphoreSlim _negotiate = new(1, 1);
    private DockerClient? _client;
    private EngineVersion? _version;

    public DockerEngineAdapter(QmConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _socket = config.DockerSocket;
        _http = new DockerHttp(_socket);
    }

    // ---- connection / version ---------------------------------------------------------------------------------------

    private Uri DockerUri
    {
        get
        {
            var tcp = DockerHttp.TcpTarget(_socket);
            if (tcp is not null)
            {
                return new Uri($"http://{tcp}");
            }

            return new Uri(_socket.StartsWith("unix://", StringComparison.Ordinal) ? _socket : $"unix://{_socket}");
        }
    }

    private async Task<DockerClient> ClientAsync(CancellationToken ct)
    {
        await GetVersionAsync(ct);
        return _client!;
    }

    public async Task<EngineVersion> GetVersionAsync(CancellationToken ct)
    {
        if (_version is not null)
        {
            return _version;
        }

        await _negotiate.WaitAsync(ct);
        try
        {
            if (_version is not null)
            {
                return _version;
            }

            // first request is unversioned; afterwards the negotiated prefix is pinned
            _http.ApiPrefix = null;
            var v = await _http.GetJsonAsync("/version", ct) ?? throw new AppException(503, ErrorCode.DockerUnavailable, "Docker daemon returned no version");
            var daemonApi = v.Str("ApiVersion") ?? "1.40";
            var min = v.Str("MinAPIVersion");
            var chosen = ApiVersioning.Negotiate(daemonApi, min);
            _http.ApiPrefix = $"v{chosen}";
            _client?.Dispose();
            _client = new DockerClientBuilder().WithEndpoint(DockerUri).WithApiVersion(System.Version.Parse(chosen)).WithTimeout(TimeSpan.FromMinutes(2)).Build();
            _version = new EngineVersion(v.Str("Version") ?? "unknown", daemonApi, min, chosen);
            return _version;
        }
        finally
        {
            _negotiate.Release();
        }
    }

    public async Task PingAsync(CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        try
        {
            await client.System.PingAsync(ct);
        }
        catch (Exception e) when (e is not AppException && DockerHttp.IsTransport(e, ct))
        {
            throw DockerHttp.Unavailable(e);
        }
    }

    // ---- reads (raw JSON) ---------------------------------------------------------------------------------------------

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct, TimeSpan? timeout = null)
    {
        await GetVersionAsync(ct);
        return await _http.GetJsonAsync(path, ct, timeout);
    }

    public async Task<JsonArray> ListContainersAsync(CancellationToken ct) =>
        await GetAsync("/containers/json?all=1", ct) as JsonArray ?? [];

    public async Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct) =>
        await GetAsync($"/containers/{Uri.EscapeDataString(idOrName)}/json", ct) as JsonObject;

    public async Task<JsonObject> InfoAsync(CancellationToken ct) =>
        await GetAsync("/info", ct) as JsonObject ?? [];

    public async Task<JsonArray> ListImagesAsync(CancellationToken ct) =>
        await GetAsync("/images/json?all=0", ct) as JsonArray ?? [];

    public async Task<JsonObject?> InspectImageAsync(string reference, CancellationToken ct) =>
        await GetAsync($"/images/{EscapePath(reference)}/json", ct) as JsonObject;

    public async Task<JsonObject> ListVolumesAsync(CancellationToken ct) =>
        await GetAsync("/volumes", ct) as JsonObject ?? [];

    public async Task<JsonObject?> InspectVolumeAsync(string name, CancellationToken ct) =>
        await GetAsync($"/volumes/{Uri.EscapeDataString(name)}", ct) as JsonObject;

    public async Task<JsonArray> ListNetworksAsync(CancellationToken ct) =>
        await GetAsync("/networks", ct) as JsonArray ?? [];

    public async Task<JsonObject?> InspectNetworkAsync(string reference, CancellationToken ct) =>
        await GetAsync($"/networks/{Uri.EscapeDataString(reference)}", ct) as JsonObject;

    public async Task<JsonObject> DiskUsageAsync(string type, CancellationToken ct)
    {
        if (type is not ("image" or "container" or "volume"))
        {
            throw new AppException(500, ErrorCode.Internal, "Unknown df type");
        }

        // `system df` walks every volume: allow minutes
        return await GetAsync($"/system/df?type={type}", ct, TimeSpan.FromMinutes(10)) as JsonObject ?? [];
    }

    /// <summary>Image references may contain "/" (registry paths); each segment is escaped, the slashes stay.</summary>
    private static string EscapePath(string reference) => string.Join('/', reference.Split('/').Select(Uri.EscapeDataString));

    // ---- logs ------------------------------------------------------------------------------------------------------

    public async IAsyncEnumerable<LogFrame> LogsAsync(string containerId, bool tty, LogRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await GetVersionAsync(ct);
        var query = new StringBuilder($"/containers/{Uri.EscapeDataString(containerId)}/logs?stdout=1&stderr=1");
        query.Append(request.Follow ? "&follow=1" : "&follow=0");
        query.Append(request.Timestamps ? "&timestamps=1" : "&timestamps=0");
        if (request.Tail is { } tail)
        {
            query.Append(System.Globalization.CultureInfo.InvariantCulture, $"&tail={tail}");
        }

        if (request.Since is { } since)
        {
            query.Append(System.Globalization.CultureInfo.InvariantCulture, $"&since={since}");
        }

        using var response = await _http.OpenStreamAsync(query.ToString(), ct) ?? throw AppException.NotFound("Container");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var header = new byte[8];
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read;
            if (tty)
            {
                read = await ReadSomeAsync(stream, buffer, ct);
                if (read == 0)
                {
                    yield break;
                }

                yield return new LogFrame(false, buffer.AsMemory(0, read).ToArray());
                continue;
            }

            if (!await ReadExactAsync(stream, header, ct))
            {
                yield break;
            }

            var size = (int)(((uint)header[4] << 24) | ((uint)header[5] << 16) | ((uint)header[6] << 8) | header[7]);
            var type = header[0];
            if (type > 2 || size < 0 || size > 64 * 1024 * 1024)
            {
                yield break; // not a multiplexed stream after all, or corrupt
            }

            var payload = new byte[size];
            if (!await ReadExactAsync(stream, payload, ct))
            {
                yield break;
            }

            yield return new LogFrame(type == 2, payload);
        }
    }

    private static async Task<int> ReadSomeAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        try
        {
            return await stream.ReadAsync(buffer, ct);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            return 0;
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buffer.AsMemory(total), ct);
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                return false;
            }

            if (n == 0)
            {
                return false;
            }

            total += n;
        }

        return true;
    }

    // ---- stats -----------------------------------------------------------------------------------------------------

    public async Task<RawStats?> StatsOnceAsync(string containerId, CancellationToken ct)
    {
        var node = await GetAsync($"/containers/{Uri.EscapeDataString(containerId)}/stats?stream=false&one-shot=true", ct, TimeSpan.FromSeconds(20));
        if (node is not JsonObject s)
        {
            return null;
        }

        var mem = s.Obj("memory_stats");
        var memStats = mem?.Obj("stats");
        long rx = 0;
        long tx = 0;
        foreach (var (_, n) in s.Obj("networks") ?? [])
        {
            rx += n.Num("rx_bytes") ?? 0;
            tx += n.Num("tx_bytes") ?? 0;
        }

        long br = 0;
        long bw = 0;
        foreach (var e in s.Obj("blkio_stats")?.Arr("io_service_bytes_recursive").Objects() ?? [])
        {
            var op = (e.Str("op") ?? string.Empty).ToLowerInvariant();
            if (op == "read")
            {
                br += e.Num("value") ?? 0;
            }
            else if (op == "write")
            {
                bw += e.Num("value") ?? 0;
            }
        }

        var readText = s.Str("read");
        var read = readText is not null && DateTimeOffset.TryParse(readText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        return new RawStats(
            read,
            s.Obj("cpu_stats")?.Obj("cpu_usage")?.Num("total_usage") ?? 0,
            mem?.Num("usage") ?? 0,
            mem?.Num("limit") ?? 0,
            memStats?.Num("inactive_file") ?? memStats?.Num("total_inactive_file") ?? memStats?.Num("cache") ?? 0,
            rx,
            tx,
            br,
            bw);
    }

    // ---- events ----------------------------------------------------------------------------------------------------

    public async IAsyncEnumerable<JsonObject> EventsAsync(DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct)
    {
        await GetVersionAsync(ct);
        var filters = Uri.EscapeDataString("{\"type\":[\"container\",\"image\",\"volume\",\"network\"]}");
        using var response = await _http.OpenStreamAsync($"/events?since={since.ToUnixTimeSeconds()}&filters={filters}", ct)
            ?? throw new AppException(502, ErrorCode.Internal, "Docker event stream not found");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                yield break;
            }

            if (line is null)
            {
                yield break;
            }

            if (line.Length == 0 || line.Length > 1_000_000)
            {
                continue;
            }

            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(line) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (obj is not null)
            {
                yield return obj;
            }
        }
    }

    // ---- mutations (gateway only) -------------------------------------------------------------------------------------

    public async Task<bool> ExecuteAsync(ContainerCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!DockerNames.IsFullId(command.ContainerId))
        {
            throw new AppException(500, ErrorCode.Internal, "Engine mutations require a full container id");
        }

        var client = await ClientAsync(ct);
        var id = command.ContainerId;
        try
        {
            switch (command.Action)
            {
                case ContainerAction.Start:
                    return await client.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct);
                case ContainerAction.Stop:
                    return await client.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = command.TimeoutSeconds is { } t ? (uint)t : null }, ct);
                case ContainerAction.Restart:
                    await client.Containers.RestartContainerAsync(id, new ContainerRestartParameters { WaitBeforeKillSeconds = command.TimeoutSeconds is { } r ? (uint)r : null }, ct);
                    return true;
                case ContainerAction.Kill:
                    await client.Containers.KillContainerAsync(id, new ContainerKillParameters { Signal = command.Signal }, ct);
                    return true;
                case ContainerAction.Pause:
                    await client.Containers.PauseContainerAsync(id, ct);
                    return true;
                case ContainerAction.Unpause:
                    await client.Containers.UnpauseContainerAsync(id, ct);
                    return true;
                case ContainerAction.Remove:
                    // never force, never remove volumes
                    await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = false, RemoveVolumes = false }, ct);
                    return true;
                default:
                    throw new AppException(500, ErrorCode.Internal, "Unsupported container action");
            }
        }
        catch (DockerContainerNotFoundException)
        {
            throw AppException.NotFound("Container");
        }
        catch (DockerApiException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            throw AppException.Conflict(e.Message);
        }
        catch (DockerApiException e)
        {
            throw new AppException(502, ErrorCode.Internal, $"Docker error: {e.Message}");
        }
        catch (Exception e) when (e is not AppException && DockerHttp.IsTransport(e, ct))
        {
            throw DockerHttp.Unavailable(e);
        }
    }

    // ---- image / volume / network mutations (gateway only) --------------------------------------------------------------

    public async Task<string?> DistributionDigestAsync(string name, CancellationToken ct)
    {
        var path = string.Join('/', name.Split('/').Select(s => Uri.EscapeDataString(s).Replace("%3A", ":", StringComparison.Ordinal).Replace("%40", "@", StringComparison.Ordinal)));
        var node = await GetAsync($"/distribution/{path}/json", ct, TimeSpan.FromSeconds(30)) as JsonObject;
        return node?.Obj("Descriptor")?.Str("digest");
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public async Task PullImageAsync(ImagePull request, IProgress<Quaymaster.Application.Docker.PullProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = await ClientAsync(ct);
        var auth = request.Auth is null ? null : new AuthConfig { Username = request.Auth.Username, Password = request.Auth.Password, ServerAddress = request.Auth.ServerAddress };
        string? failure = null;
        var sink = new SyncProgress<JSONMessage>(m =>
        {
            if (!string.IsNullOrEmpty(m.Error?.Message))
            {
                failure ??= m.Error.Message;
            }

            progress?.Report(new Quaymaster.Application.Docker.PullProgress(m.Status, m.ID, m.Progress?.Current is { } cur && m.Progress.Total > 0 ? $"{cur}/{m.Progress.Total}" : null));
        });
        try
        {
            // fromImage=<repo>&tag=<sha256:...>: the Engine resolves the digest, so a moving tag is never pulled
            await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = request.Repository, Tag = request.Digest }, auth, sink, ct);
        }
        catch (DockerApiException e)
        {
            throw new AppException(502, ErrorCode.RegistryError, $"Docker could not pull the image: {e.Message}");
        }
        catch (Exception e) when (e is not AppException && DockerHttp.IsTransport(e, ct))
        {
            throw DockerHttp.Unavailable(e);
        }

        if (failure is not null)
        {
            throw new AppException(502, ErrorCode.RegistryError, failure);
        }
    }

    public async Task TagImageAsync(string imageId, string repo, string tag, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        try
        {
            await client.Images.TagImageAsync(imageId, new ImageTagParameters { RepositoryName = repo, Tag = tag }, ct);
        }
        catch (DockerImageNotFoundException)
        {
            throw AppException.NotFound("Image");
        }
        catch (DockerApiException e)
        {
            throw MapApi(e);
        }
        catch (Exception e) when (e is not AppException && DockerHttp.IsTransport(e, ct))
        {
            throw DockerHttp.Unavailable(e);
        }
    }

    public async Task RemoveImageAsync(string nameOrId, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        try
        {
            // never forced: Docker refuses to delete an image a container uses (409)
            await client.Images.DeleteImageAsync(nameOrId, new ImageDeleteParameters { Force = false, NoPrune = false }, ct);
        }
        catch (DockerImageNotFoundException)
        {
            throw AppException.NotFound("Image");
        }
        catch (DockerApiException e)
        {
            throw MapApi(e);
        }
        catch (Exception e) when (e is not AppException && DockerHttp.IsTransport(e, ct))
        {
            throw DockerHttp.Unavailable(e);
        }
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        try
        {
            await client.Volumes.RemoveAsync(name, force: false, ct);
        }
        catch (DockerApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw AppException.NotFound("Volume");
        }
        catch (DockerApiException e)
        {
            throw MapApi(e);
        }
        catch (Exception e) when (e is not AppException && DockerHttp.IsTransport(e, ct))
        {
            throw DockerHttp.Unavailable(e);
        }
    }

    public async Task RemoveNetworkAsync(string id, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        try
        {
            await client.Networks.DeleteNetworkAsync(id, ct);
        }
        catch (DockerApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw AppException.NotFound("Network");
        }
        catch (DockerApiException e)
        {
            throw MapApi(e);
        }
        catch (Exception e) when (e is not AppException && DockerHttp.IsTransport(e, ct))
        {
            throw DockerHttp.Unavailable(e);
        }
    }

    private static AppException MapApi(DockerApiException e) =>
        e.StatusCode == HttpStatusCode.Conflict ? AppException.Conflict(e.Message) : new AppException(502, ErrorCode.Internal, $"Docker error: {e.Message}");

    public void Dispose()
    {
        _client?.Dispose();
        _http.Dispose();
        _negotiate.Dispose();
    }
}

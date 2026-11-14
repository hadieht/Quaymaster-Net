using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quaymaster.Application.Errors;

namespace Quaymaster.Infrastructure.Docker;

/// <summary>
/// Minimal read-only HTTP client for the Engine API. Used where the Engine's own JSON must pass through unchanged
/// (inspect, list, info, df, events, logs, stats): typed client models would drop unknown fields and rename keys.
/// Accepts a unix socket path or <c>tcp://host:port</c> (a socket proxy). Only GET is ever issued here.
/// </summary>
internal sealed class DockerHttp : IDisposable
{
    private readonly HttpClient _http;

    public DockerHttp(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false };
        var tcp = TcpTarget(target);
        if (tcp is null)
        {
            var path = target.StartsWith("unix://", StringComparison.Ordinal) ? target["unix://".Length..] : target;
            handler.ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(tcp is null ? "http://docker.invalid" : $"http://{tcp}"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>API version prefix such as <c>v1.54</c>; null until negotiated (then the unversioned path is used).</summary>
    public string? ApiPrefix { get; set; }

    public static string? TcpTarget(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var m = System.Text.RegularExpressions.Regex.Match(target, @"^tcp://([^:/]+):(\d{1,5})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return m.Success ? $"{m.Groups[1].Value}:{m.Groups[2].Value}" : null;
    }

    private string Url(string path) => ApiPrefix is null ? path : $"/{ApiPrefix}{path}";

    /// <summary>GET returning parsed JSON, or null for 404. Transport failures become <c>docker_unavailable</c>.</summary>
    public async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            using var response = await _http.GetAsync(Url(path), HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, cts.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            return await JsonNode.ParseAsync(stream, cancellationToken: cts.Token);
        }
        catch (Exception e) when (IsTransport(e, ct))
        {
            throw Unavailable(e);
        }
    }

    /// <summary>GET opening a long-lived response (events, logs). The caller disposes it.</summary>
    public async Task<HttpResponseMessage?> OpenStreamAsync(string path, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(Url(path), HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return null;
            }

            try
            {
                await EnsureSuccessAsync(response, ct);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            return response;
        }
        catch (Exception e) when (IsTransport(e, ct))
        {
            throw Unavailable(e);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            message = JsonNode.Parse(body)?["message"]?.GetValue<string>() ?? body;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            message = response.ReasonPhrase ?? "Docker error";
        }

        throw (int)response.StatusCode switch
        {
            409 => AppException.Conflict(message),
            400 => AppException.BadRequest(message),
            _ => new AppException(502, ErrorCode.Internal, $"Docker error: {message}"),
        };
    }

    public static bool IsTransport(Exception e, CancellationToken caller) =>
        !caller.IsCancellationRequested
        && (e is HttpRequestException or SocketException or IOException or TaskCanceledException or TimeoutException or UnauthorizedAccessException);

    public static AppException Unavailable(Exception e)
    {
        var inner = e;
        while (inner.InnerException is not null)
        {
            inner = inner.InnerException;
        }

        var why = inner switch
        {
            SocketException s => s.SocketErrorCode.ToString(),
            UnauthorizedAccessException => "permission denied",
            TaskCanceledException or TimeoutException => "timeout",
            _ => inner.GetType().Name,
        };
        return new AppException(503, ErrorCode.DockerUnavailable, $"Docker daemon unreachable: {why}");
    }

    public void Dispose() => _http.Dispose();
}

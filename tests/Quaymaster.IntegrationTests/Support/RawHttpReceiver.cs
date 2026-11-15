using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Quaymaster.IntegrationTests.Support;

/// <summary>One request exactly as it arrived on the wire.</summary>
public sealed record CapturedRequest(string RequestLine, IReadOnlyList<KeyValuePair<string, string>> Headers, string Body, string RawHead)
{
    public string Method => RequestLine.Split(' ')[0];

    public string Path => RequestLine.Split(' ')[1];

    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>A tiny raw-socket HTTP/1.1 listener on 127.0.0.1 (local only): records every request byte-exactly, answers from a script.</summary>
public sealed class RawHttpReceiver : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<CapturedRequest> _requests = [];
    private readonly Lock _gate = new();

    public RawHttpReceiver()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Raw response for request number n (0-based); null = never answer (timeout tests).</summary>
    public Func<int, CapturedRequest, string?> Respond { get; set; } = (_, _) => Response(200);

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public static string Response(int status, string? location = null, int bodyBytes = 0)
    {
        var sb = new StringBuilder($"HTTP/1.1 {status} X\r\n");
        if (location is not null)
        {
            sb.Append("Location: ").Append(location).Append("\r\n");
        }

        sb.Append("Content-Length: ").Append(bodyBytes).Append("\r\nConnection: close\r\n\r\n").Append('x', bodyBytes);
        return sb.ToString();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // stopped
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new List<byte>();
                var chunk = new byte[4096];
                int headEnd;
                while ((headEnd = IndexOfHeadEnd(buffer)) < 0)
                {
                    var n = await stream.ReadAsync(chunk, _cts.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                }

                var head = Encoding.Latin1.GetString([.. buffer.Take(headEnd)]);
                var lines = head.Split("\r\n");
                var headers = lines.Skip(1).Select(l => l.IndexOf(':', StringComparison.Ordinal)).Zip(lines.Skip(1), (i, l) => new KeyValuePair<string, string>(l[..i], l[(i + 1)..].Trim())).ToList();
                var length = int.Parse(headers.FirstOrDefault(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                while (buffer.Count - (headEnd + 4) < length)
                {
                    var n = await stream.ReadAsync(chunk, _cts.Token);
                    if (n == 0)
                    {
                        break;
                    }

                    buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                }

                var body = Encoding.UTF8.GetString([.. buffer.Skip(headEnd + 4).Take(length)]);
                var captured = new CapturedRequest(lines[0], headers, body, head);
                int index;
                lock (_gate)
                {
                    index = _requests.Count;
                    _requests.Add(captured);
                }

                var response = Respond(index, captured);
                if (response is null)
                {
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                    return;
                }

                await stream.WriteAsync(Encoding.Latin1.GetBytes(response), _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
            {
                // client went away / test finished
            }
        }
    }

    private static int IndexOfHeadEnd(List<byte> b)
    {
        for (var i = 0; i + 3 < b.Count; i++)
        {
            if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10)
            {
                return i;
            }
        }

        return -1;
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using Quaymaster.Application.Notifications;
using Quaymaster.Domain.Notifications;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.Infrastructure.Notifications;

/// <summary>A refused or failed outbound request. The message never contains the full URL (it is a secret as a whole).</summary>
internal sealed class OutboundException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An address the policy refuses; raised from the connect callback so DNS rebinding cannot slip past the URL check.</summary>
internal sealed class BlockedAddressException(string message) : Exception(message);

/// <summary>
/// Production transport: the single outbound policy of the original (<see cref="OutboundPolicy"/>) on top of
/// <see cref="HttpClient"/>. http/https only, every resolved address checked at connect time, 5 s total deadline, 64 KiB response
/// cap (bodies are discarded), redirects only to the SAME host (max 3; 307/308 keep method + body, others become GET).
/// </summary>
internal sealed class OutboundHttpTransport : INotificationTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly int _timeoutMs;

    public OutboundHttpTransport(int timeoutMs = OutboundPolicy.DefaultTimeoutMs)
    {
        _timeoutMs = timeoutMs;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            ConnectCallback = ConnectCheckedAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _client.Dispose();

    public Task SendMailAsync(MailMessage mail, CancellationToken ct) => SmtpMailer.SendAsync(mail, ct);

    public async Task<int> SendRequestAsync(OutboundHttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeoutMs = request.TimeoutMs ?? _timeoutMs;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var maskedOrigin = Mask(request.Url);
        Uri current;
        try
        {
            current = OutboundPolicy.Validate(request.Url);
        }
        catch (OutboundPolicyException e)
        {
            throw new OutboundException(e.Message);
        }

        var origin = current.Authority.ToLowerInvariant();
        var method = request.Method.ToUpperInvariant();
        var body = request.Body;
        for (var hop = 0; ; hop++)
        {
            (int Status, string? Location) res;
            try
            {
                res = await OnceAsync(current, method, request.Headers, body, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new OutboundException($"Request to {Mask(current.ToString())} timed out after {timeoutMs} ms");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or SocketException or BlockedAddressException)
            {
                throw new OutboundException(Describe(e, Mask(current.ToString())), e);
            }

            if (res.Status is 301 or 302 or 303 or 307 or 308 && !string.IsNullOrEmpty(res.Location))
            {
                if (hop >= OutboundPolicy.MaxRedirects)
                {
                    throw new OutboundException($"Too many redirects from {maskedOrigin}");
                }

                Uri next;
                try
                {
                    next = OutboundPolicy.Validate(new Uri(current, res.Location).ToString());
                }
                catch (Exception e) when (e is OutboundPolicyException or UriFormatException)
                {
                    throw new OutboundException($"Redirect from {maskedOrigin} to an invalid target refused");
                }

                if (!string.Equals(next.Authority, origin, StringComparison.OrdinalIgnoreCase))
                {
                    throw new OutboundException($"Redirect from {maskedOrigin} to a different host refused");
                }

                if (res.Status is not (307 or 308))
                {
                    method = "GET";
                    body = null;
                }

                current = next;
                continue;
            }

            return res.Status;
        }
    }

    private async Task<(int Status, string? Location)> OnceAsync(Uri url, string method, IReadOnlyList<KeyValuePair<string, string>> headers, string? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            req.Content.Headers.Clear();
        }

        foreach (var (name, value) in headers)
        {
            if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase) || name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                req.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            else if (!req.Headers.TryAddWithoutValidation(name, value))
            {
                req.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var location = res.Headers.Location is { } l ? l.OriginalString : null;
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        var total = 0;
        try
        {
            int read;
            while (total <= OutboundPolicy.MaxResponseBytes && (read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
            }
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            // the status is what counts; a broken body after the headers is not a failure
        }

        return ((int)res.StatusCode, location);
    }

    internal static string Mask(string url) => OutboundPolicy.MaskToSchemeHost(url, Redactor.Mask);

    private static string Describe(Exception e, string masked)
    {
        for (var x = (Exception?)e; x is not null; x = x.InnerException)
        {
            if (x is BlockedAddressException b)
            {
                return $"{b.Message} ({masked})";
            }
        }

        var code = "network error";
        for (var x = (Exception?)e; x is not null; x = x.InnerException)
        {
            if (x is SocketException se)
            {
                code = se.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "ECONNREFUSED",
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "ENOTFOUND",
                    SocketError.TimedOut => "ETIMEDOUT",
                    SocketError.ConnectionReset => "ECONNRESET",
                    SocketError.NetworkUnreachable => "ENETUNREACH",
                    SocketError.HostUnreachable => "EHOSTUNREACH",
                    _ => "network error",
                };
                break;
            }

            if (x is System.Security.Authentication.AuthenticationException)
            {
                code = "TLS error";
                break;
            }
        }

        return $"Request to {masked} failed: {code}";
    }

    /// <summary>Resolves the host, refuses any disallowed address, then connects to the CHECKED address (TLS name stays the host).</summary>
    internal static async ValueTask<Stream> ConnectCheckedAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await ResolveAllowedAsync(host, ct);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    internal static async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(string host, CancellationToken ct)
    {
        var bare = host.TrimStart('[').TrimEnd(']');
        IPAddress[] list;
        if (OutboundPolicy.IsIpLiteral(bare) && IPAddress.TryParse(bare.Split('%')[0], out var literal))
        {
            list = [literal];
        }
        else
        {
            list = await Dns.GetHostAddressesAsync(host, ct);
        }

        if (list.Length == 0)
        {
            throw new BlockedAddressException("host did not resolve");
        }

        foreach (var a in list)
        {
            var why = OutboundPolicy.DisallowedAddressReason(a.ToString());
            if (why is not null)
            {
                throw new BlockedAddressException($"Destination address {a} is not allowed ({why})");
            }
        }

        return list;
    }
}

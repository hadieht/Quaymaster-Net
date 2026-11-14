using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Quaymaster.Domain.Notifications;

namespace Quaymaster.Infrastructure.Notifications;

/// <summary>
/// A small SMTP submission client (the original used nodemailer): address policy first, then connect to the CHECKED ip with the
/// host name as TLS server name, implicit TLS when <c>secure</c>, otherwise STARTTLS when the server offers it, AUTH PLAIN / LOGIN
/// when a username is configured, plain-text UTF-8 message. Timeouts: connect 5 s, each read/write 15 s.
/// </summary>
internal sealed class SmtpMailer
{
    private const int ConnectTimeoutMs = 5000;
    private const int IoTimeoutMs = 15000;

    public static async Task SendAsync(MailMessage m, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);
        var addresses = await OutboundHttpTransport.ResolveAllowedAsync(m.Host, ct);
        using var tcp = new TcpClient();
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(ConnectTimeoutMs);
            try
            {
                await tcp.ConnectAsync(addresses[0], m.Port, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("SMTP connection timed out");
            }
            catch (SocketException e)
            {
                throw new IOException($"SMTP connection failed: {e.SocketErrorCode}");
            }
        }

        tcp.ReceiveTimeout = IoTimeoutMs;
        tcp.SendTimeout = IoTimeoutMs;
        Stream stream = tcp.GetStream();
        if (m.Secure)
        {
            stream = await TlsAsync(stream, m.Host, ct);
        }

        var session = new Session(stream);
        try
        {
            await session.ExpectAsync(220, ct);
            var ehlo = await session.CommandAsync("EHLO quaymaster", 250, ct);
            if (!m.Secure && ehlo.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
            {
                await session.CommandAsync("STARTTLS", 220, ct);
                session = new Session(await TlsAsync(stream, m.Host, ct));
                ehlo = await session.CommandAsync("EHLO quaymaster", 250, ct);
            }

            if (m.Username is not null)
            {
                var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{m.Username}\0{m.Password ?? string.Empty}"));
                if (ehlo.Contains("AUTH", StringComparison.OrdinalIgnoreCase) && ehlo.Contains("LOGIN", StringComparison.OrdinalIgnoreCase) && !ehlo.Contains("PLAIN", StringComparison.OrdinalIgnoreCase))
                {
                    await session.CommandAsync("AUTH LOGIN", 334, ct);
                    await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Username)), 334, ct);
                    await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Password ?? string.Empty)), 235, ct);
                }
                else
                {
                    await session.CommandAsync($"AUTH PLAIN {plain}", 235, ct);
                }
            }

            await session.CommandAsync($"MAIL FROM:<{m.From}>", 250, ct);
            foreach (var to in m.To)
            {
                await session.CommandAsync($"RCPT TO:<{to}>", 250, ct);
            }

            await session.CommandAsync("DATA", 354, ct);
            await session.SendRawAsync(Build(m), ct);
            await session.ExpectAsync(250, ct);
            await session.CommandAsync("QUIT", 221, ct, tolerate: true);
        }
        finally
        {
            session.Dispose();
        }
    }

    private static async Task<Stream> TlsAsync(Stream inner, string host, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
        return ssl;
    }

    /// <summary>RFC 5322 message with CRLF line ends and dot-stuffing, terminated by the lone dot.</summary>
    internal static string Build(MailMessage m)
    {
        var ascii = m.Text.All(c => c < 128);
        var body = m.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var longLine = body.Split('\n').Any(l => l.Length > 900);
        var sb = new StringBuilder();
        sb.Append("From: ").Append(m.From).Append("\r\n");
        sb.Append("To: ").Append(string.Join(", ", m.To)).Append("\r\n");
        sb.Append("Subject: ").Append(EncodeHeader(m.Subject)).Append("\r\n");
        sb.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(m.Host).Append(">\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
        if (ascii && !longLine)
        {
            sb.Append("Content-Transfer-Encoding: 7bit\r\n\r\n");
            foreach (var line in body.Split('\n'))
            {
                sb.Append(line.StartsWith('.') ? "." + line : line).Append("\r\n");
            }
        }
        else
        {
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
            for (var i = 0; i < b64.Length; i += 76)
            {
                sb.Append(b64.AsSpan(i, Math.Min(76, b64.Length - i))).Append("\r\n");
            }
        }

        sb.Append(".\r\n");
        return sb.ToString();
    }

    private static string EncodeHeader(string v) =>
        v.All(c => c is >= ' ' and <= '~') ? v : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(v))}?=";

    private sealed class Session(Stream stream) : IDisposable
    {
        private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);

        public void Dispose()
        {
            _reader.Dispose();
            stream.Dispose();
        }

        public async Task SendRawAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        public async Task<string> CommandAsync(string line, int expected, CancellationToken ct, bool tolerate = false)
        {
            await SendRawAsync(line + "\r\n", ct);
            try
            {
                return await ExpectAsync(expected, ct);
            }
            catch (IOException) when (tolerate)
            {
                return string.Empty;
            }
        }

        public async Task<string> ExpectAsync(int expected, CancellationToken ct)
        {
            var all = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(IoTimeoutMs);
            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token) ?? throw new IOException("SMTP connection closed");
                all.Append(line).Append('\n');
                if (line.Length >= 4 && line[3] == '-')
                {
                    continue;
                }

                if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), CultureInfo.InvariantCulture, out var code))
                {
                    throw new IOException("SMTP protocol error");
                }

                if (code != expected)
                {
                    throw new IOException($"SMTP {code}: {(line.Length > 4 ? line[4..] : string.Empty)}");
                }

                return all.ToString();
            }
        }
    }
}

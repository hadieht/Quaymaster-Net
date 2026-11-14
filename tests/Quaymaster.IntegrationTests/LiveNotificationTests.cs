using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Notifications;
using Quaymaster.Domain.Notifications;
using Quaymaster.Infrastructure.Notifications;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

/// <summary>
/// Real HTTP / SMTP on loopback against receivers THIS test run owns (no external service is ever contacted). Expected headers
/// and bodies are transcribed from the original channels/*.ts + outbound.ts for the same fixture event.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveNotificationTransportTests
{
    // 1700000000000 ms = 2023-11-14T22:13:20.000Z
    private static readonly OutboundEvent Fixture = new("run.failed", "critical", "Update failed: shop", "Health check failed", 1_700_000_000_000, Data: new JsonObject { ["run"] = "r1", ["attempts"] = 2, ["ok"] = false });

    private static OutboundHttpRequest Render(string kind, string config)
    {
        var k = Channels.Get(kind)!;
        var parsed = k.Parse(JsonNode.Parse(config));
        parsed.Issues.Should().BeEmpty();
        return k.Render(parsed.Config!, Fixture).Http!;
    }

    /// <summary>Lower-cased header names → values, minus the ones the HTTP stack owns.</summary>
    private static Dictionary<string, string> Wire(CapturedRequest r) =>
        r.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value);

    [Fact]
    public async Task Webhook_Goes_Out_Exactly_Like_The_Original()
    {
        using var rx = new RawHttpReceiver();
        using var transport = new OutboundHttpTransport();
        var req = Render("webhook", $$$"""{"url":"{{{rx.BaseUrl}}}/hook?x=1","secret":"s3cret-key","headers":{"X-Token":"t1","Authorization":"Bearer abc"}}""");
        (await transport.SendRequestAsync(req, default)).Should().Be(200);

        var got = rx.Requests.Should().ContainSingle().Which;
        got.RequestLine.Should().Be("POST /hook?x=1 HTTP/1.1");
        got.Body.Should().Be("""{"type":"run.failed","severity":"critical","title":"Update failed: shop","message":"Health check failed","data":{"run":"r1","attempts":2,"ok":false},"time":"2023-11-14T22:13:20.000Z"}""");
        var h = Wire(got);
        h.Keys.Should().BeEquivalentTo("host", "x-token", "authorization", "content-type", "user-agent", "x-quaymaster-timestamp", "x-quaymaster-signature", "content-length");
        h["host"].Should().Be($"127.0.0.1:{rx.Port}");
        h["content-type"].Should().Be("application/json");
        h["user-agent"].Should().Be("Quaymaster");
        h["x-quaymaster-timestamp"].Should().Be("1700000000");
        h["x-token"].Should().Be("t1");
        h["authorization"].Should().Be("Bearer abc");
        h["content-length"].Should().Be(Encoding.UTF8.GetByteCount(got.Body).ToString(System.Globalization.CultureInfo.InvariantCulture));
        // independent recomputation of the signature (openssl dgst -sha256 -hmac s3cret-key gives the same value)
        h["x-quaymaster-signature"].Should().Be("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("s3cret-key"u8.ToArray(), Encoding.UTF8.GetBytes(got.Body))));
        h["x-quaymaster-signature"].Should().Be("sha256=b9325d0d26c9a5f1cf98280b9b78497ea4e66411f8732fbb67f0fab6a2f464d8");
    }

    [Fact]
    public async Task Ntfy_Goes_Out_Exactly_Like_The_Original()
    {
        using var rx = new RawHttpReceiver();
        using var transport = new OutboundHttpTransport();
        var req = Render("ntfy", $$$"""{"serverUrl":"{{{rx.BaseUrl}}}/","topic":"quay-master","token":"tk_secret"}""");
        (await transport.SendRequestAsync(req, default)).Should().Be(200);

        var got = rx.Requests.Should().ContainSingle().Which;
        got.RequestLine.Should().Be("POST /quay-master HTTP/1.1");
        got.Body.Should().Be("Health check failed");
        var h = Wire(got);
        h.Keys.Should().BeEquivalentTo("host", "title", "priority", "tags", "content-type", "authorization", "content-length");
        h["title"].Should().Be("Update failed: shop");
        h["priority"].Should().Be("5");
        h["tags"].Should().Be("rotating_light,run.failed");
        h["content-type"].Should().Be("text/plain; charset=utf-8");
        h["authorization"].Should().Be("Bearer tk_secret");
        h["content-length"].Should().Be("19");
    }

    [Fact]
    public async Task Gotify_Telegram_Discord_And_Slack_Shapes()
    {
        using var rx = new RawHttpReceiver();
        using var transport = new OutboundHttpTransport();
        await transport.SendRequestAsync(Render("gotify", $$$"""{"serverUrl":"{{{rx.BaseUrl}}}","appToken":"AppTok"}"""), default);
        await transport.SendRequestAsync(Render("telegram", $$$"""{"botToken":"123:ABC","chatId":"-100200","apiBase":"{{{rx.BaseUrl}}}"}"""), default);
        await transport.SendRequestAsync(Render("discord", $$$"""{"url":"{{{rx.BaseUrl}}}/api/webhooks/1/tok"}"""), default);
        await transport.SendRequestAsync(Render("slack", $$$"""{"url":"{{{rx.BaseUrl}}}/services/T/B/x"}"""), default);

        var r = rx.Requests;
        r.Select(x => x.RequestLine).Should().Equal("POST /message HTTP/1.1", "POST /bot123:ABC/sendMessage HTTP/1.1", "POST /api/webhooks/1/tok HTTP/1.1", "POST /services/T/B/x HTTP/1.1");
        Wire(r[0])["x-gotify-key"].Should().Be("AppTok");
        r[0].Body.Should().Be("""{"title":"Update failed: shop","message":"Health check failed","priority":8,"extras":{"client::display":{"contentType":"text/plain"}}}""");
        r[1].Body.Should().Be("""{"chat_id":"-100200","text":"[CRITICAL] Update failed: shop\nHealth check failed","disable_web_page_preview":true}""");
        r.Should().OnlyContain(x => Wire(x)["content-type"] == "application/json");
        JsonNode.Parse(r[2].Body)!["embeds"]![0]!["color"]!.GetValue<int>().Should().Be(15158332);
        JsonNode.Parse(r[3].Body)!["attachments"]![0]!["color"]!.GetValue<string>().Should().Be("#e01e5a");
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(299, true)]
    [InlineData(400, false)]
    [InlineData(500, false)]
    public async Task Status_Is_Returned_As_Is(int status, bool success)
    {
        using var rx = new RawHttpReceiver { Respond = (_, _) => RawHttpReceiver.Response(status) };
        using var transport = new OutboundHttpTransport();
        var s = await transport.SendRequestAsync(Render("webhook", $$$"""{"url":"{{{rx.BaseUrl}}}/x"}"""), default);
        s.Should().Be(status);
        (s is >= 200 and < 300).Should().Be(success);
    }

    // ---- redirects -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Redirect_307_And_308_Keep_Method_And_Body_Others_Become_Get()
    {
        foreach (var (code, keeps) in new[] { (307, true), (308, true), (301, false), (302, false), (303, false) })
        {
            using var rx = new RawHttpReceiver { Respond = (i, _) => i == 0 ? RawHttpReceiver.Response(code, "/moved") : RawHttpReceiver.Response(200) };
            using var transport = new OutboundHttpTransport();
            (await transport.SendRequestAsync(Render("webhook", $$$"""{"url":"{{{rx.BaseUrl}}}/orig","secret":"k"}"""), default)).Should().Be(200);
            var second = rx.Requests[1];
            second.Path.Should().Be("/moved");
            if (keeps)
            {
                second.Method.Should().Be("POST", $"{code}");
                second.Body.Should().Be(rx.Requests[0].Body);
                Wire(second)["x-quaymaster-signature"].Should().Be(Wire(rx.Requests[0])["x-quaymaster-signature"]);
            }
            else
            {
                second.Method.Should().Be("GET", $"{code}");
                second.Body.Should().BeEmpty();
            }
        }
    }

    [Fact]
    public async Task Redirects_To_Another_Host_Or_A_Refused_Address_Are_Not_Followed()
    {
        using var rx = new RawHttpReceiver { Respond = (_, _) => RawHttpReceiver.Response(302, $"http://localhost:{0}/x") };
        using var transport = new OutboundHttpTransport();
        rx.Respond = (_, _) => RawHttpReceiver.Response(302, $"http://localhost:{rx.Port}/other-host");
        var act = () => transport.SendRequestAsync(Render("webhook", $$$"""{"url":"{{{rx.BaseUrl}}}/orig"}"""), default);
        (await act.Should().ThrowAsync<OutboundException>()).Which.Message.Should().Be($"Redirect from http://127.0.0.1:{rx.Port}/•••••• to a different host refused");
        rx.Requests.Should().HaveCount(1);

        rx.Respond = (_, _) => RawHttpReceiver.Response(302, "http://169.254.169.254/latest/meta-data");
        (await act.Should().ThrowAsync<OutboundException>()).Which.Message.Should().Contain("to an invalid target refused");

        rx.Respond = (_, _) => RawHttpReceiver.Response(302, "ftp://127.0.0.1/x");
        (await act.Should().ThrowAsync<OutboundException>()).Which.Message.Should().Contain("to an invalid target refused");
    }

    [Fact]
    public async Task At_Most_Three_Redirects_Are_Followed()
    {
        using var rx = new RawHttpReceiver { Respond = (i, _) => i < 3 ? RawHttpReceiver.Response(307, $"/hop{i + 1}") : RawHttpReceiver.Response(200) };
        using var transport = new OutboundHttpTransport();
        (await transport.SendRequestAsync(Render("webhook", $$$"""{"url":"{{{rx.BaseUrl}}}/hop0"}"""), default)).Should().Be(200);
        rx.Requests.Select(r => r.Path).Should().Equal("/hop0", "/hop1", "/hop2", "/hop3");

        using var loop = new RawHttpReceiver { Respond = (i, _) => RawHttpReceiver.Response(307, $"/hop{i + 1}") };
        var act = () => transport.SendRequestAsync(Render("webhook", $$$"""{"url":"{{{loop.BaseUrl}}}/hop0"}"""), default);
        (await act.Should().ThrowAsync<OutboundException>()).Which.Message.Should().Be($"Too many redirects from http://127.0.0.1:{loop.Port}/••••••");
        loop.Requests.Should().HaveCount(4);
    }

    // ---- policy at the socket --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Link_Local_And_Unspecified_Destinations_Are_Refused_Before_Any_Connection()
    {
        using var transport = new OutboundHttpTransport();
        foreach (var url in new[] { "http://169.254.169.254/latest/meta-data/", "http://0.0.0.0:80/", "http://[fe80::1]:80/", "http://224.0.0.1/" })
        {
            var act = () => transport.SendRequestAsync(new OutboundHttpRequest(url, "POST", [], "x"), default);
            var e = (await act.Should().ThrowAsync<OutboundException>()).Which;
            e.Message.Should().StartWith("Destination address ").And.Contain("is not allowed");
        }

        var bad = () => transport.SendRequestAsync(new OutboundHttpRequest("ftp://127.0.0.1/", "POST", [], "x"), default);
        (await bad.Should().ThrowAsync<OutboundException>()).Which.Message.Should().Be("Only http and https URLs are allowed");
        var cred = () => transport.SendRequestAsync(new OutboundHttpRequest("http://u:p@127.0.0.1/", "POST", [], "x"), default);
        (await cred.Should().ThrowAsync<OutboundException>()).Which.Message.Should().NotContain("u:p");
    }

    [Fact]
    public async Task Loopback_Private_And_Named_Hosts_Are_Allowed_For_Self_Hosted_Servers()
    {
        using var rx = new RawHttpReceiver();
        using var transport = new OutboundHttpTransport();
        // a hostname that resolves to loopback: the connect-time check resolves it, sees 127.0.0.1 (allowed) and connects
        (await transport.SendRequestAsync(new OutboundHttpRequest($"http://localhost:{rx.Port}/n", "POST", [], "x"), default)).Should().Be(200);
        rx.Requests.Should().ContainSingle().Which.Path.Should().Be("/n");
    }

    [Fact]
    public async Task Connection_Failures_And_Timeouts_Never_Leak_The_Url()
    {
        using var transport = new OutboundHttpTransport(timeoutMs: 400);
        var refused = () => transport.SendRequestAsync(new OutboundHttpRequest("http://127.0.0.1:1/very/secret/path?token=abc", "POST", [], "x"), default);
        var e = (await refused.Should().ThrowAsync<OutboundException>()).Which;
        e.Message.Should().Be("Request to http://127.0.0.1:1/•••••• failed: ECONNREFUSED");

        using var silent = new RawHttpReceiver { Respond = (_, _) => null };
        var slow = () => transport.SendRequestAsync(new OutboundHttpRequest($"{silent.BaseUrl}/secret/path", "POST", [], "x"), default);
        var t = (await slow.Should().ThrowAsync<OutboundException>()).Which;
        t.Message.Should().Be($"Request to http://127.0.0.1:{silent.Port}/•••••• timed out after 400 ms");
    }

    [Fact]
    public async Task Response_Bodies_Are_Capped_And_Discarded()
    {
        using var rx = new RawHttpReceiver { Respond = (_, _) => RawHttpReceiver.Response(200, null, 300_000) };
        using var transport = new OutboundHttpTransport();
        (await transport.SendRequestAsync(Render("webhook", $$$"""{"url":"{{{rx.BaseUrl}}}/big"}"""), default)).Should().Be(200);
    }

    // ---- SMTP ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Smtp_Delivers_A_Plain_Text_Message_With_Auth_Over_Loopback()
    {
        using var smtp = new FakeSmtpServer();
        var k = Channels.Get("smtp")!;
        var cfg = k.Parse(JsonNode.Parse($$"""{"host":"127.0.0.1","port":{{smtp.Port}},"username":"mailer","password":"pw-1","from":"qm@example.com","to":["a@example.com","b@example.com"]}""")).Config!;
        using var transport = new OutboundHttpTransport();
        await transport.SendMailAsync(k.Render(cfg, Fixture).Mail!, default);

        smtp.Commands.Should().Equal(
            "EHLO quaymaster",
            "AUTH PLAIN " + Convert.ToBase64String("\0mailer\0pw-1"u8.ToArray()),
            "MAIL FROM:<qm@example.com>",
            "RCPT TO:<a@example.com>",
            "RCPT TO:<b@example.com>",
            "DATA",
            "QUIT");
        smtp.Message.Should().Contain("From: qm@example.com\r\n").And.Contain("To: a@example.com, b@example.com\r\n").And.Contain("Subject: [CRITICAL] Update failed: shop\r\n")
            .And.Contain("Content-Type: text/plain; charset=utf-8\r\n").And.EndWith("\r\n\r\nHealth check failed\r\nrun: r1\r\nattempts: 2\r\nok: false\r\n.\r\n");
    }

    [Fact]
    public async Task Smtp_Refuses_Link_Local_Hosts_And_Reports_Server_Rejections()
    {
        using var transport = new OutboundHttpTransport();
        var k = Channels.Get("smtp")!;
        var cfg = k.Parse(JsonNode.Parse("""{"host":"169.254.169.254","port":25,"from":"a@b.co","to":["c@d.co"]}""")).Config!;
        var blocked = () => transport.SendMailAsync(k.Render(cfg, Fixture).Mail!, default);
        (await blocked.Should().ThrowAsync<BlockedAddressException>()).Which.Message.Should().Contain("is not allowed");

        using var smtp = new FakeSmtpServer { RejectRcpt = true };
        var cfg2 = k.Parse(JsonNode.Parse($$"""{"host":"127.0.0.1","port":{{smtp.Port}},"from":"a@b.co","to":["c@d.co"]}""")).Config!;
        var rejected = () => transport.SendMailAsync(k.Render(cfg2, Fixture).Mail!, default);
        (await rejected.Should().ThrowAsync<IOException>()).Which.Message.Should().Be("SMTP 550: no such user");
    }

    // ---- the whole product, real transport -------------------------------------------------------------------------------------

    [Fact]
    public async Task Api_To_Wire_Channels_Test_Send_And_Retry_Against_A_Local_Receiver()
    {
        using var rx = new RawHttpReceiver();
        using var h = await DockerHarness.CreateAsync();
        var hook = await h.Admin.PostAsync("/notifications/channels", new { type = "webhook", name = "hook", config = new { url = rx.BaseUrl + "/hook", secret = "live-secret-1" } });
        hook.Status.Should().Be(201, hook.Body);
        var ntfy = await h.Admin.PostAsync("/notifications/channels", new { type = "ntfy", name = "ntfy", minSeverity = "warning", config = new { serverUrl = rx.BaseUrl, topic = "qm" } });
        ntfy.Status.Should().Be(201, ntfy.Body);
        hook.Body.Should().NotContain("live-secret-1").And.NotContain(rx.BaseUrl + "/hook");

        var test = await h.Admin.PostAsync($"/notifications/channels/{hook.Json.GetProperty("id").GetString()}/test");
        test.Body.Should().Be("""{"ok":true,"httpStatus":200,"error":null}""");
        var first = rx.Requests.Should().ContainSingle().Which;
        first.RequestLine.Should().Be("POST /hook HTTP/1.1");
        JsonNode.Parse(first.Body)!["title"]!.GetValue<string>().Should().Be("Quaymaster test notification");
        Wire(first)["x-quaymaster-signature"].Should().Be("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("live-secret-1"u8.ToArray(), Encoding.UTF8.GetBytes(first.Body))));

        // a real event: first attempt answers 500, the retry (after the back-off) answers 200
        rx.Respond = (i, r) => r.Path == "/qm" && i < 3 ? RawHttpReceiver.Response(500) : RawHttpReceiver.Response(200);
        var sink = h.Factory.Services.GetRequiredService<Quaymaster.Application.Monitoring.INotificationSink>();
        var svc = h.Factory.Services.GetRequiredService<INotificationService>();
        sink.Notify(new Quaymaster.Application.Monitoring.NotificationEvent("host.disk_low", "critical", "Disk low", "3% free on /", "disk-low", null, new Dictionary<string, object?> { ["freePercent"] = 3 }));
        await svc.FlushAsync();
        var rows = await svc.ListDeliveriesAsync(10, null, null, default);
        rows.Where(r => r.EventType == "host.disk_low").Select(r => (r.ChannelName, r.Status)).Should().BeEquivalentTo([("hook", "sent"), ("ntfy", "failed")]);
        h.Factory.Time.Advance(TimeSpan.FromSeconds(40));
        (await svc.ProcessRetriesAsync(default)).Should().Be(1);
        (await svc.ListDeliveriesAsync(10, null, null, default)).Single(r => r.ChannelName == "ntfy").Status.Should().Be("sent");
        var ntfyReqs = rx.Requests.Where(r => r.Path == "/qm").ToList();
        ntfyReqs.Should().HaveCount(2);
        ntfyReqs[0].Body.Should().Be(ntfyReqs[1].Body);
        Wire(ntfyReqs[0])["priority"].Should().Be("5");
    }
}

/// <summary>Minimal SMTP server on loopback that records the dialogue.</summary>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _commands = [];
    private readonly Lock _gate = new();
    private string _message = string.Empty;

    public FakeSmtpServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public bool RejectRcpt { get; set; }

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands];
            }
        }
    }

    public string Message
    {
        get
        {
            lock (_gate)
            {
                return _message;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => SessionAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // stopped
        }
    }

    private async Task SessionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                async Task Send(string s) => await stream.WriteAsync(Encoding.UTF8.GetBytes(s + "\r\n"), _cts.Token);
                await Send("220 fake ESMTP");
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    lock (_gate)
                    {
                        _commands.Add(line);
                    }

                    if (line.StartsWith("EHLO", StringComparison.Ordinal))
                    {
                        await Send("250-fake\r\n250-AUTH PLAIN LOGIN\r\n250 8BITMIME");
                    }
                    else if (line.StartsWith("AUTH", StringComparison.Ordinal))
                    {
                        await Send("235 ok");
                    }
                    else if (line.StartsWith("MAIL", StringComparison.Ordinal))
                    {
                        await Send("250 ok");
                    }
                    else if (line.StartsWith("RCPT", StringComparison.Ordinal))
                    {
                        await Send(RejectRcpt ? "550 no such user" : "250 ok");
                    }
                    else if (line == "DATA")
                    {
                        await Send("354 go");
                        var sb = new StringBuilder();
                        while (await reader.ReadLineAsync(_cts.Token) is { } m)
                        {
                            sb.Append(m).Append("\r\n");
                            if (m == ".")
                            {
                                break;
                            }
                        }

                        lock (_gate)
                        {
                            _message = sb.ToString();
                        }

                        await Send("250 queued");
                    }
                    else if (line == "QUIT")
                    {
                        await Send("221 bye");
                        return;
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
            {
                // finished
            }
        }
    }
}

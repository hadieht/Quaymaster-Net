using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Notifications;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

/// <summary>T5 over HTTP with a faked transport: contract, secrecy, pipeline, retries, signing, test endpoint, audit.</summary>
public sealed class NotificationsApiTests : IDisposable
{
    private const string HookSecret = "very-secret-hmac-key-12345";
    private const string HookPath = "/services/T0000/B0000/XXXXSecretPathTokenXXXX";

    private readonly FakeNotificationTransport _transport = new();
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync()
    {
        var docker = new FakeDocker();
        T3Support.RoomyDisk(docker);
        var registry = new FakeRegistry();
        registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;
        var transport = _transport;
        _h = await DockerHarness.CreateAsync(null, docker, registry, services => services.AddSingleton<INotificationTransport>(transport));
        return _h;
    }

    private static object Webhook(string name = "hook", object? extra = null) =>
        new { type = "webhook", name, config = new { url = "https://hooks.example.com" + HookPath, secret = HookSecret, headers = new Dictionary<string, string> { ["X-Token"] = "tok-123456789" } }, events = extra };

    private static async Task<string> CreateAsync(DockerHarness h, object body)
    {
        var r = await h.Admin.PostAsync("/notifications/channels", body);
        r.Status.Should().Be(201, r.Body);
        return r.Json.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> DeliveriesAsync(DockerHarness h, string query = "")
    {
        var r = await h.Admin.GetAsync("/notifications/deliveries" + query);
        r.Status.Should().Be(200, r.Body);
        return r.Json.Clone();
    }

    private static async Task EmitAsync(DockerHarness h, NotificationEvent e)
    {
        h.Factory.Services.GetRequiredService<INotificationSink>().Notify(e);
        await h.Factory.Services.GetRequiredService<INotificationService>().FlushAsync();
    }

    // ---- catalogue -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Types_Lists_The_Seven_Kinds_With_Field_Specs_For_Any_Signed_In_User()
    {
        var h = await HarnessAsync();
        (await h.Factory.NewClient().GetAsync("/notifications/types", auth: false)).Status.Should().Be(401);
        var r = await h.Viewer.GetAsync("/notifications/types");
        r.Status.Should().Be(200);
        var kinds = r.Json.EnumerateArray().ToList();
        kinds.Select(k => k.GetProperty("type").GetString()).Should().Equal("webhook", "ntfy", "gotify", "discord", "slack", "telegram", "smtp");
        kinds[0].GetProperty("label").GetString().Should().Be("Webhook (JSON)");
        var secret = kinds[0].GetProperty("fields").EnumerateArray().First(f => f.GetProperty("name").GetString() == "secret");
        secret.GetProperty("kind").GetString().Should().Be("secret");
        secret.GetProperty("required").GetBoolean().Should().BeFalse();
        secret.GetProperty("secret").GetBoolean().Should().BeTrue();
        secret.GetProperty("help").GetString().Should().Be("Adds X-Quaymaster-Signature: sha256=<HMAC-SHA256 of the body>");
        kinds[1].GetProperty("fields").EnumerateArray().First().TryGetProperty("help", out _).Should().BeFalse("absent help is omitted, not null");
        kinds[6].GetProperty("fields").GetArrayLength().Should().Be(7);
    }

    // ---- channel CRUD ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Crud_Lifecycle_Wire_Shape_And_Masking()
    {
        var h = await HarnessAsync();
        (await h.Admin.GetAsync("/notifications/channels")).Json.GetArrayLength().Should().Be(0);

        var created = await h.Admin.PostAsync("/notifications/channels", Webhook());
        created.Status.Should().Be(201);
        var c = created.Json;
        c.EnumerateObject().Select(p => p.Name).Should().Equal("id", "type", "name", "enabled", "minSeverity", "events", "config", "createdAt", "updatedAt");
        c.GetProperty("id").GetString().Should().MatchRegex("^ch_[0-9a-f]{12}$");
        (c.GetProperty("type").GetString(), c.GetProperty("name").GetString(), c.GetProperty("enabled").GetBoolean(), c.GetProperty("minSeverity").GetString()).Should().Be(("webhook", "hook", true, "info"));
        c.GetProperty("events").EnumerateArray().Select(e => e.GetString()).Should().Equal("*");
        c.GetProperty("config").GetProperty("url").GetString().Should().Be("https://hooks.example.com/••••••");
        c.GetProperty("config").GetProperty("secret").GetString().Should().Be("••••••");
        c.GetProperty("config").GetProperty("headers").GetString().Should().Be("••••••");
        c.GetProperty("createdAt").GetString().Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$");
        created.Body.Should().NotContain(HookSecret).And.NotContain("XXXXSecretPathTokenXXXX").And.NotContain("tok-123456789");

        var id = c.GetProperty("id").GetString()!;
        foreach (var body in new[] { (await h.Admin.GetAsync($"/notifications/channels/{id}")).Body, (await h.Viewer.GetAsync("/notifications/channels")).Body })
        {
            body.Should().NotContain(HookSecret).And.NotContain("XXXXSecretPathTokenXXXX").And.NotContain("tok-123456789");
        }

        var patched = await h.Admin.SendAsync(HttpMethod.Patch, $"/notifications/channels/{id}", new { name = "renamed", enabled = false, minSeverity = "warning", events = new[] { "run.failed", "host.disk_low" } });
        patched.Status.Should().Be(200, patched.Body);
        patched.Json.GetProperty("name").GetString().Should().Be("renamed");
        patched.Json.GetProperty("enabled").GetBoolean().Should().BeFalse();
        patched.Json.GetProperty("events").EnumerateArray().Select(e => e.GetString()).Should().Equal("run.failed", "host.disk_low");

        var del = await h.Admin.SendAsync(HttpMethod.Delete, $"/notifications/channels/{id}");
        del.Status.Should().Be(204);
        del.Body.Should().BeEmpty();
        (await h.Admin.GetAsync($"/notifications/channels/{id}")).Status.Should().Be(404);
        (await h.Admin.GetAsync($"/notifications/channels/{id}")).ErrorCode.Should().Be("not_found");
    }

    [Fact]
    public async Task All_Seven_Kinds_Can_Be_Created_And_Never_Return_Their_Secrets()
    {
        var h = await HarnessAsync();
        var bodies = new object[]
        {
            new { type = "webhook", name = "w", config = new { url = "http://10.0.0.5:9000/in", secret = "wh-secret-1" } },
            new { type = "ntfy", name = "n", config = new { serverUrl = "http://192.168.1.2:8080", topic = "qm", token = "ntfy-token-xyz" } },
            new { type = "gotify", name = "g", config = new { serverUrl = "https://gotify.example.com", appToken = "gotify-app-token" } },
            new { type = "discord", name = "d", config = new { url = "https://discord.com/api/webhooks/1/discord-secret-token" } },
            new { type = "slack", name = "s", config = new { url = "https://hooks.slack.com/services/T/B/slack-secret-token" } },
            new { type = "telegram", name = "t", config = new { botToken = "123456:telegram-bot-token", chatId = "-100123" } },
            new { type = "smtp", name = "m", config = new { host = "mail.example.com", port = 587, username = "mailer", password = "smtp-password-1", from = "qm@example.com", to = new[] { "ops@example.com" } } },
        };
        foreach (var b in bodies)
        {
            await CreateAsync(h, b);
        }

        var list = await h.Admin.GetAsync("/notifications/channels");
        list.Json.GetArrayLength().Should().Be(7);
        foreach (var secret in new[] { "wh-secret-1", "ntfy-token-xyz", "gotify-app-token", "discord-secret-token", "slack-secret-token", "telegram-bot-token", "smtp-password-1", "10.0.0.5:9000/in" })
        {
            list.Body.Should().NotContain(secret);
        }

        var byName = list.Json.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);
        byName["t"].GetProperty("config").GetProperty("chatId").GetString().Should().Be("-100123");
        byName["t"].GetProperty("config").GetProperty("botToken").GetString().Should().Be("••••••");
        byName["m"].GetProperty("config").GetProperty("username").GetString().Should().Be("mailer");
        byName["m"].GetProperty("config").GetProperty("password").GetString().Should().Be("••••••");
        byName["m"].GetProperty("config").GetProperty("secure").GetBoolean().Should().BeFalse();
        byName["n"].GetProperty("config").GetProperty("topic").GetString().Should().Be("qm");
        byName["n"].GetProperty("config").GetProperty("serverUrl").GetString().Should().Be("http://192.168.1.2:8080/••••••");
    }

    [Fact]
    public async Task Secrets_Are_Encrypted_At_Rest_And_Not_In_The_Database_File()
    {
        var h = await HarnessAsync();
        await CreateAsync(h, Webhook());
        await CreateAsync(h, new { type = "telegram", name = "tg", config = new { botToken = "987654:AtRestTelegramToken", chatId = "1" } });
        await h.Factory.Services.GetRequiredService<INotificationService>().FlushAsync();
        var files = Directory.GetFiles(h.Factory.DataDir, "quaymaster.db*");
        files.Should().NotBeEmpty();
        var bytes = files.Select(f => { using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var ms = new MemoryStream(); fs.CopyTo(ms); return Encoding.Latin1.GetString(ms.ToArray()); }).Aggregate((a, b) => a + b);
        foreach (var secret in new[] { HookSecret, "XXXXSecretPathTokenXXXX", "tok-123456789", "AtRestTelegramToken" })
        {
            bytes.Should().NotContain(secret);
        }

        bytes.Should().Contain("notify:ch_"); // the secret_store row exists (ciphertext v1.<key>.…)
    }

    [Fact]
    public async Task Secret_Patch_Semantics_Masked_Echo_Keeps_Value_And_New_Value_Replaces_It()
    {
        var h = await HarnessAsync();
        var id = await CreateAsync(h, Webhook());
        // the UI echoes the mask back: stored secret must survive
        (await h.Admin.SendAsync(HttpMethod.Patch, $"/notifications/channels/{id}", new { config = new { secret = "••••••", url = "https://hooks.example.com/new" } })).Status.Should().Be(200);
        await EmitAsync(h, new NotificationEvent("run.failed", "warning", "T", "M", "k1"));
        var sig = _transport.Requests.Single().Headers.Single(x => x.Key == "X-Quaymaster-Signature").Value;
        sig.Should().Be("sha256=" + Hmac(HookSecret, _transport.Requests.Single().Body!));
        _transport.Requests.Single().Url.Should().Be("https://hooks.example.com/new");
        _transport.Requests.Single().Headers.Should().Contain(x => x.Key == "X-Token" && x.Value == "tok-123456789");

        (await h.Admin.SendAsync(HttpMethod.Patch, $"/notifications/channels/{id}", new { config = new { secret = "rotated-secret-99" } })).Status.Should().Be(200);
        await EmitAsync(h, new NotificationEvent("run.failed", "warning", "T2", "M", "k2"));
        var second = _transport.Requests[1];
        second.Headers.Single(x => x.Key == "X-Quaymaster-Signature").Value.Should().Be("sha256=" + Hmac("rotated-secret-99", second.Body!));
    }

    private static string Hmac(string secret, string body) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    // ---- validation + auth -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Validation_Errors_Use_The_Envelope_With_Path_Message_Details()
    {
        var h = await HarnessAsync();
        var bad = await h.Admin.PostAsync("/notifications/channels", new { type = "smtp", name = "m", config = new { host = "bad host", port = 70000, from = "nope", to = Array.Empty<string>(), extra = 1 } });
        bad.Status.Should().Be(400);
        bad.ErrorCode.Should().Be("validation");
        bad.ErrorMessage.Should().Be("Invalid smtp configuration");
        bad.Details.Should().Contain([("host", "invalid host"), ("port", "Number must be less than or equal to 65535"), ("from", "Invalid email"), ("to", "Array must contain at least 1 element(s)"), ("", "Unrecognized key(s) in object: 'extra'")]);
        bad.Body.Should().NotContain("bad host").And.NotContain("nope"); // input values are never echoed

        var ssrf = await h.Admin.PostAsync("/notifications/channels", new { type = "webhook", name = "x", config = new { url = "http://169.254.169.254/latest/meta-data" } });
        ssrf.Status.Should().Be(400);
        ssrf.Details.Should().Contain(("url", "Destination address 169.254.169.254 is not allowed (link-local (cloud metadata range))"));

        var envelope = await h.Admin.PostAsync("/notifications/channels", new { type = "nope", name = "", minSeverity = "loud", events = new[] { "run.failed", "bogus" }, config = new { }, extra = true });
        envelope.Status.Should().Be(400);
        envelope.ErrorMessage.Should().Be("Invalid request");
        (await h.Admin.PostAsync("/notifications/channels", new { name = "x", config = new { } })).Details.Should().Contain(("type", "Required"));
        var multi = await h.Admin.PostAsync("/notifications/channels", new { type = "nope", name = "", minSeverity = "loud", events = new[] { "run.failed", "bogus" }, config = 5 });
        multi.Details.Select(d => d.Path).Should().Contain(["type", "name", "minSeverity", "events.1", "config"]);
        multi.Details.Should().Contain(("name", "String must contain at least 1 character(s)"));
        multi.Details.Should().Contain(("config", "Expected object, received number"));
        multi.Details.Single(d => d.Path == "type").Message.Should().StartWith("Invalid enum value. Expected 'webhook' | 'ntfy' | 'gotify' | 'discord' | 'slack' | 'telegram' | 'smtp', received 'nope'");
        (await h.Admin.PostAsync("/notifications/channels", new { type = "webhook", name = new string('n', 81), config = new { url = "https://h/x" } })).Details.Should().Contain(("name", "String must contain at most 80 character(s)"));
        (await h.Admin.PostAsync("/notifications/channels", new { type = "webhook", name = "n", events = Array.Empty<string>(), config = new { url = "https://h/x" } })).Details.Should().Contain(("events", "Array must contain at least 1 element(s)"));
    }

    [Fact]
    public async Task Patch_Validation_Conflicts_Ids_And_Unknown_Channels()
    {
        var h = await HarnessAsync();
        var a = await CreateAsync(h, Webhook("a"));
        await CreateAsync(h, Webhook("b"));
        var patch = (string id, object body) => h.Admin.SendAsync(HttpMethod.Patch, $"/notifications/channels/{id}", body);
        (await patch(a, new { name = "b" })).Status.Should().Be(409);
        (await patch(a, new { name = "b" })).ErrorCode.Should().Be("conflict");
        (await h.Admin.PostAsync("/notifications/channels", Webhook("a"))).Status.Should().Be(409);
        (await patch(a, new { unknown = 1 })).Status.Should().Be(400);
        (await patch(a, new { minSeverity = "nope" })).Status.Should().Be(400);
        var badConfig = await patch(a, new { config = new { url = "ftp://x" } });
        badConfig.Status.Should().Be(400);
        badConfig.ErrorMessage.Should().Be("Invalid webhook configuration");
        badConfig.Details.Should().Contain(("url", "Only http and https URLs are allowed"));
        (await patch("ch_doesnotexist", new { enabled = false })).Status.Should().Be(404);
        (await patch("bad id!", new { enabled = false })).Status.Should().Be(400);
        (await h.Admin.GetAsync("/notifications/channels/bad%20id")).ErrorMessage.Should().Be("Invalid channel id");
        (await h.Admin.SendAsync(HttpMethod.Delete, "/notifications/channels/ch_doesnotexist")).Status.Should().Be(404);
        (await h.Admin.PostAsync("/notifications/channels/ch_doesnotexist/test")).Status.Should().Be(404);
    }

    [Fact]
    public async Task Viewers_Can_Read_But_Not_Change_Or_Test()
    {
        var h = await HarnessAsync();
        var id = await CreateAsync(h, Webhook());
        (await h.Viewer.GetAsync("/notifications/channels")).Status.Should().Be(200);
        (await h.Viewer.GetAsync($"/notifications/channels/{id}")).Status.Should().Be(200);
        (await h.Viewer.GetAsync("/notifications/deliveries")).Status.Should().Be(200);
        (await h.Viewer.PostAsync("/notifications/channels", Webhook("v"))).Status.Should().Be(403);
        (await h.Viewer.SendAsync(HttpMethod.Patch, $"/notifications/channels/{id}", new { enabled = false })).Status.Should().Be(403);
        (await h.Viewer.SendAsync(HttpMethod.Delete, $"/notifications/channels/{id}")).Status.Should().Be(403);
        (await h.Viewer.PostAsync($"/notifications/channels/{id}/test")).Status.Should().Be(403);
        var anon = h.Factory.NewClient();
        (await anon.GetAsync("/notifications/channels", auth: false)).Status.Should().Be(401);
        (await anon.GetAsync("/notifications/deliveries", auth: false)).Status.Should().Be(401);
        _transport.Requests.Should().BeEmpty();
    }

    // ---- test endpoint ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_Endpoint_Sends_A_Test_Event_And_Reports_The_Result()
    {
        var h = await HarnessAsync();
        var id = await CreateAsync(h, Webhook("hook"));
        var ok = await h.Admin.PostAsync($"/notifications/channels/{id}/test");
        ok.Status.Should().Be(200, ok.Body);
        ok.Body.Should().Be("""{"ok":true,"httpStatus":200,"error":null}""");
        var req = _transport.Requests.Single();
        req.Url.Should().Be("https://hooks.example.com" + HookPath);
        var payload = JsonElement.Parse(req.Body!);
        payload.GetProperty("type").GetString().Should().Be("test");
        payload.GetProperty("severity").GetString().Should().Be("info");
        payload.GetProperty("title").GetString().Should().Be("Quaymaster test notification");
        payload.GetProperty("message").GetString().Should().Be("This is a test notification for channel \"hook\".");

        _transport.Respond = _ => 500;
        var failed = await h.Admin.PostAsync($"/notifications/channels/{id}/test");
        failed.Status.Should().Be(200);
        failed.Body.Should().Be("""{"ok":false,"httpStatus":500,"error":"HTTP 500"}""");

        _transport.Respond = _ => throw new HttpRequestException("Request to https://hooks.example.com/•••••• failed: ECONNREFUSED");
        var down = await h.Admin.PostAsync($"/notifications/channels/{id}/test");
        down.Body.Should().Be("""{"ok":false,"httpStatus":null,"error":"Request to https://hooks.example.com/•••••• failed: ECONNREFUSED"}""");

        var log = (await DeliveriesAsync(h)).EnumerateArray().ToList();
        log.Should().HaveCount(3);
        log.Should().OnlyContain(d => d.GetProperty("eventType").GetString() == "test" && d.GetProperty("attempts").GetInt32() == 1 && d.GetProperty("nextAttemptAt").ValueKind == JsonValueKind.Null);
        log.Select(d => d.GetProperty("status").GetString()).Should().Equal("failed", "failed", "sent");
        await h.Factory.Services.GetRequiredService<INotificationService>().ProcessRetriesAsync(default);
        _transport.Requests.Should().HaveCount(3); // tests are never retried
    }

    // ---- pipeline --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_Picks_Channels_Renders_Per_Kind_And_Records_Deliveries()
    {
        var h = await HarnessAsync();
        var hook = await CreateAsync(h, new { type = "webhook", name = "w", config = new { url = "https://hooks.example.com/in", secret = HookSecret } });
        var ntfy = await CreateAsync(h, new { type = "ntfy", name = "n", minSeverity = "warning", config = new { serverUrl = "http://192.168.1.2:8080", topic = "qm", token = "tk" } });
        var gotify = await CreateAsync(h, new { type = "gotify", name = "g", events = new[] { "host.disk_low" }, config = new { serverUrl = "https://gotify.example.com", appToken = "app" } });
        var discord = await CreateAsync(h, new { type = "discord", name = "d", config = new { url = "https://discord.example/api/webhooks/1/x" } });
        var slack = await CreateAsync(h, new { type = "slack", name = "s", enabled = false, config = new { url = "https://hooks.slack.example/services/x" } });
        var telegram = await CreateAsync(h, new { type = "telegram", name = "t", minSeverity = "critical", config = new { botToken = "1:abc", chatId = "5" } });
        var smtp = await CreateAsync(h, new { type = "smtp", name = "m", config = new { host = "mail.example.com", port = 25, from = "qm@example.com", to = new[] { "ops@example.com" } } });

        await EmitAsync(h, new NotificationEvent("run.failed", "warning", "Update failed: shop", "Health check failed", "run:r1", null, new Dictionary<string, object?> { ["run"] = "r1", ["status"] = "failed" }));

        var urls = _transport.Requests.Select(r => r.Url).ToList();
        urls.Should().BeEquivalentTo("https://hooks.example.com/in", "http://192.168.1.2:8080/qm", "https://discord.example/api/webhooks/1/x");
        _transport.Mails.Should().ContainSingle().Which.Subject.Should().Be("[WARNING] Update failed: shop");
        var hookReq = _transport.Requests.Single(r => r.Url.EndsWith("/in", StringComparison.Ordinal));
        hookReq.Method.Should().Be("POST");
        var body = JsonElement.Parse(hookReq.Body!);
        body.GetProperty("type").GetString().Should().Be("run.failed");
        body.GetProperty("data").GetProperty("run").GetString().Should().Be("r1");
        hookReq.Headers.Single(x => x.Key == "X-Quaymaster-Signature").Value.Should().Be("sha256=" + Hmac(HookSecret, hookReq.Body!));
        var ntfyReq = _transport.Requests.Single(r => r.Url.Contains("192.168", StringComparison.Ordinal));
        (ntfyReq.Body, ntfyReq.Headers.Single(x => x.Key == "Priority").Value, ntfyReq.Headers.Single(x => x.Key == "Authorization").Value).Should().Be(("Health check failed", "4", "Bearer tk"));

        var rows = (await DeliveriesAsync(h)).EnumerateArray().ToList();
        rows.Should().HaveCount(4);
        rows.Should().OnlyContain(d => d.GetProperty("status").GetString() == "sent" && d.GetProperty("eventType").GetString() == "run.failed" && d.GetProperty("severity").GetString() == "warning" && d.GetProperty("title").GetString() == "Update failed: shop");
        rows.Select(d => d.GetProperty("channelId").GetString()).Should().BeEquivalentTo(hook, ntfy, discord, smtp);
        rows[0].EnumerateObject().Select(p => p.Name).Should().Equal("id", "time", "channelId", "channelName", "eventType", "severity", "title", "status", "attempts", "nextAttemptAt", "lastError", "httpStatus");
        (await DeliveriesAsync(h, $"?channelId={smtp}")).EnumerateArray().Single().GetProperty("httpStatus").ValueKind.Should().Be(JsonValueKind.Null);
        gotify.Should().NotBeNull();
        slack.Should().NotBeNull();
        telegram.Should().NotBeNull();
    }

    [Fact]
    public async Task Cooldown_Suppresses_Repeats_And_The_Log_Shows_It()
    {
        var h = await HarnessAsync();
        var id = await CreateAsync(h, Webhook());
        var e = new NotificationEvent("host.disk_low", "warning", "Disk low", "5% free", "disk-low", 60_000);
        await EmitAsync(h, e);
        await EmitAsync(h, e);
        await EmitAsync(h, e with { DedupeKey = "other" });
        _transport.Requests.Should().HaveCount(2);
        (await DeliveriesAsync(h)).EnumerateArray().Select(d => d.GetProperty("status").GetString()).Should().Equal("sent", "suppressed", "sent");
        h.Factory.Time.Advance(TimeSpan.FromSeconds(61));
        await EmitAsync(h, e);
        _transport.Requests.Should().HaveCount(3);
        (await h.Factory.Services.GetRequiredService<NotificationService>().SuppressedCountAsync(id, "disk-low")).Should().Be(1);
    }

    [Fact]
    public async Task Failed_Delivery_Is_Retried_With_Backoff_And_Becomes_Sent()
    {
        var h = await HarnessAsync();
        await CreateAsync(h, Webhook());
        var attempts = 0;
        _transport.Respond = _ => ++attempts < 3 ? 500 : 200;
        var svc = h.Factory.Services.GetRequiredService<INotificationService>();
        await EmitAsync(h, new NotificationEvent("run.failed", "critical", "Boom", "m", "run:1"));

        var first = (await DeliveriesAsync(h)).EnumerateArray().Single();
        (first.GetProperty("status").GetString(), first.GetProperty("attempts").GetInt32(), first.GetProperty("httpStatus").GetInt32(), first.GetProperty("lastError").GetString()).Should().Be(("failed", 1, 500, "HTTP 500"));
        var next = DateTimeOffset.Parse(first.GetProperty("nextAttemptAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        (next - h.Factory.Time.GetUtcNow()).TotalSeconds.Should().BeInRange(23.9, 36.1); // 30 s ± 20 %

        (await svc.ProcessRetriesAsync(default)).Should().Be(0);
        h.Factory.Time.Advance(TimeSpan.FromSeconds(37));
        (await svc.ProcessRetriesAsync(default)).Should().Be(1);
        var second = (await DeliveriesAsync(h)).EnumerateArray().Single();
        (second.GetProperty("status").GetString(), second.GetProperty("attempts").GetInt32()).Should().Be(("failed", 2));
        h.Factory.Time.Advance(TimeSpan.FromSeconds(150)); // 2 min ± 20 %
        (await svc.ProcessRetriesAsync(default)).Should().Be(1);
        var third = (await DeliveriesAsync(h)).EnumerateArray().Single();
        (third.GetProperty("status").GetString(), third.GetProperty("attempts").GetInt32(), third.GetProperty("httpStatus").GetInt32(), third.GetProperty("lastError").ValueKind, third.GetProperty("nextAttemptAt").ValueKind)
            .Should().Be(("sent", 3, 200, JsonValueKind.Null, JsonValueKind.Null));
        _transport.Requests.Should().HaveCount(3);
        _transport.Requests.Select(r => r.Body).Distinct().Should().HaveCount(1, "a retry replays the stored event byte for byte");
    }

    [Fact]
    public async Task Dead_Delivery_Raises_One_Critical_App_Error_To_The_Other_Channels()
    {
        var h = await HarnessAsync();
        var bad = await CreateAsync(h, new { type = "webhook", name = "bad", config = new { url = "https://bad.example/in" } });
        var good = await CreateAsync(h, new { type = "ntfy", name = "good", config = new { serverUrl = "https://ntfy.example", topic = "qm" } });
        _transport.Respond = r => r.Url.StartsWith("https://bad.example", StringComparison.Ordinal) ? 502 : 200;
        var svc = h.Factory.Services.GetRequiredService<INotificationService>();
        await EmitAsync(h, new NotificationEvent("run.failed", "critical", "Boom", "m", "run:1"));
        for (var i = 0; i < 4; i++)
        {
            h.Factory.Time.Advance(TimeSpan.FromMinutes(i == 3 ? 50 : 15));
            await svc.ProcessRetriesAsync(default);
            await svc.FlushAsync();
        }

        var rows = (await svc.ListDeliveriesAsync(50, null, null, default)).ToList(); // the admin session itself expired with the fake clock
        rows.Single(d => d.ChannelId == bad && d.EventType == "run.failed").Status.Should().Be("dead");
        var self = rows.Where(d => d.EventType == "app.error").ToList();
        self.Should().ContainSingle();
        self[0].ChannelId.Should().Be(good);
        self[0].Severity.Should().Be("critical");
        self[0].Status.Should().Be("sent");
    }

    [Fact]
    public async Task Deliveries_Query_Parameters_Are_Validated_And_Applied()
    {
        var h = await HarnessAsync();
        var a = await CreateAsync(h, Webhook("a"));
        await CreateAsync(h, Webhook("b"));
        for (var i = 0; i < 3; i++)
        {
            await EmitAsync(h, new NotificationEvent("run.failed", "warning", $"T{i}", "m", $"k{i}"));
        }

        (await DeliveriesAsync(h)).GetArrayLength().Should().Be(6);
        (await DeliveriesAsync(h, "?limit=4")).GetArrayLength().Should().Be(4);
        (await DeliveriesAsync(h, $"?channelId={a}")).EnumerateArray().Should().OnlyContain(d => d.GetProperty("channelId").GetString() == a);
        var all = (await DeliveriesAsync(h)).EnumerateArray().Select(d => d.GetProperty("id").GetInt64()).ToList();
        all.Should().BeInDescendingOrder();
        (await DeliveriesAsync(h, $"?beforeId={all[2]}")).EnumerateArray().Select(d => d.GetProperty("id").GetInt64()).Should().Equal(all.Skip(3));
        (await h.Admin.GetAsync("/notifications/deliveries?limit=0")).Status.Should().Be(400);
        (await h.Admin.GetAsync("/notifications/deliveries?limit=501")).Status.Should().Be(400);
        (await h.Admin.GetAsync("/notifications/deliveries?limit=abc")).Status.Should().Be(400);
        (await h.Admin.GetAsync("/notifications/deliveries?beforeId=0")).Status.Should().Be(400);
        var unknown = await h.Admin.GetAsync("/notifications/deliveries?bogus=1");
        unknown.Status.Should().Be(400);
        unknown.Details.Should().Contain(("", "Unrecognized key(s) in object: 'bogus'"));
    }

    [Fact]
    public async Task Deleting_A_Channel_Keeps_Its_Log_With_A_Null_Name_And_Resets_Dedupe()
    {
        var h = await HarnessAsync();
        var id = await CreateAsync(h, Webhook());
        await EmitAsync(h, new NotificationEvent("run.failed", "warning", "T", "m", "k"));
        (await h.Admin.SendAsync(HttpMethod.Delete, $"/notifications/channels/{id}")).Status.Should().Be(204);
        var row = (await DeliveriesAsync(h)).EnumerateArray().Single();
        row.GetProperty("channelName").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("channelId").GetString().Should().Be(id);
    }

    // ---- audit -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Channel_Changes_Are_Audited_Without_Secrets()
    {
        var h = await HarnessAsync();
        var id = await CreateAsync(h, Webhook());
        (await h.Admin.SendAsync(HttpMethod.Patch, $"/notifications/channels/{id}", new { enabled = false, config = new { secret = "brand-new-secret-77" } })).Status.Should().Be(200);
        (await h.Admin.PostAsync($"/notifications/channels/{id}/test")).Status.Should().Be(200);
        (await h.Admin.SendAsync(HttpMethod.Delete, $"/notifications/channels/{id}")).Status.Should().Be(204);
        var audit = await h.Admin.GetAsync("/audit?limit=100");
        var entries = audit.Json.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("action").GetString()!.StartsWith("notifications.", StringComparison.Ordinal)).ToList();
        entries.Select(e => e.GetProperty("action").GetString()).Should().BeEquivalentTo("notifications.channel.create", "notifications.channel.update", "notifications.channel.test", "notifications.channel.delete");
        entries.Should().OnlyContain(e => e.GetProperty("actor").GetString() == "admin" && e.GetProperty("target").GetString() == id);
        audit.Body.Should().NotContain(HookSecret).And.NotContain("brand-new-secret-77").And.NotContain("XXXXSecretPathTokenXXXX");
    }

    // ---- monitors produce real deliveries --------------------------------------------------------------------------------------

    [Fact]
    public async Task Monitor_Events_Now_Become_Real_Deliveries()
    {
        var h = await HarnessAsync();
        await CreateAsync(h, new { type = "webhook", name = "ops", events = new[] { "updates.available" }, config = new { url = "https://hooks.example.com/ops" } });
        await h.Factory.Services.GetRequiredService<IMonitorEvaluator>().TickAsync(default);
        await h.Factory.Services.GetRequiredService<INotificationService>().FlushAsync();
        var req = _transport.Requests.Should().ContainSingle().Which;
        var body = JsonElement.Parse(req.Body!);
        body.GetProperty("type").GetString().Should().Be("updates.available");
        body.GetProperty("severity").GetString().Should().Be("info");
        body.GetProperty("data").GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        var row = (await DeliveriesAsync(h)).EnumerateArray().Single();
        (row.GetProperty("eventType").GetString(), row.GetProperty("status").GetString()).Should().Be(("updates.available", "sent"));

        // the daily marker keeps the next tick quiet
        await h.Factory.Services.GetRequiredService<IMonitorEvaluator>().TickAsync(default);
        await h.Factory.Services.GetRequiredService<INotificationService>().FlushAsync();
        _transport.Requests.Should().HaveCount(1);
        (await DeliveriesAsync(h)).GetArrayLength().Should().Be(1);
        HttpStatusCode.OK.Should().Be(HttpStatusCode.OK);
    }
}

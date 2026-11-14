using System.Text.Json.Nodes;
using FluentAssertions;
using Quaymaster.Domain.Notifications;
using Xunit;

namespace Quaymaster.UnitTests.Notifications;

/// <summary>Snapshot-style asserts transcribed from the original channels/*.ts for one fixed event.</summary>
public sealed class RenderingTests
{
    // ts 1700000000000 = 2023-11-14T22:13:20.000Z
    private static OutboundEvent Event(string severity = "critical", JsonObject? data = null, string title = "Update failed: shop", string message = "Health check failed") =>
        new("run.failed", severity, title, message, 1_700_000_000_000, Data: data ?? new JsonObject { ["run"] = "r1", ["attempts"] = 2, ["ok"] = false });

    private static OutboundHttpRequest Http(string kind, string config, OutboundEvent e)
    {
        var k = Channels.Get(kind)!;
        var parsed = k.Parse(JsonNode.Parse(config));
        parsed.Issues.Should().BeEmpty();
        return k.Render(parsed.Config!, e).Http!;
    }

    private static string Header(OutboundHttpRequest r, string name) => r.Headers.Single(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void Webhook_Body_Headers_And_Signature()
    {
        var r = Http("webhook", """{"url":"http://127.0.0.1:9/hook","secret":"s3cret-key","headers":{"X-Token":"t1"}}""", Event());
        r.Method.Should().Be("POST");
        r.Url.Should().Be("http://127.0.0.1:9/hook");
        r.Body.Should().Be("""{"type":"run.failed","severity":"critical","title":"Update failed: shop","message":"Health check failed","data":{"run":"r1","attempts":2,"ok":false},"time":"2023-11-14T22:13:20.000Z"}""");
        r.Headers.Select(h => h.Key).Should().Equal("X-Token", "content-type", "user-agent", "X-Quaymaster-Timestamp", "X-Quaymaster-Signature");
        Header(r, "content-type").Should().Be("application/json");
        Header(r, "user-agent").Should().Be("Quaymaster");
        Header(r, "X-Quaymaster-Timestamp").Should().Be("1700000000");
        // openssl dgst -sha256 -hmac s3cret-key over the exact body
        Header(r, "X-Quaymaster-Signature").Should().Be("sha256=b9325d0d26c9a5f1cf98280b9b78497ea4e66411f8732fbb67f0fab6a2f464d8");
    }

    [Fact]
    public void Webhook_Without_Secret_Has_No_Signature_And_Empty_Data_Is_An_Object()
    {
        var r = Http("webhook", """{"url":"https://h/x"}""", new OutboundEvent("test", "info", "T", "M", 1_700_000_000_000));
        r.Headers.Select(h => h.Key).Should().NotContain("X-Quaymaster-Signature");
        r.Body.Should().Be("""{"type":"test","severity":"info","title":"T","message":"M","data":{},"time":"2023-11-14T22:13:20.000Z"}""");
    }

    [Fact]
    public void Webhook_Json_Escaping_Is_JavaScript_Not_SystemTextJson()
    {
        var r = Http("webhook", """{"url":"https://h/x"}""", Event(title: "café <b>&'\"</b> \u2603 \ud83d\ude00", message: "line1\nline2\t\\ \u0001"));
        r.Body.Should().Contain("\"title\":\"café <b>&'\\\"</b> \u2603 \ud83d\ude00\"");
        r.Body.Should().Contain("\"message\":\"line1\\nline2\\t\\\\ \\u0001\"");
    }

    [Fact]
    public void Ntfy_Request()
    {
        var r = Http("ntfy", """{"serverUrl":"http://192.168.1.5:8080/","topic":"quay","token":"tk_1"}""", Event());
        r.Url.Should().Be("http://192.168.1.5:8080/quay");
        r.Method.Should().Be("POST");
        r.Body.Should().Be("Health check failed");
        r.Headers.Select(h => $"{h.Key}: {h.Value}").Should().Equal(
            "Title: Update failed: shop",
            "Priority: 5",
            "Tags: rotating_light,run.failed",
            "content-type: text/plain; charset=utf-8",
            "Authorization: Bearer tk_1");
    }

    [Theory]
    [InlineData("info", "3", "information_source")]
    [InlineData("warning", "4", "warning")]
    [InlineData("critical", "5", "rotating_light")]
    public void Ntfy_Priority_And_Tags_Per_Severity(string sev, string prio, string tag)
    {
        var r = Http("ntfy", """{"serverUrl":"https://ntfy.sh","topic":"t"}""", Event(sev));
        Header(r, "Priority").Should().Be(prio);
        Header(r, "Tags").Should().Be($"{tag},run.failed");
        r.Headers.Select(h => h.Key).Should().NotContain("Authorization");
    }

    [Fact]
    public void Ntfy_Title_Header_Is_Sanitised()
    {
        Header(Http("ntfy", """{"serverUrl":"https://ntfy.sh","topic":"t"}""", Event(title: "a\r\nb  ")), "Title").Should().Be("a b");
        Header(Http("ntfy", """{"serverUrl":"https://ntfy.sh","topic":"t"}""", Event(title: "café")), "Title").Should().Be("=?UTF-8?B?Y2Fmw6k=?=");
    }

    [Fact]
    public void Gotify_Request()
    {
        var r = Http("gotify", """{"serverUrl":"https://gotify.example.com///","appToken":"AppTok"}""", Event("warning"));
        r.Url.Should().Be("https://gotify.example.com/message");
        r.Headers.Select(h => $"{h.Key}: {h.Value}").Should().Equal("content-type: application/json", "X-Gotify-Key: AppTok");
        r.Body.Should().Be("""{"title":"Update failed: shop","message":"Health check failed","priority":5,"extras":{"client::display":{"contentType":"text/plain"}}}""");
        Http("gotify", """{"serverUrl":"https://g","appToken":"a"}""", Event("info")).Body.Should().Contain("\"priority\":3");
        Http("gotify", """{"serverUrl":"https://g","appToken":"a"}""", Event("critical")).Body.Should().Contain("\"priority\":8");
    }

    [Fact]
    public void Discord_Embed()
    {
        var r = Http("discord", """{"url":"https://discord.example/api/webhooks/1/tok"}""", Event());
        r.Url.Should().Be("https://discord.example/api/webhooks/1/tok");
        r.Headers.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("content-type", "application/json"));
        r.Body.Should().Be("""{"username":"Quaymaster","allowed_mentions":{"parse":[]},"embeds":[{"title":"Update failed: shop","description":"Health check failed","color":15158332,"timestamp":"2023-11-14T22:13:20.000Z","footer":{"text":"critical · run.failed"},"fields":[{"name":"run","value":"r1","inline":true},{"name":"attempts","value":"2","inline":true},{"name":"ok","value":"false","inline":true}]}]}""");
    }

    [Theory]
    [InlineData("info", 3447003)]
    [InlineData("warning", 15844367)]
    [InlineData("critical", 15158332)]
    public void Discord_Colors(string sev, int color) => Http("discord", """{"url":"https://d/x"}""", Event(sev)).Body.Should().Contain($"\"color\":{color},");

    [Fact]
    public void Discord_Limits()
    {
        var data = new JsonObject();
        for (var i = 0; i < 30; i++)
        {
            data[$"k{i}"] = i;
        }

        var body = JsonNode.Parse(Http("discord", """{"url":"https://d/x"}""", Event(data: data, title: new string('t', 300), message: new string('m', 4100))).Body!)!;
        var embed = body["embeds"]![0]!;
        ((string)embed["title"]!).Length.Should().Be(256);
        ((string)embed["title"]!).Should().EndWith("…");
        ((string)embed["description"]!).Length.Should().Be(4000);
        ((JsonArray)embed["fields"]!).Count.Should().Be(25);
        Http("discord", """{"url":"https://d/x"}""", Event(data: new JsonObject { ["empty"] = "", ["n"] = null })).Body.Should().Contain("""{"name":"empty","value":"-","inline":true},{"name":"n","value":"null","inline":true}""");
        var longValue = JsonNode.Parse(Http("discord", """{"url":"https://d/x"}""", Event(data: new JsonObject { ["long"] = new string('v', 1100) })).Body!)!["embeds"]![0]!["fields"]![0]!["value"]!;
        longValue.GetValue<string>().Length.Should().Be(1024);
    }

    [Fact]
    public void Slack_Attachment_With_Blocks()
    {
        var r = Http("slack", """{"url":"https://hooks.slack.example/services/T/B/x"}""", Event(message: "a<b>&c"));
        r.Body.Should().Be("""{"text":"Update failed: shop: a<b>&c","attachments":[{"color":"#e01e5a","blocks":[{"type":"header","text":{"type":"plain_text","text":"Update failed: shop","emoji":false}},{"type":"section","text":{"type":"mrkdwn","text":"a&lt;b&gt;&amp;c"}},{"type":"section","fields":[{"type":"mrkdwn","text":"*run*\nr1"},{"type":"mrkdwn","text":"*attempts*\n2"},{"type":"mrkdwn","text":"*ok*\nfalse"}]},{"type":"context","elements":[{"type":"mrkdwn","text":"critical · run.failed"}]}]}]}""");
    }

    [Fact]
    public void Slack_Without_Data_Has_No_Fields_Section_And_Empty_Message_Is_A_Space()
    {
        var body = Http("slack", """{"url":"https://s/x"}""", new OutboundEvent("test", "info", "T", "", 1_700_000_000_000)).Body!;
        body.Should().NotContain("\"fields\"");
        body.Should().Contain("""{"type":"mrkdwn","text":" "}""");
        body.Should().Contain("\"color\":\"#3498db\"");
        body.Should().StartWith("""{"text":"T: ",""");
    }

    [Fact]
    public void Telegram_Request_Embeds_The_Token_In_The_Path()
    {
        var r = Http("telegram", """{"botToken":"123:ABC","chatId":"-100200"}""", Event());
        r.Url.Should().Be("https://api.telegram.org/bot123:ABC/sendMessage");
        r.Body.Should().Be("""{"chat_id":"-100200","text":"[CRITICAL] Update failed: shop\nHealth check failed","disable_web_page_preview":true}""");
        Http("telegram", """{"botToken":"123:ABC","chatId":"@chan","apiBase":"http://127.0.0.1:8081/"}""", Event()).Url.Should().Be("http://127.0.0.1:8081/bot123:ABC/sendMessage");
        Http("telegram", """{"botToken":"123:ABC","chatId":"1"}""", Event("warning")).Body.Should().Contain("[WARNING]");
        Http("telegram", """{"botToken":"123:ABC","chatId":"1"}""", Event("info")).Body.Should().Contain("[INFO]");
    }

    [Fact]
    public void Smtp_Mail()
    {
        var k = Channels.Get("smtp")!;
        var cfg = k.Parse(JsonNode.Parse("""{"host":"mail.example.com","port":465,"secure":true,"username":"u","password":"p","from":"qm@example.com","to":["a@example.com","b@example.com"]}""")).Config!;
        var m = k.Render(cfg, Event()).Mail!;
        m.Host.Should().Be("mail.example.com");
        m.Port.Should().Be(465);
        m.Secure.Should().BeTrue();
        (m.Username, m.Password).Should().Be(("u", "p"));
        m.From.Should().Be("qm@example.com");
        m.To.Should().Equal("a@example.com", "b@example.com");
        m.Subject.Should().Be("[CRITICAL] Update failed: shop");
        m.Text.Should().Be("Health check failed\nrun: r1\nattempts: 2\nok: false");
        var plain = k.Render(k.Parse(JsonNode.Parse("""{"host":"h","port":25,"from":"a@b.co","to":["c@d.co"]}""")).Config!, Event("info")).Mail!;
        (plain.Username, plain.Password, plain.Secure).Should().Be((null, null, false));
        k.Render(cfg, Event(title: "multi\r\nline " + new string('x', 300))).Mail!.Subject.Length.Should().Be(200);
    }

    [Fact]
    public void Js_Number_And_String_Formatting()
    {
        JsJson.ToJsString(JsonValue.Create(1.5)).Should().Be("1.5");
        JsJson.ToJsString(JsonValue.Create(3.0)).Should().Be("3");
        JsJson.ToJsString(JsonValue.Create(5_368_709_120L)).Should().Be("5368709120");
        JsJson.ToJsString(null).Should().Be("null");
        JsJson.ToJsString(JsonValue.Create(true)).Should().Be("true");
        JsJson.Stringify(new JsonObject { ["a"] = new JsonArray(1, "x", null) }).Should().Be("""{"a":[1,"x",null]}""");
    }
}

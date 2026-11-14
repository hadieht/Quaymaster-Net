using System.Text.Json.Nodes;
using FluentAssertions;
using Quaymaster.Domain.Notifications;
using Xunit;

namespace Quaymaster.UnitTests.Notifications;

public sealed class ChannelConfigTests
{
    private static ConfigParseResult Parse(string kind, string json) => Channels.Get(kind)!.Parse(JsonNode.Parse(json));

    private static string[] Issues(ConfigParseResult r) => [.. r.Issues.Select(i => $"{i.Path}|{i.Message}")];

    [Fact]
    public void All_Seven_Kinds_Are_Registered_In_The_Original_Order() =>
        Channels.All.Select(k => k.Type).Should().Equal("webhook", "ntfy", "gotify", "discord", "slack", "telegram", "smtp");

    [Fact]
    public void Webhook_Accepts_Minimal_And_Full_Config_And_Keeps_Schema_Order()
    {
        Parse("webhook", """{"url":"https://hooks.example.com/x"}""").Config!.ToJsonString().Should().Be("""{"url":"https://hooks.example.com/x"}""");
        var full = Parse("webhook", """{"headers":{"X-Token":"t"},"secret":"abc","url":"http://10.0.0.5:8080/in"}""");
        full.Issues.Should().BeEmpty();
        full.Config!.ToJsonString().Should().Be("""{"url":"http://10.0.0.5:8080/in","secret":"abc","headers":{"X-Token":"t"}}""");
    }

    [Theory]
    [InlineData("""{}""", "url|Required")]
    [InlineData("""{"url":""}""", "url|String must contain at least 1 character(s)")]
    [InlineData("""{"url":5}""", "url|Expected string, received number")]
    [InlineData("""{"url":"ftp://x/y"}""", "url|Only http and https URLs are allowed")]
    [InlineData("""{"url":"http://u:p@host/x"}""", "url|URLs with embedded credentials (user:password@) are not allowed")]
    [InlineData("""{"url":"http://169.254.169.254/latest"}""", "url|Destination address 169.254.169.254 is not allowed (link-local (cloud metadata range))")]
    [InlineData("""{"url":"https://h/x","secret":""}""", "secret|String must contain at least 1 character(s)")]
    [InlineData("""{"url":"https://h/x","headers":{"Content-Type":"x"}}""", "headers.Content-Type|reserved header name")]
    [InlineData("""{"url":"https://h/x","headers":{"X-Quaymaster-Signature":"x"}}""", "headers.X-Quaymaster-Signature|reserved header name")]
    [InlineData("""{"url":"https://h/x","headers":{"bad name":"x"}}""", "headers.bad name|invalid header name")]
    [InlineData("""{"url":"https://h/x","headers":{"X-A":"a\nb"}}""", "headers.X-A|invalid header value")]
    [InlineData("""{"url":"https://h/x","headers":[]}""", "headers|Expected object, received array")]
    [InlineData("""{"url":"https://h/x","extra":1,"more":2}""", "|Unrecognized key(s) in object: 'extra', 'more'")]
    public void Webhook_Rejects_With_Zod_Paths_And_Messages(string json, string expected) =>
        Issues(Parse("webhook", json)).Should().Contain(expected);

    [Fact]
    public void Webhook_Url_Length_Boundary()
    {
        var ok = "https://h.example/" + new string('a', 2048 - 18);
        Parse("webhook", $$"""{"url":"{{ok}}"}""").Issues.Should().BeEmpty();
        Issues(Parse("webhook", $$"""{"url":"{{ok}}a"}""")).Should().Contain("url|String must contain at most 2048 character(s)");
    }

    [Fact]
    public void Webhook_Header_Value_And_Name_Length_Boundaries()
    {
        Parse("webhook", $$$"""{"url":"https://h/x","headers":{"{{{new string('A', 64)}}}":"{{{new string('v', 1024)}}}"}}""").Issues.Should().BeEmpty();
        Issues(Parse("webhook", $$$"""{"url":"https://h/x","headers":{"{{{new string('A', 65)}}}":"{{{new string('v', 1025)}}}"}}"""))
            .Should().Contain([$"headers.{new string('A', 65)}|invalid header name", $"headers.{new string('A', 65)}|String must contain at most 1024 character(s)"]);
    }

    [Fact]
    public void Ntfy_Topic_And_Token_Rules()
    {
        Parse("ntfy", """{"serverUrl":"http://192.168.1.10:8080","topic":"quay_master-1"}""").Issues.Should().BeEmpty();
        Parse("ntfy", $$"""{"serverUrl":"https://ntfy.sh","topic":"{{new string('t', 64)}}","token":"tk_abc"}""").Issues.Should().BeEmpty();
        var msg = "topic|topic may contain letters, digits, _ and -";
        Issues(Parse("ntfy", $$"""{"serverUrl":"https://ntfy.sh","topic":"{{new string('t', 65)}}"}""")).Should().Contain(msg);
        Issues(Parse("ntfy", """{"serverUrl":"https://ntfy.sh","topic":"a/b"}""")).Should().Contain(msg);
        Issues(Parse("ntfy", """{"serverUrl":"https://ntfy.sh","topic":""}""")).Should().Contain(msg);
        Issues(Parse("ntfy", """{"serverUrl":"https://ntfy.sh"}""")).Should().Contain("topic|Required");
        Issues(Parse("ntfy", """{"serverUrl":"https://ntfy.sh","topic":"a","token":""}""")).Should().Contain("token|String must contain at least 1 character(s)");
        Issues(Parse("ntfy", """{"serverUrl":"https://ntfy.sh","topic":"a","priority":3}""")).Should().Contain("|Unrecognized key(s) in object: 'priority'");
    }

    [Fact]
    public void Gotify_Requires_Server_And_App_Token()
    {
        Parse("gotify", """{"serverUrl":"https://gotify.local","appToken":"A1b2"}""").Issues.Should().BeEmpty();
        Issues(Parse("gotify", """{}""")).Should().BeEquivalentTo("serverUrl|Required", "appToken|Required");
        Issues(Parse("gotify", $$"""{"serverUrl":"https://g","appToken":"{{new string('x', 513)}}"}""")).Should().Contain("appToken|String must contain at most 512 character(s)");
    }

    [Fact]
    public void Discord_And_Slack_Take_A_Single_Url()
    {
        foreach (var kind in new[] { "discord", "slack" })
        {
            Parse(kind, """{"url":"https://example.com/api/webhooks/1/abc"}""").Issues.Should().BeEmpty();
            Issues(Parse(kind, """{"url":"https://example.com/x","serverUrl":"y"}""")).Should().Contain("|Unrecognized key(s) in object: 'serverUrl'");
            Issues(Parse(kind, """{"url":"not a url"}""")).Should().Contain("url|Invalid URL");
        }
    }

    [Theory]
    [InlineData("""{"botToken":"123456:ABC-DEF","chatId":"-1001234567890"}""", true)]
    [InlineData("""{"botToken":"123456:ABC-DEF","chatId":"@my_channel"}""", true)]
    [InlineData("""{"botToken":"123456:ABC-DEF","chatId":"42","apiBase":"http://127.0.0.1:8081"}""", true)]
    [InlineData("""{"botToken":"ab","chatId":"42"}""", false)]
    [InlineData("""{"botToken":"abc/def","chatId":"42"}""", false)]
    [InlineData("""{"botToken":"abc def","chatId":"42"}""", false)]
    [InlineData("""{"botToken":"abc","chatId":"@ab"}""", false)]
    [InlineData("""{"botToken":"abc","chatId":"12x"}""", false)]
    [InlineData("""{"botToken":"abc","chatId":"123456789012345678901"}""", false)]
    [InlineData("""{"botToken":"abc","chatId":"42","apiBase":"ftp://x"}""", false)]
    public void Telegram_Token_ChatId_And_ApiBase(string json, bool valid) => Parse("telegram", json).Issues.Count.Should().Be(valid ? 0 : 1, json);

    [Fact]
    public void Telegram_Messages()
    {
        Issues(Parse("telegram", """{"botToken":"a/b","chatId":"x"}""")).Should().Contain("botToken|invalid token format").And.Contain("chatId|chatId must be numeric or @channelname");
        Issues(Parse("telegram", """{"botToken":"ab","chatId":"1"}""")).Should().Contain("botToken|String must contain at least 3 character(s)");
    }

    [Fact]
    public void Smtp_Defaults_Secure_False_And_Validates_Every_Field()
    {
        var ok = Parse("smtp", """{"host":"mail.example.com","port":587,"from":"qm@example.com","to":["a@example.com","b@example.org"]}""");
        ok.Issues.Should().BeEmpty();
        ok.Config!.ToJsonString().Should().Be("""{"host":"mail.example.com","port":587,"secure":false,"from":"qm@example.com","to":["a@example.com","b@example.org"]}""");
        Parse("smtp", """{"host":"[::1]","port":465,"secure":true,"username":"u","password":"p","from":"a@b.co","to":["x@y.io"]}""").Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"host":"","port":25,"from":"a@b.co","to":["x@y.io"]}""", "host|String must contain at least 1 character(s)")]
    [InlineData("""{"host":"bad host","port":25,"from":"a@b.co","to":["x@y.io"]}""", "host|invalid host")]
    [InlineData("""{"host":"h","port":0,"from":"a@b.co","to":["x@y.io"]}""", "port|Number must be greater than or equal to 1")]
    [InlineData("""{"host":"h","port":65536,"from":"a@b.co","to":["x@y.io"]}""", "port|Number must be less than or equal to 65535")]
    [InlineData("""{"host":"h","port":25.5,"from":"a@b.co","to":["x@y.io"]}""", "port|Expected integer, received float")]
    [InlineData("""{"host":"h","port":"25","from":"a@b.co","to":["x@y.io"]}""", "port|Expected number, received string")]
    [InlineData("""{"host":"h","port":25,"secure":"yes","from":"a@b.co","to":["x@y.io"]}""", "secure|Expected boolean, received string")]
    [InlineData("""{"host":"h","port":25,"from":"nope","to":["x@y.io"]}""", "from|Invalid email")]
    [InlineData("""{"host":"h","port":25,"from":"a@b.co","to":[]}""", "to|Array must contain at least 1 element(s)")]
    [InlineData("""{"host":"h","port":25,"from":"a@b.co","to":["x@y.io","bad"]}""", "to.1|Invalid email")]
    [InlineData("""{"host":"h","port":25,"from":"a@b.co","to":"x@y.io"}""", "to|Expected array, received string")]
    [InlineData("""{"host":"h","port":25,"from":"a@b.co","to":["x@y.io"],"username":""}""", "username|String must contain at least 1 character(s)")]
    [InlineData("""{"host":"h","port":25,"from":"a@b.co","to":["x@y.io"],"password":""}""", "password|String must contain at least 1 character(s)")]
    [InlineData("""{"port":25,"from":"a@b.co","to":["x@y.io"]}""", "host|Required")]
    public void Smtp_Rejections(string json, string expected) => Issues(Parse("smtp", json)).Should().Contain(expected);

    [Fact]
    public void Smtp_Recipient_Count_Boundary()
    {
        string To(int n) => string.Join(',', Enumerable.Range(0, n).Select(i => $"\"u{i}@example.com\""));
        Parse("smtp", $$"""{"host":"h","port":25,"from":"a@b.co","to":[{{To(20)}}]}""").Issues.Should().BeEmpty();
        Issues(Parse("smtp", $$"""{"host":"h","port":25,"from":"a@b.co","to":[{{To(21)}}]}""")).Should().Contain("to|Array must contain at most 20 element(s)");
    }

    [Fact]
    public void Non_Object_Config_And_Null_Are_Handled_Like_The_Original()
    {
        Issues(Channels.Get("webhook")!.Parse(JsonNode.Parse("[]"))).Should().Equal("|Expected object, received array");
        Issues(Channels.Get("webhook")!.Parse(null)).Should().Equal("url|Required"); // raw ?? {}
    }

    [Fact]
    public void Every_Kind_Declares_Secret_Fields_The_Api_Must_Mask()
    {
        Channels.Get("webhook")!.Fields.Where(f => f.Secret).Select(f => f.Name).Should().Equal("url", "secret", "headers");
        Channels.Get("ntfy")!.Fields.Where(f => f.Secret).Select(f => f.Name).Should().Equal("serverUrl", "token");
        Channels.Get("gotify")!.Fields.Where(f => f.Secret).Select(f => f.Name).Should().Equal("serverUrl", "appToken");
        Channels.Get("discord")!.Fields.Single().Secret.Should().BeTrue();
        Channels.Get("slack")!.Fields.Single().Secret.Should().BeTrue();
        Channels.Get("telegram")!.Fields.Where(f => f.Secret).Select(f => f.Name).Should().Equal("botToken");
        Channels.Get("smtp")!.Fields.Where(f => f.Secret).Select(f => f.Name).Should().Equal("password");
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Notifications;

/// <summary>One channel kind: its field catalogue, config schema and message rendering (port of channels/*.ts).</summary>
public interface IChannelKind
{
    string Type { get; }

    string Label { get; }

    IReadOnlyList<ChannelFieldSpec> Fields { get; }

    /// <summary>Validates and normalises a raw config (zod semantics, every issue reported, secrets never echoed).</summary>
    ConfigParseResult Parse(JsonNode? raw);

    RenderedMessage Render(JsonObject config, OutboundEvent e);
}

public static class Channels
{
    public static IReadOnlyList<IChannelKind> All { get; } = [new WebhookKind(), new NtfyKind(), new GotifyKind(), new DiscordKind(), new SlackKind(), new TelegramKind(), new SmtpKind()];

    public static IChannelKind? Get(string type) => All.FirstOrDefault(k => k.Type == type);

    public static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Truncate(string s, int max) => s.Length <= max ? s : string.Concat(s.AsSpan(0, Math.Max(0, max - 1)), "…");

    public static string TrimSlash(string url) => url.TrimEnd('/');

    /// <summary>Makes a value safe for an HTTP header (no CR/LF; RFC 2047 for non-ASCII).</summary>
    public static string HeaderSafe(string v)
    {
        var clean = Regex.Replace(v, "[\r\n]+", " ").Trim();
        return clean.All(c => c is >= ' ' and <= '~') ? clean : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(clean))}?=";
    }

    public static string SeverityLabel(string severity) => severity switch { "info" => "INFO", "warning" => "WARNING", _ => "CRITICAL" };

    internal static ConfigParseResult ParseObject(JsonNode? raw, Func<ConfigReader, ConfigParseResult> body)
    {
        if (raw is null)
        {
            raw = new JsonObject();
        }

        if (raw is not JsonObject o)
        {
            return new ConfigParseResult(null, [new ConfigIssue(string.Empty, $"Expected object, received {ConfigReader.Received(raw, true)}")]);
        }

        return body(new ConfigReader(o));
    }

    internal static OutboundHttpRequest Post(string url, IEnumerable<KeyValuePair<string, string>> headers, string body) =>
        new(url, "POST", [.. headers], body);

    internal static string Str(JsonObject c, string key) => (string?)c[key] ?? string.Empty;

    internal static IEnumerable<KeyValuePair<string, JsonNode?>> DataEntries(OutboundEvent e) => e.Data ?? [];

    internal static KeyValuePair<string, string> H(string k, string v) => new(k, v);
}

internal sealed partial class WebhookKind : IChannelKind
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-type", "content-length", "host", "x-quaymaster-signature", "x-quaymaster-timestamp",
    };

    public string Type => "webhook";

    public string Label => "Webhook (JSON)";

    public IReadOnlyList<ChannelFieldSpec> Fields { get; } =
    [
        new("url", "URL", "url", true, true),
        new("secret", "Signing secret", "secret", false, true, "Adds X-Quaymaster-Signature: sha256=<HMAC-SHA256 of the body>"),
        new("headers", "Extra headers", "secret", false, true, "JSON object of additional request headers (may carry credentials)"),
    ];

    public ConfigParseResult Parse(JsonNode? raw) => Channels.ParseObject(raw, r =>
    {
        r.String("url", true, ConfigReader.Min(1), ConfigReader.Max(2048), ConfigReader.OutboundUrl);
        r.String("secret", false, ConfigReader.Min(1), ConfigReader.Max(512));
        r.StringRecord(
            "headers",
            [ConfigReader.Re(HeaderName(), "invalid header name"), k => Reserved.Contains(k) ? "reserved header name" : null],
            [ConfigReader.Max(1024), v => v.Contains('\r', StringComparison.Ordinal) || v.Contains('\n', StringComparison.Ordinal) ? "invalid header value" : null]);
        return r.Finish();
    });

    /// <summary>The exact JSON body (also the HMAC input).</summary>
    public static string Body(OutboundEvent e) => JsJson.Stringify(new JsonObject
    {
        ["type"] = e.Type,
        ["severity"] = e.Severity,
        ["title"] = e.Title,
        ["message"] = e.Message,
        ["data"] = e.Data is null ? new JsonObject() : JsonNode.Parse(e.Data.ToJsonString()),
        ["time"] = Channels.Iso(e.Ts),
    });

    public RenderedMessage Render(JsonObject config, OutboundEvent e)
    {
        var body = Body(e);
        var headers = new List<KeyValuePair<string, string>>();
        if (config["headers"] is JsonObject extra)
        {
            headers.AddRange(extra.Select(p => new KeyValuePair<string, string>(p.Key, (string?)p.Value ?? string.Empty)));
        }

        headers.Add(new("content-type", "application/json"));
        headers.Add(new("user-agent", "Quaymaster"));
        headers.Add(new("X-Quaymaster-Timestamp", (e.Ts / 1000).ToString(CultureInfo.InvariantCulture)));
        if (Channels.Str(config, "secret") is { Length: > 0 } secret)
        {
            headers.Add(new("X-Quaymaster-Signature", "sha256=" + Sign(secret, body)));
        }

        return new(Channels.Post(Channels.Str(config, "url"), headers, body), null);
    }

    public static string Sign(string secret, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderName();
}

internal sealed partial class NtfyKind : IChannelKind
{
    public string Type => "ntfy";

    public string Label => "ntfy";

    public IReadOnlyList<ChannelFieldSpec> Fields { get; } =
    [
        new("serverUrl", "Server URL", "url", true, true),
        new("topic", "Topic", "string", true, false),
        new("token", "Access token", "secret", false, true),
    ];

    public ConfigParseResult Parse(JsonNode? raw) => Channels.ParseObject(raw, r =>
    {
        r.String("serverUrl", true, ConfigReader.Min(1), ConfigReader.Max(2048), ConfigReader.OutboundUrl);
        r.String("topic", true, ConfigReader.Re(Topic(), "topic may contain letters, digits, _ and -"));
        r.String("token", false, ConfigReader.Min(1), ConfigReader.Max(512));
        return r.Finish();
    });

    public RenderedMessage Render(JsonObject config, OutboundEvent e)
    {
        var (priority, tag) = e.Severity switch { "info" => (3, "information_source"), "warning" => (4, "warning"), _ => (5, "rotating_light") };
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Title", Channels.HeaderSafe(e.Title)),
            new("Priority", priority.ToString(CultureInfo.InvariantCulture)),
            new("Tags", $"{tag},{e.Type}"),
            new("content-type", "text/plain; charset=utf-8"),
        };
        if (Channels.Str(config, "token") is { Length: > 0 } token)
        {
            headers.Add(new("Authorization", $"Bearer {token}"));
        }

        return new(Channels.Post($"{Channels.TrimSlash(Channels.Str(config, "serverUrl"))}/{Channels.Str(config, "topic")}", headers, e.Message), null);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex Topic();
}

internal sealed class GotifyKind : IChannelKind
{
    public string Type => "gotify";

    public string Label => "Gotify";

    public IReadOnlyList<ChannelFieldSpec> Fields { get; } =
    [
        new("serverUrl", "Server URL", "url", true, true),
        new("appToken", "Application token", "secret", true, true),
    ];

    public ConfigParseResult Parse(JsonNode? raw) => Channels.ParseObject(raw, r =>
    {
        r.String("serverUrl", true, ConfigReader.Min(1), ConfigReader.Max(2048), ConfigReader.OutboundUrl);
        r.String("appToken", true, ConfigReader.Min(1), ConfigReader.Max(512));
        return r.Finish();
    });

    public RenderedMessage Render(JsonObject config, OutboundEvent e)
    {
        var body = JsJson.Stringify(new JsonObject
        {
            ["title"] = e.Title,
            ["message"] = e.Message,
            ["priority"] = e.Severity switch { "info" => 3, "warning" => 5, _ => 8 },
            ["extras"] = new JsonObject { ["client::display"] = new JsonObject { ["contentType"] = "text/plain" } },
        });
        return new(Channels.Post(
            $"{Channels.TrimSlash(Channels.Str(config, "serverUrl"))}/message",
            [Channels.H("content-type", "application/json"), Channels.H("X-Gotify-Key", Channels.Str(config, "appToken"))],
            body), null);
    }
}

internal sealed class DiscordKind : IChannelKind
{
    public string Type => "discord";

    public string Label => "Discord";

    public IReadOnlyList<ChannelFieldSpec> Fields { get; } = [new("url", "Webhook URL", "url", true, true)];

    public ConfigParseResult Parse(JsonNode? raw) => Channels.ParseObject(raw, r =>
    {
        r.String("url", true, ConfigReader.Min(1), ConfigReader.Max(2048), ConfigReader.OutboundUrl);
        return r.Finish();
    });

    public RenderedMessage Render(JsonObject config, OutboundEvent e)
    {
        var fields = new JsonArray();
        foreach (var (k, v) in Channels.DataEntries(e).Take(25))
        {
            var name = Channels.Truncate(k, 256);
            var value = Channels.Truncate(JsJson.ToJsString(v), 1024);
            fields.Add(new JsonObject { ["name"] = name.Length == 0 ? "-" : name, ["value"] = value.Length == 0 ? "-" : value, ["inline"] = true });
        }

        var color = e.Severity switch { "info" => 0x3498db, "warning" => 0xf1c40f, _ => 0xe74c3c };
        var body = JsJson.Stringify(new JsonObject
        {
            ["username"] = "Quaymaster",
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = Channels.Truncate(e.Title, 256),
                ["description"] = Channels.Truncate(e.Message, 4000),
                ["color"] = color,
                ["timestamp"] = Channels.Iso(e.Ts),
                ["footer"] = new JsonObject { ["text"] = $"{e.Severity} · {e.Type}" },
                ["fields"] = fields,
            }),
        });
        return new(Channels.Post(Channels.Str(config, "url"), [Channels.H("content-type", "application/json")], body), null);
    }
}

internal sealed class SlackKind : IChannelKind
{
    public string Type => "slack";

    public string Label => "Slack";

    public IReadOnlyList<ChannelFieldSpec> Fields { get; } = [new("url", "Incoming webhook URL", "url", true, true)];

    public ConfigParseResult Parse(JsonNode? raw) => Channels.ParseObject(raw, r =>
    {
        r.String("url", true, ConfigReader.Min(1), ConfigReader.Max(2048), ConfigReader.OutboundUrl);
        return r.Finish();
    });

    private static string Esc(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public RenderedMessage Render(JsonObject config, OutboundEvent e)
    {
        var text = Channels.Truncate(Esc(e.Message), 3000);
        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "header", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Channels.Truncate(e.Title, 150), ["emoji"] = false } },
            new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = text.Length == 0 ? " " : text } },
        };
        var data = Channels.DataEntries(e).Take(10).ToList();
        if (data.Count > 0)
        {
            var fields = new JsonArray();
            foreach (var (k, v) in data)
            {
                fields.Add(new JsonObject { ["type"] = "mrkdwn", ["text"] = Channels.Truncate($"*{Esc(k)}*\n{Esc(JsJson.ToJsString(v))}", 2000) });
            }

            blocks.Add(new JsonObject { ["type"] = "section", ["fields"] = fields });
        }

        blocks.Add(new JsonObject { ["type"] = "context", ["elements"] = new JsonArray(new JsonObject { ["type"] = "mrkdwn", ["text"] = $"{e.Severity} · {e.Type}" }) });
        var color = e.Severity switch { "info" => "#3498db", "warning" => "#ecb22e", _ => "#e01e5a" };
        var body = JsJson.Stringify(new JsonObject
        {
            ["text"] = Channels.Truncate($"{e.Title}: {e.Message}", 300),
            ["attachments"] = new JsonArray(new JsonObject { ["color"] = color, ["blocks"] = blocks }),
        });
        return new(Channels.Post(Channels.Str(config, "url"), [Channels.H("content-type", "application/json")], body), null);
    }
}

internal sealed partial class TelegramKind : IChannelKind
{
    public string Type => "telegram";

    public string Label => "Telegram";

    public IReadOnlyList<ChannelFieldSpec> Fields { get; } =
    [
        new("botToken", "Bot token", "secret", true, true),
        new("chatId", "Chat ID", "string", true, false),
        new("apiBase", "API base URL", "url", false, false, "Defaults to https://api.telegram.org"),
    ];

    public ConfigParseResult Parse(JsonNode? raw) => Channels.ParseObject(raw, r =>
    {
        r.String("botToken", true, ConfigReader.Min(3), ConfigReader.Max(200), ConfigReader.Re(Token(), "invalid token format"));
        r.String("chatId", true, ConfigReader.Re(ChatId(), "chatId must be numeric or @channelname"));
        r.String("apiBase", false, ConfigReader.Min(1), ConfigReader.Max(2048), ConfigReader.OutboundUrl);
        return r.Finish();
    });

    public RenderedMessage Render(JsonObject config, OutboundEvent e)
    {
        var apiBase = Channels.TrimSlash((string?)config["apiBase"] ?? "https://api.telegram.org");
        var body = JsJson.Stringify(new JsonObject
        {
            ["chat_id"] = Channels.Str(config, "chatId"),
            ["text"] = Channels.Truncate($"[{Channels.SeverityLabel(e.Severity)}] {e.Title}\n{e.Message}", 4000),
            ["disable_web_page_preview"] = true,
        });
        return new(Channels.Post($"{apiBase}/bot{Channels.Str(config, "botToken")}/sendMessage", [Channels.H("content-type", "application/json")], body), null);
    }

    [GeneratedRegex(@"^[^\s/?#]+$")]
    private static partial Regex Token();

    [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z0-9_]{3,64})$")]
    private static partial Regex ChatId();
}

internal sealed partial class SmtpKind : IChannelKind
{
    public string Type => "smtp";

    public string Label => "Email (SMTP)";

    public IReadOnlyList<ChannelFieldSpec> Fields { get; } =
    [
        new("host", "SMTP host", "string", true, false),
        new("port", "Port", "number", true, false),
        new("secure", "Implicit TLS (port 465)", "boolean", false, false),
        new("username", "Username", "string", false, false),
        new("password", "Password", "secret", false, true),
        new("from", "From", "email", true, false),
        new("to", "Recipients", "string_list", true, false),
    ];

    public ConfigParseResult Parse(JsonNode? raw) => Channels.ParseObject(raw, r =>
    {
        r.String("host", true, ConfigReader.Min(1), ConfigReader.Max(255), ConfigReader.Re(Host(), "invalid host"));
        r.Integer("port", true, 1, 65535);
        r.Boolean("secure", false);
        r.String("username", false, ConfigReader.Min(1), ConfigReader.Max(256));
        r.String("password", false, ConfigReader.Min(1), ConfigReader.Max(1024));
        r.String("from", true, ConfigReader.Email, ConfigReader.Max(320));
        r.StringList("to", true, 1, 20, ConfigReader.Email, ConfigReader.Max(320));
        return r.Finish();
    });

    public RenderedMessage Render(JsonObject config, OutboundEvent e)
    {
        var lines = new List<string> { e.Message };
        lines.AddRange(Channels.DataEntries(e).Select(p => $"{p.Key}: {JsJson.ToJsString(p.Value)}"));
        var subject = Channels.Truncate(Regex.Replace($"[{Channels.SeverityLabel(e.Severity)}] {e.Title}", "[\r\n]+", " "), 200);
        return new(
            null,
            new MailMessage(
                Channels.Str(config, "host"),
                (int)((long?)config["port"] ?? 25),
                (bool?)config["secure"] ?? false,
                (string?)config["username"] is { Length: > 0 } u ? u : null,
                (string?)config["password"] is { Length: > 0 } p ? p : null,
                Channels.Str(config, "from"),
                [.. ((JsonArray?)config["to"] ?? []).Select(x => (string?)x ?? string.Empty)],
                subject,
                string.Join('\n', lines)));
    }

    [GeneratedRegex(@"^[A-Za-z0-9.:\-[\]]+$")]
    private static partial Regex Host();
}

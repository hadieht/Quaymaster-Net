using System.Text.Json.Nodes;

namespace Quaymaster.Domain.Notifications;

public static class NotificationCatalog
{
    public static readonly IReadOnlyList<string> EventTypes =
    [
        "container.died", "container.unhealthy", "container.restart_loop", "updates.available", "maintenance.upcoming",
        "maintenance.awaiting_approval", "run.succeeded", "run.failed", "run.rolled_back", "run.partial", "run.skipped",
        "run.interrupted", "run.conflict", "service.left_down", "cleanup.report", "host.disk_low", "host.cpu_high",
        "host.memory_high", "app.error", "test",
    ];

    public static readonly IReadOnlyList<string> Severities = ["info", "warning", "critical"];

    public static readonly IReadOnlyList<string> ChannelKinds = ["webhook", "ntfy", "gotify", "discord", "slack", "telegram", "smtp"];

    public static readonly IReadOnlyList<string> DeliveryStatuses = ["pending", "sent", "failed", "suppressed", "dead"];

    public static int Rank(string severity) => severity switch { "info" => 0, "warning" => 1, "critical" => 2, _ => 0 };
}

/// <summary>An event after sanitising: what channels render and what a retry replays (<c>event_json</c>).</summary>
public sealed record OutboundEvent(
    string Type,
    string Severity,
    string Title,
    string Message,
    long Ts,
    string? DedupeKey = null,
    long? CooldownMs = null,
    JsonObject? Data = null)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["type"] = Type, ["severity"] = Severity, ["title"] = Title, ["message"] = Message };
        if (DedupeKey is not null)
        {
            o["dedupeKey"] = DedupeKey;
        }

        if (CooldownMs is not null)
        {
            o["cooldownMs"] = CooldownMs;
        }

        o["ts"] = Ts;
        if (Data is not null)
        {
            o["data"] = JsonNode.Parse(Data.ToJsonString());
        }

        return o;
    }

    public static OutboundEvent? FromJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o)
            {
                return null;
            }

            return new OutboundEvent(
                (string?)o["type"] ?? "app.error",
                (string?)o["severity"] ?? "info",
                (string?)o["title"] ?? string.Empty,
                (string?)o["message"] ?? string.Empty,
                (long?)o["ts"] ?? 0,
                (string?)o["dedupeKey"],
                (long?)o["cooldownMs"],
                o["data"] as JsonObject);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

public sealed record OutboundHttpRequest(string Url, string Method, IReadOnlyList<KeyValuePair<string, string>> Headers, string? Body, int? TimeoutMs = null);

public sealed record MailMessage(string Host, int Port, bool Secure, string? Username, string? Password, string From, IReadOnlyList<string> To, string Subject, string Text);

/// <summary>What a channel hands to the transport: either an HTTP request or a mail.</summary>
public sealed record RenderedMessage(OutboundHttpRequest? Http, MailMessage? Mail);

public sealed record ConfigIssue(string Path, string Message);

public sealed record ChannelFieldSpec(string Name, string Label, string Kind, bool Required, bool Secret, string? Help = null);

public sealed record ConfigParseResult(JsonObject? Config, IReadOnlyList<ConfigIssue> Issues);

/// <summary>Delivery arithmetic copied from the original service: cool-down, retry back-off, attempt cap.</summary>
public static class DeliveryRules
{
    public const long DefaultCooldownMs = 15 * 60_000;
    public const long CriticalCooldownMs = 5 * 60_000;
    public const int MaxAttempts = 5;

    /// <summary>Delay after the n-th failed attempt, before jitter.</summary>
    public static readonly IReadOnlyList<long> BackoffMs = [30_000, 120_000, 600_000, 1_800_000, 7_200_000];

    public static long CooldownFor(long? cooldownMs, string severity) =>
        cooldownMs ?? (severity == "critical" ? CriticalCooldownMs : DefaultCooldownMs);

    public static string KeyFor(string? dedupeKey, string type, string title) => string.IsNullOrEmpty(dedupeKey) ? $"{type}:{title}" : dedupeKey;

    /// <summary>Suppressed when the same channel|key was sent less than <paramref name="cooldownMs"/> ago.</summary>
    public static bool IsSuppressed(long? lastSent, long now, long cooldownMs) => lastSent is not null && now - lastSent.Value < cooldownMs;

    public static bool Matches(bool enabled, string minSeverity, IReadOnlyCollection<string> events, string type, string severity) =>
        enabled && NotificationCatalog.Rank(minSeverity) <= NotificationCatalog.Rank(severity) && (events.Contains("*") || events.Contains(type));

    /// <param name="random01">Uniform [0,1); the jitter is ±20 %.</param>
    public static long Backoff(int attempts, double random01)
    {
        var idx = Math.Min(attempts, BackoffMs.Count) - 1;
        var b = BackoffMs[Math.Max(0, idx)];
        return (long)Math.Round(b * (0.8 + (random01 * 0.4)), MidpointRounding.AwayFromZero);
    }

    /// <summary>Status after a failed attempt: dead once the cap is reached or the channel is gone, else failed with a retry time.</summary>
    public static (string Status, long? NextAttemptAt) AfterFailure(int attempts, bool channelMissing, long now, double random01) =>
        channelMissing || attempts >= MaxAttempts ? ("dead", null) : ("failed", now + Backoff(attempts, random01));
}

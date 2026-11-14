using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Notifications;
using Quaymaster.Domain.Notifications;
using Quaymaster.Domain.Redaction;
using Quaymaster.UnitTests.Fakes;
using Xunit;

namespace Quaymaster.UnitTests.Notifications;

internal sealed class InMemoryNotificationStore : INotificationStore
{
    private readonly Lock _gate = new();
    private long _nextDelivery = 1;

    public Dictionary<string, ChannelRow> Channels { get; } = [];

    public Dictionary<string, string> Configs { get; } = [];

    public List<DeliveryRow> Deliveries { get; } = [];

    public Dictionary<string, (long Last, int Suppressed)> Dedupe { get; } = [];

    public Task<IReadOnlyList<ChannelRow>> ListChannelsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ChannelRow>>([.. Channels.Values.OrderBy(c => c.Name, StringComparer.Ordinal)]);
        }
    }

    public Task<ChannelRow?> GetChannelAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Channels.GetValueOrDefault(id));
        }
    }

    public Task<bool> ChannelNameExistsAsync(string name, string? exceptId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Channels.Values.Any(c => c.Name == name && c.Id != exceptId));
        }
    }

    public Task InsertChannelAsync(ChannelRow row, string configJson, CancellationToken ct)
    {
        lock (_gate)
        {
            Channels[row.Id] = row;
            Configs[row.SecretName] = configJson;
        }

        return Task.CompletedTask;
    }

    public Task UpdateChannelAsync(ChannelRow row, string? configJson, CancellationToken ct)
    {
        lock (_gate)
        {
            Channels[row.Id] = row;
            if (configJson is not null)
            {
                Configs[row.SecretName] = configJson;
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteChannelAsync(ChannelRow row, CancellationToken ct)
    {
        lock (_gate)
        {
            Channels.Remove(row.Id);
            Configs.Remove(row.SecretName);
            foreach (var k in Dedupe.Keys.Where(k => k.StartsWith(row.Id + "|", StringComparison.Ordinal)).ToList())
            {
                Dedupe.Remove(k);
            }
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetConfigJsonAsync(string secretName, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Configs.GetValueOrDefault(secretName));
        }
    }

    public Task<long> InsertDeliveryAsync(DeliveryRow row, CancellationToken ct)
    {
        lock (_gate)
        {
            var id = _nextDelivery++;
            Deliveries.Add(row with { Id = id, ChannelName = Channels.GetValueOrDefault(row.ChannelId)?.Name });
            return Task.FromResult(id);
        }
    }

    public Task<DeliveryRow?> GetDeliveryAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Deliveries.FirstOrDefault(d => d.Id == id));
        }
    }

    public Task UpdateDeliveryResultAsync(long id, DeliveryResultUpdate u, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = Deliveries.FindIndex(d => d.Id == id);
            Deliveries[i] = Deliveries[i] with { Status = u.Status, Attempts = u.Attempts, NextAttemptAt = u.NextAttemptAt, LastError = u.LastError, HttpStatus = u.HttpStatus };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeliveryRow>> ListDeliveriesAsync(int limit, string? channelId, long? beforeId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeliveryRow>>([.. Deliveries.Where(d => (channelId is null || d.ChannelId == channelId) && (beforeId is null || d.Id < beforeId)).OrderByDescending(d => d.Id).Take(limit)]);
        }
    }

    public Task<IReadOnlyList<DeliveryRow>> DueRetriesAsync(long now, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeliveryRow>>([.. Deliveries.Where(d => d.Status == "failed" && d.NextAttemptAt is { } n && n <= now).OrderBy(d => d.NextAttemptAt).ThenBy(d => d.Id).Take(limit)]);
        }
    }

    public Task PruneAsync(long olderThan, CancellationToken ct) => Task.CompletedTask;

    public Task<long?> LastSentAsync(string key, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<long?>(Dedupe.TryGetValue(key, out var v) ? v.Last : null);
        }
    }

    public Task MarkSentAsync(string key, long now, CancellationToken ct)
    {
        lock (_gate)
        {
            Dedupe[key] = (now, Dedupe.TryGetValue(key, out var v) ? v.Suppressed : 0);
        }

        return Task.CompletedTask;
    }

    public Task BumpSuppressedAsync(string key, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Dedupe.TryGetValue(key, out var v))
            {
                Dedupe[key] = (v.Last, v.Suppressed + 1);
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> SuppressedCountAsync(string key, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Dedupe.TryGetValue(key, out var v) ? v.Suppressed : 0);
        }
    }
}

internal sealed class ScriptedTransport : INotificationTransport
{
    private readonly Lock _gate = new();

    public List<OutboundHttpRequest> Requests { get; } = [];

    public List<MailMessage> Mails { get; } = [];

    /// <summary>Decides the HTTP status (or throws) per request; default 200.</summary>
    public Func<OutboundHttpRequest, int> Respond { get; set; } = _ => 200;

    public Task<int> SendRequestAsync(OutboundHttpRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            Requests.Add(request);
        }

        return Task.FromResult(Respond(request));
    }

    public Task SendMailAsync(MailMessage mail, CancellationToken ct)
    {
        lock (_gate)
        {
            Mails.Add(mail);
        }

        return Task.CompletedTask;
    }
}

public sealed class NotificationServiceTests
{
    private static readonly Actor Admin = new("admin", "127.0.0.1");

    private readonly InMemoryNotificationStore _store = new();
    private readonly ScriptedTransport _transport = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly Scrubber _scrubber = new();
    private readonly NotificationService _svc;

    public NotificationServiceTests() =>
        _svc = new NotificationService(_store, _transport, _audit, _scrubber, _time, NullLogger<NotificationService>.Instance, () => 0.5);

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private static void ConfigIs(JsonObject actual, string expectedJson) => JsonNode.DeepEquals(actual, JsonNode.Parse(expectedJson)).Should().BeTrue(actual.ToJsonString());

    private async Task<string> AddAsync(string name, string kind = "webhook", string? url = null, string minSeverity = "info", string[]? events = null, bool enabled = true)
    {
        var config = kind switch
        {
            "ntfy" => new JsonObject { ["serverUrl"] = url ?? $"http://127.0.0.1:1/{name}", ["topic"] = "t" },
            "smtp" => new JsonObject { ["host"] = "mail.example.com", ["port"] = 25, ["from"] = "a@b.co", ["to"] = new JsonArray("c@d.co") },
            _ => new JsonObject { ["url"] = url ?? $"http://127.0.0.1:1/{name}" },
        };
        return (await _svc.CreateChannelAsync(new ChannelCreateInput(kind, name, enabled, minSeverity, events, config), Admin, default)).Id;
    }

    private async Task NotifyAsync(string type = "run.failed", string severity = "warning", string title = "T", string message = "M", string? key = "k1", long? cooldown = null, Dictionary<string, object?>? data = null)
    {
        _svc.Notify(new NotificationEvent(type, severity, title, message, key!, cooldown, data));
        await _svc.FlushAsync();
    }

    private string[] Statuses(string channelId) => [.. _store.Deliveries.Where(d => d.ChannelId == channelId).Select(d => d.Status)];

    // ---- routing ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Event_Reaches_Only_Enabled_Channels_That_Allow_The_Type_And_Severity()
    {
        var all = await AddAsync("all");
        var onlyRuns = await AddAsync("runs", events: ["run.failed", "run.succeeded"]);
        var onlyDisk = await AddAsync("disk", events: ["host.disk_low"]);
        var critical = await AddAsync("crit", minSeverity: "critical");
        var warn = await AddAsync("warn", minSeverity: "warning");
        var off = await AddAsync("off", enabled: false);

        await NotifyAsync(severity: "warning");

        Statuses(all).Should().Equal("sent");
        Statuses(onlyRuns).Should().Equal("sent");
        Statuses(warn).Should().Equal("sent");
        Statuses(onlyDisk).Should().BeEmpty();
        Statuses(critical).Should().BeEmpty();
        Statuses(off).Should().BeEmpty();
        _transport.Requests.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("info", "info", true)]
    [InlineData("info", "warning", true)]
    [InlineData("warning", "info", false)]
    [InlineData("warning", "warning", true)]
    [InlineData("critical", "warning", false)]
    [InlineData("critical", "critical", true)]
    public void Severity_Filter_Truth_Table(string min, string actual, bool delivered) =>
        DeliveryRules.Matches(true, min, ["*"], "run.failed", actual).Should().Be(delivered);

    [Fact]
    public void Event_Filter_Wildcard_And_Exact() =>
        (DeliveryRules.Matches(true, "info", ["*"], "x", "info"), DeliveryRules.Matches(true, "info", ["a", "b"], "b", "info"), DeliveryRules.Matches(true, "info", ["a"], "b", "info"))
            .Should().Be((true, true, false));

    // ---- de-dupe / cool-down ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cooldown_State_Machine_Suppresses_Then_Releases_At_The_Boundary()
    {
        var ch = await AddAsync("a");
        await NotifyAsync(cooldown: 60_000);
        await NotifyAsync(cooldown: 60_000);
        Statuses(ch).Should().Equal("sent", "suppressed");
        (await _svc.SuppressedCountAsync(ch, "k1")).Should().Be(1);

        _time.Advance(TimeSpan.FromMilliseconds(59_999));
        await NotifyAsync(cooldown: 60_000);
        Statuses(ch).Should().Equal("sent", "suppressed", "suppressed");
        (await _svc.SuppressedCountAsync(ch, "k1")).Should().Be(2);

        _time.Advance(TimeSpan.FromMilliseconds(1)); // exactly cooldown after the LAST SENT (a suppressed event does not extend the window)
        await NotifyAsync(cooldown: 60_000);
        Statuses(ch).Should().Equal("sent", "suppressed", "suppressed", "sent");
        _transport.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Default_Cooldowns_Are_15_Minutes_And_5_For_Critical()
    {
        var ch = await AddAsync("a");
        await NotifyAsync(severity: "warning", key: "w");
        await NotifyAsync(severity: "critical", key: "c");
        _time.Advance(TimeSpan.FromMinutes(5));
        await NotifyAsync(severity: "warning", key: "w");
        await NotifyAsync(severity: "critical", key: "c");
        _store.Deliveries.Where(d => d.DedupeKey == "w").Select(d => d.Status).Should().Equal("sent", "suppressed");
        _store.Deliveries.Where(d => d.DedupeKey == "c").Select(d => d.Status).Should().Equal("sent", "sent");
        _time.Advance(TimeSpan.FromMinutes(10));
        await NotifyAsync(severity: "warning", key: "w");
        _store.Deliveries.Where(d => d.DedupeKey == "w").Select(d => d.Status).Should().Equal("sent", "suppressed", "sent");
        ch.Should().NotBeNull();
    }

    [Fact]
    public async Task Explicit_Cooldown_Wins_And_Keys_And_Channels_Are_Independent()
    {
        var a = await AddAsync("a");
        var b = await AddAsync("b", events: ["run.failed"]);
        await NotifyAsync(key: "k1", cooldown: 1000);
        await NotifyAsync(key: "k2", cooldown: 1000);
        Statuses(a).Should().Equal("sent", "sent");
        await NotifyAsync(key: "k1", cooldown: 1000);
        Statuses(a).Should().Equal("sent", "sent", "suppressed");
        Statuses(b).Should().Equal("sent", "sent", "suppressed");
        _store.Dedupe.Keys.Should().BeEquivalentTo($"{a}|k1", $"{a}|k2", $"{b}|k1", $"{b}|k2");
    }

    [Fact]
    public async Task Missing_Dedupe_Key_Defaults_To_Type_And_Title()
    {
        var ch = await AddAsync("a");
        await NotifyAsync(key: null, title: "Same");
        await NotifyAsync(key: null, title: "Same");
        await NotifyAsync(key: null, title: "Other");
        Statuses(ch).Should().Equal("sent", "suppressed", "sent");
        _store.Dedupe.Keys.Should().Contain($"{ch}|run.failed:Same");
    }

    [Fact]
    public async Task A_Channel_Without_Matching_Events_Does_Not_Consume_The_Window()
    {
        var ch = await AddAsync("a", minSeverity: "critical");
        await NotifyAsync(severity: "warning");
        _store.Dedupe.Should().BeEmpty();
        await NotifyAsync(severity: "critical");
        Statuses(ch).Should().Equal("sent");
    }

    // ---- retries ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Backoff_Schedule_And_Jitter_Bounds()
    {
        DeliveryRules.BackoffMs.Should().Equal(30_000, 120_000, 600_000, 1_800_000, 7_200_000);
        DeliveryRules.MaxAttempts.Should().Be(5);
        DeliveryRules.Backoff(1, 0.5).Should().Be(30_000);
        DeliveryRules.Backoff(1, 0).Should().Be(24_000);
        DeliveryRules.Backoff(1, 0.999999).Should().BeInRange(35_999, 36_000);
        DeliveryRules.Backoff(2, 0.5).Should().Be(120_000);
        DeliveryRules.Backoff(3, 0.5).Should().Be(600_000);
        DeliveryRules.Backoff(4, 0.5).Should().Be(1_800_000);
        DeliveryRules.Backoff(5, 0.5).Should().Be(7_200_000);
        DeliveryRules.Backoff(9, 0.5).Should().Be(7_200_000);
        DeliveryRules.AfterFailure(4, false, 1000, 0.5).Should().Be(("failed", 1000 + 1_800_000));
        DeliveryRules.AfterFailure(5, false, 1000, 0.5).Should().Be(("dead", null));
        DeliveryRules.AfterFailure(1, true, 1000, 0.5).Should().Be(("dead", null));
    }

    [Fact]
    public async Task Server_Error_Is_Retried_With_Backoff_Until_It_Succeeds()
    {
        var ch = await AddAsync("a");
        var calls = 0;
        _transport.Respond = _ => ++calls < 3 ? 500 : 204;
        var t0 = Now;
        await NotifyAsync();
        var d = _store.Deliveries.Single();
        (d.Status, d.Attempts, d.HttpStatus, d.LastError, d.NextAttemptAt).Should().Be(("failed", 1, 500, "HTTP 500", t0 + 30_000));

        (await _svc.ProcessRetriesAsync(default)).Should().Be(0); // not due yet
        _time.Advance(TimeSpan.FromSeconds(30));
        (await _svc.ProcessRetriesAsync(default)).Should().Be(1);
        d = _store.Deliveries.Single();
        (d.Status, d.Attempts, d.NextAttemptAt).Should().Be(("failed", 2, Now + 120_000));

        _time.Advance(TimeSpan.FromSeconds(119));
        (await _svc.ProcessRetriesAsync(default)).Should().Be(0);
        _time.Advance(TimeSpan.FromSeconds(1));
        (await _svc.ProcessRetriesAsync(default)).Should().Be(1);
        d = _store.Deliveries.Single();
        (d.Status, d.Attempts, d.HttpStatus, d.LastError, d.NextAttemptAt).Should().Be(("sent", 3, 204, null, null));
        calls.Should().Be(3);
        ch.Should().NotBeNull();
    }

    [Fact]
    public async Task After_Five_Failures_The_Delivery_Is_Dead_And_Raises_A_Critical_Self_Event_Once()
    {
        var failing = await AddAsync("failing", url: "http://127.0.0.1:1/dead");
        var other = await AddAsync("other", url: "http://127.0.0.1:1/ok");
        _transport.Respond = r => r.Url.EndsWith("/dead", StringComparison.Ordinal) ? 503 : 200;
        await NotifyAsync(title: "Update failed", severity: "critical", key: "run:r1");
        var expected = new long[] { 30_000, 120_000, 600_000, 1_800_000 };
        foreach (var wait in expected)
        {
            _time.Advance(TimeSpan.FromMilliseconds(wait));
            (await _svc.ProcessRetriesAsync(default)).Should().Be(1);
            await _svc.FlushAsync();
        }

        var dead = _store.Deliveries.Single(d => d.ChannelId == failing && d.EventType == "run.failed");
        (dead.Status, dead.Attempts, dead.NextAttemptAt, dead.LastError).Should().Be(("dead", 5, null, "HTTP 503"));
        _time.Advance(TimeSpan.FromDays(1));
        (await _svc.ProcessRetriesAsync(default)).Should().Be(0); // dead is final

        // self-monitor: exactly one app.error, to the OTHER channel only
        var selfEvents = _store.Deliveries.Where(d => d.EventType == "app.error").ToList();
        selfEvents.Should().ContainSingle();
        selfEvents[0].ChannelId.Should().Be(other);
        selfEvents[0].Severity.Should().Be("critical");
        selfEvents[0].Title.Should().Be("Notification delivery failed");
        selfEvents[0].DedupeKey.Should().Be($"delivery-dead:{failing}");
        JsonNode.Parse(selfEvents[0].EventJson)!["message"]!.GetValue<string>().Should().Contain("\"Update failed\"").And.Contain("\"failing\"").And.Contain("5 attempt(s)").And.Contain("HTTP 503");
    }

    [Fact]
    public async Task A_Failing_Self_Event_Never_Loops()
    {
        await AddAsync("a");
        await AddAsync("b");
        _transport.Respond = _ => 500; // everything fails
        await NotifyAsync(severity: "critical", key: "k");
        for (var i = 0; i < 12; i++)
        {
            _time.Advance(TimeSpan.FromHours(2));
            await _svc.ProcessRetriesAsync(default);
            await _svc.FlushAsync();
        }

        // original event: dead on both channels -> one app.error per failing channel to the other channel; those fail too and die silently
        _store.Deliveries.Where(d => d.EventType == "run.failed").Should().OnlyContain(d => d.Status == "dead");
        _store.Deliveries.Where(d => d.EventType == "app.error").Should().HaveCount(2).And.OnlyContain(d => d.Status == "dead");
        _store.Deliveries.Count.Should().Be(4);
    }

    [Fact]
    public async Task Test_Deliveries_Are_Never_Retried_And_Do_Not_Raise_A_Self_Event()
    {
        var ch = await AddAsync("a");
        await AddAsync("b");
        _transport.Respond = _ => 500;
        var r = await _svc.TestChannelAsync(ch, Admin, default);
        r.Should().Be(new ChannelTestResultDto(false, 500, "HTTP 500"));
        var d = _store.Deliveries.Single();
        (d.EventType, d.Status, d.Attempts, d.NextAttemptAt, d.DedupeKey).Should().Be(("test", "failed", 1, null, null));
        _time.Advance(TimeSpan.FromDays(1));
        (await _svc.ProcessRetriesAsync(default)).Should().Be(0);
        await _svc.FlushAsync();
        _store.Deliveries.Should().ContainSingle();
    }

    [Fact]
    public async Task Deleted_Channel_Makes_Pending_Retries_Dead()
    {
        var ch = await AddAsync("a");
        _transport.Respond = _ => 500;
        await NotifyAsync();
        await _svc.DeleteChannelAsync(ch, Admin, default);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _svc.ProcessRetriesAsync(default);
        var d = _store.Deliveries.Single();
        (d.Status, d.LastError).Should().Be(("dead", "channel was deleted"));
    }

    [Fact]
    public async Task Transport_Exception_Becomes_A_Failed_Delivery_Without_Http_Status()
    {
        await AddAsync("a");
        _transport.Respond = _ => throw new InvalidOperationException("Request to http://127.0.0.1:1/••••••  failed: ECONNREFUSED");
        await NotifyAsync();
        var d = _store.Deliveries.Single();
        (d.Status, d.HttpStatus, d.LastError).Should().Be(("failed", null, "Request to http://127.0.0.1:1/•••••• failed: ECONNREFUSED"));
    }

    [Fact]
    public async Task Redirect_Status_Counts_As_Failure_Only_Outside_2xx()
    {
        await AddAsync("a");
        _transport.Respond = _ => 302;
        await NotifyAsync();
        _store.Deliveries.Single().LastError.Should().Be("HTTP 302");
    }

    // ---- content ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Event_Text_Is_Redacted_And_Channel_Secrets_Are_Scrubbed_Before_Storage_And_Sending()
    {
        var url = "https://discord.example/api/webhooks/123456789/SuperSecretWebhookToken123";
        await AddAsync("d", kind: "ntfy", url: "http://127.0.0.1:1/x");
        var hook = await _svc.CreateChannelAsync(new ChannelCreateInput("discord", "hook", true, "info", null, new JsonObject { ["url"] = url }), Admin, default);
        await NotifyAsync(message: $"posting to {url} with password=hunter2hunter2 failed", title: "SuperSecretWebhookToken123 title", data: new() { ["token"] = "abcdefghijklmnopqrstuvwxyz", ["n"] = 5 });
        var stored = string.Join('\n', _store.Deliveries.Select(d => d.EventJson + d.Title));
        stored.Should().NotContain("SuperSecretWebhookToken123").And.NotContain("hunter2hunter2").And.NotContain("abcdefghijklmnopqrstuvwxyz");
        _transport.Requests.Select(r => r.Body).Should().OnlyContain(b => !b!.Contains("SuperSecretWebhookToken123") && !b.Contains("hunter2hunter2"));
        hook.Id.Should().NotBeNull();
    }

    [Fact]
    public async Task Data_Types_Survive_The_Retry_Round_Trip()
    {
        await AddAsync("a");
        _transport.Respond = r => r.Body!.Contains("\"attempts\"") ? 200 : 500;
        await NotifyAsync(data: new() { ["attempts"] = 2, ["ratio"] = 0.5, ["ok"] = true, ["name"] = "x", ["none"] = null });
        var body = JsonNode.Parse(_transport.Requests.Single().Body!)!;
        body["data"]!.ToJsonString().Should().Be("""{"attempts":2,"ratio":0.5,"ok":true,"name":"x","none":null}""");
    }

    // ---- channel management ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Info_Masks_Secrets_And_Url_Like_Fields()
    {
        var created = await _svc.CreateChannelAsync(
            new ChannelCreateInput("webhook", "w", null, null, null, new JsonObject { ["url"] = "https://hooks.example.com/a/secret", ["secret"] = "hmac-key", ["headers"] = new JsonObject { ["X-Token"] = "t" } }),
            Admin,
            default);
        ConfigIs(created.Config, """{"url":"https://hooks.example.com/••••••","secret":"••••••","headers":"••••••"}""");
        (created.Enabled, created.MinSeverity).Should().Be((true, "info")); created.Events.Should().Equal("*");
        var smtp = await _svc.CreateChannelAsync(
            new ChannelCreateInput("smtp", "m", null, null, null, new JsonObject { ["host"] = "mail.example.com", ["port"] = 587, ["username"] = "u", ["password"] = "pw", ["from"] = "a@b.co", ["to"] = new JsonArray("c@d.co") }),
            Admin,
            default);
        ConfigIs(smtp.Config, """{"host":"mail.example.com","port":587,"secure":false,"username":"u","password":"••••••","from":"a@b.co","to":["c@d.co"]}""");
        var tg = await _svc.CreateChannelAsync(new ChannelCreateInput("telegram", "t", null, null, null, new JsonObject { ["botToken"] = "123:ABC", ["chatId"] = "42" }), Admin, default);
        ConfigIs(tg.Config, """{"botToken":"••••••","chatId":"42"}""");
        System.Text.Json.JsonSerializer.Serialize(tg).Should().NotContain("123:ABC");
    }

    [Fact]
    public async Task Patch_Merges_Config_Keeps_Secrets_On_Masked_Echo_And_Null_Removes_Optional_Keys()
    {
        var created = await _svc.CreateChannelAsync(
            new ChannelCreateInput("webhook", "w", null, null, null, new JsonObject { ["url"] = "https://h.example/x", ["secret"] = "hmac-key" }),
            Admin,
            default);
        var secretName = $"notify:{created.Id}";

        await _svc.UpdateChannelAsync(created.Id, new ChannelPatchInput(null, null, null, null, new JsonObject { ["secret"] = "••••••" }), Admin, default);
        JsonNode.Parse(_store.Configs[secretName])!["secret"]!.GetValue<string>().Should().Be("hmac-key");

        await _svc.UpdateChannelAsync(created.Id, new ChannelPatchInput(null, null, null, null, new JsonObject { ["url"] = "https://h.example/y" }), Admin, default);
        _store.Configs[secretName].Should().Be("""{"url":"https://h.example/y","secret":"hmac-key"}""");

        await _svc.UpdateChannelAsync(created.Id, new ChannelPatchInput("renamed", false, "critical", ["run.failed"], new JsonObject { ["secret"] = null }), Admin, default);
        _store.Configs[secretName].Should().Be("""{"url":"https://h.example/y"}""");
        var row = _store.Channels[created.Id];
        (row.Name, row.Enabled, row.MinSeverity).Should().Be(("renamed", false, "critical")); row.Events.Should().Equal("run.failed");

        var act = () => _svc.UpdateChannelAsync(created.Id, new ChannelPatchInput(null, null, null, null, new JsonObject { ["url"] = "ftp://nope" }), Admin, default);
        var e = (await act.Should().ThrowAsync<AppException>()).Which;
        (e.Status, e.Code, e.Message).Should().Be((400, ErrorCode.Validation, "Invalid webhook configuration"));
        ((IEnumerable<ValidationIssue>)e.Details!).Should().ContainSingle().Which.Should().Be(new ValidationIssue("url", "Only http and https URLs are allowed"));
        _store.Configs[secretName].Should().Be("""{"url":"https://h.example/y"}"""); // failed patch changed nothing
    }

    [Fact]
    public async Task Name_Conflict_Unknown_Event_And_Missing_Channel_Errors()
    {
        await AddAsync("dup");
        var dup = () => AddAsync("dup");
        (await dup.Should().ThrowAsync<AppException>()).Which.Status.Should().Be(409);
        var badEvent = () => _svc.CreateChannelAsync(new ChannelCreateInput("webhook", "x", null, null, ["nope"], new JsonObject { ["url"] = "https://h/x" }), Admin, default);
        (await badEvent.Should().ThrowAsync<AppException>()).Which.Message.Should().Be("Unknown event type in events");
        var blank = () => _svc.CreateChannelAsync(new ChannelCreateInput("webhook", "   ", null, null, null, new JsonObject { ["url"] = "https://h/x" }), Admin, default);
        (await blank.Should().ThrowAsync<AppException>()).Which.Message.Should().Be("Channel name must be 1-80 characters");
        var missing = () => _svc.GetChannelAsync("ch_nope", default);
        (await missing.Should().ThrowAsync<AppException>()).Which.Status.Should().Be(404);
    }

    [Fact]
    public async Task Channel_Changes_Are_Audited_Without_Secrets()
    {
        var c = await _svc.CreateChannelAsync(new ChannelCreateInput("webhook", "w", null, null, null, new JsonObject { ["url"] = "https://h.example/very-secret-path", ["secret"] = "very-secret-hmac" }), Admin, default);
        await _svc.UpdateChannelAsync(c.Id, new ChannelPatchInput(null, false, null, null, new JsonObject { ["secret"] = "another-secret" }), Admin, default);
        await _svc.TestChannelAsync(c.Id, Admin, default);
        await _svc.DeleteChannelAsync(c.Id, Admin, default);
        _audit.Records.Select(r => r.Action).Should().Equal("notifications.channel.create", "notifications.channel.update", "notifications.channel.test", "notifications.channel.delete");
        _audit.Records.Should().OnlyContain(r => r.Target == c.Id && r.Ip == "127.0.0.1" && r.Actor == "admin");
        System.Text.Json.JsonSerializer.Serialize(_audit.Records).Should().NotContain("very-secret").And.NotContain("another-secret");
    }

    [Fact]
    public async Task Deliveries_Listing_Filters_Pages_And_Orders_Newest_First()
    {
        var a = await AddAsync("a");
        var b = await AddAsync("b", events: ["run.failed"]);
        await NotifyAsync(key: "k1");
        await NotifyAsync(key: "k2");
        var all = await _svc.ListDeliveriesAsync(50, null, null, default);
        all.Select(d => d.Id).Should().BeInDescendingOrder();
        all.Should().HaveCount(4);
        (await _svc.ListDeliveriesAsync(50, a, null, default)).Should().OnlyContain(d => d.ChannelId == a);
        (await _svc.ListDeliveriesAsync(2, null, null, default)).Should().HaveCount(2);
        (await _svc.ListDeliveriesAsync(50, null, all[1].Id, default)).Select(d => d.Id).Should().Equal(all.Skip(2).Select(d => d.Id));
        var first = all[^1];
        (first.Status, first.Attempts, first.Title, first.EventType, first.Severity, first.NextAttemptAt, first.LastError, first.HttpStatus, first.Time).Should()
            .Be(("sent", 1, "T", "run.failed", "warning", null, null, 200, "2026-11-14T12:00:00.000Z"));
        b.Should().NotBeNull();
    }
}

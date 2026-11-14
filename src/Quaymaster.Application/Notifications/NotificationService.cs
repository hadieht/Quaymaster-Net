using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Monitoring;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Notifications;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.Application.Notifications;

public interface INotificationService
{
    IReadOnlyList<ChannelTypeInfoDto> Types();

    Task<IReadOnlyList<ChannelInfoDto>> ListChannelsAsync(CancellationToken ct);

    Task<ChannelInfoDto> GetChannelAsync(string id, CancellationToken ct);

    Task<ChannelInfoDto> CreateChannelAsync(ChannelCreateInput input, Actor actor, CancellationToken ct);

    Task<ChannelInfoDto> UpdateChannelAsync(string id, ChannelPatchInput patch, Actor actor, CancellationToken ct);

    Task DeleteChannelAsync(string id, Actor actor, CancellationToken ct);

    Task<ChannelTestResultDto> TestChannelAsync(string id, Actor actor, CancellationToken ct);

    Task<IReadOnlyList<DeliveryDto>> ListDeliveriesAsync(int limit, string? channelId, long? beforeId, CancellationToken ct);

    /// <summary>Retries every failed delivery that is due. Returns the number of attempts made.</summary>
    Task<int> ProcessRetriesAsync(CancellationToken ct);

    /// <summary>Loads channel secrets into the log scrubber (start-up).</summary>
    Task PrimeScrubberAsync(CancellationToken ct);

    Task PruneAsync(CancellationToken ct);

    /// <summary>Completes when all fire-and-forget dispatches started so far have finished (tests, shutdown).</summary>
    Task FlushAsync();
}

/// <summary>
/// Port of <c>notifications/service.ts</c>: event → matching channels (enabled, event allowed, severity) → de-dupe /
/// cool-down (persisted in <c>notification_dedupe</c>) → render → send → delivery log, with retries and back-off for failed
/// deliveries. Also the <see cref="INotificationSink"/> the monitors and the maintenance engine emit into: emitting never
/// throws and a delivery failure never reaches the producer. A delivery that ends <c>dead</c> raises ONE critical
/// <c>app.error</c> event to the OTHER channels; <c>app.error</c> / <c>test</c> deliveries never raise it (no loops).
/// </summary>
public sealed partial class NotificationService : INotificationService, INotificationSink
{
    private static readonly Regex Space = SpaceRe();

    private readonly INotificationStore _store;
    private readonly INotificationTransport _transport;
    private readonly IAuditLog _audit;
    private readonly Scrubber _scrubber;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationService> _log;
    private readonly Func<double> _random;
    private readonly ConcurrentDictionary<Task, byte> _inflight = new();
    private readonly ConcurrentDictionary<long, byte> _busy = new();
    private readonly SemaphoreSlim _routing = new(1, 1);

    public NotificationService(
        INotificationStore store,
        INotificationTransport transport,
        IAuditLog audit,
        Scrubber scrubber,
        TimeProvider time,
        ILogger<NotificationService> log,
        Func<double>? random = null)
    {
        _store = store;
        _transport = transport;
        _audit = audit;
        _scrubber = scrubber;
        _time = time;
        _log = log;
        _random = random ?? Random.Shared.NextDouble;
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------- catalogue

    public IReadOnlyList<ChannelTypeInfoDto> Types() =>
        [.. Channels.All.Select(k => new ChannelTypeInfoDto(k.Type, k.Label, [.. k.Fields.Select(f => new ChannelFieldDto(f.Name, f.Label, f.Kind, f.Required, f.Secret, f.Help))]))];

    // ---------------------------------------------------------------- channels

    public async Task<IReadOnlyList<ChannelInfoDto>> ListChannelsAsync(CancellationToken ct)
    {
        var rows = await _store.ListChannelsAsync(ct);
        var result = new List<ChannelInfoDto>(rows.Count);
        foreach (var r in rows)
        {
            result.Add(await ToInfoAsync(r, ct));
        }

        return result;
    }

    public async Task<ChannelInfoDto> GetChannelAsync(string id, CancellationToken ct) => await ToInfoAsync(await MustGetAsync(id, ct), ct);

    public async Task<ChannelInfoDto> CreateChannelAsync(ChannelCreateInput input, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(actor);
        var kind = Channels.Get(input.Type) ?? throw AppException.BadRequest($"Unknown channel type \"{input.Type}\"");
        var name = CheckName(input.Name);
        var minSeverity = CheckSeverity(input.MinSeverity ?? "info");
        var events = CheckEvents(input.Events ?? ["*"]);
        if (await _store.ChannelNameExistsAsync(name, null, ct))
        {
            throw AppException.Conflict($"A channel named \"{name}\" already exists");
        }

        var config = ParseConfig(kind, input.Config);
        var id = $"ch_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
        var now = Now;
        var row = new ChannelRow(id, kind.Type, name, input.Enabled ?? true, minSeverity, events, $"notify:{id}", now, now);
        await _store.InsertChannelAsync(row, config.ToJsonString(), ct);
        FeedScrubber(config);
        await _audit.RecordAsync(new AuditRecord(actor.Name, "notifications.channel.create", AuditOutcome.Success, actor.Ip, id, new { type = row.Type, name, enabled = row.Enabled }), ct);
        return await ToInfoAsync(row, ct);
    }

    public async Task<ChannelInfoDto> UpdateChannelAsync(string id, ChannelPatchInput patch, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(actor);
        var row = await MustGetAsync(id, ct);
        var kind = Channels.Get(row.Type) ?? throw AppException.BadRequest($"Unknown channel type \"{row.Type}\"");
        var next = row with { UpdatedAt = Now };
        if (patch.Name is not null)
        {
            next = next with { Name = CheckName(patch.Name) };
            if (await _store.ChannelNameExistsAsync(next.Name, id, ct))
            {
                throw AppException.Conflict($"A channel named \"{next.Name}\" already exists");
            }
        }

        if (patch.Enabled is not null)
        {
            next = next with { Enabled = patch.Enabled.Value };
        }

        if (patch.MinSeverity is not null)
        {
            next = next with { MinSeverity = CheckSeverity(patch.MinSeverity) };
        }

        if (patch.Events is not null)
        {
            next = next with { Events = CheckEvents(patch.Events) };
        }

        string? configJson = null;
        if (patch.Config is not null)
        {
            var merged = await LoadConfigAsync(row, ct);
            var secretFields = kind.Fields.Where(f => f.Secret).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var (k, v) in patch.Config)
            {
                if (v is null)
                {
                    merged.Remove(k);
                }
                else if (secretFields.Contains(k) && v is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Contains(Redactor.Mask, StringComparison.Ordinal))
                {
                    // A masked placeholder echoed back by a UI means "keep the stored value".
                    continue;
                }
                else
                {
                    merged[k] = JsonNode.Parse(v.ToJsonString());
                }
            }

            var config = ParseConfig(kind, merged);
            configJson = config.ToJsonString();
            FeedScrubber(config);
        }

        await _store.UpdateChannelAsync(next, configJson, ct);
        await _audit.RecordAsync(new AuditRecord(actor.Name, "notifications.channel.update", AuditOutcome.Success, actor.Ip, id, new { name = next.Name, enabled = next.Enabled, configChanged = patch.Config is not null }), ct);
        return await ToInfoAsync(next, ct);
    }

    public async Task DeleteChannelAsync(string id, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var row = await MustGetAsync(id, ct);
        await _store.DeleteChannelAsync(row, ct);
        await _audit.RecordAsync(new AuditRecord(actor.Name, "notifications.channel.delete", AuditOutcome.Success, actor.Ip, id, new { type = row.Type, name = row.Name }), ct);
    }

    public async Task<ChannelTestResultDto> TestChannelAsync(string id, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var row = await MustGetAsync(id, ct);
        var now = Now;
        var ev = Sanitize(new OutboundEvent("test", "info", "Quaymaster test notification", $"This is a test notification for channel \"{row.Name}\".", now));
        var deliveryId = await _store.InsertDeliveryAsync(
            new DeliveryRow(0, now, id, null, "test", "info", null, ev.Title, "pending", 0, null, null, null, ev.ToJson().ToJsonString()),
            ct);
        var res = await DeliverAsync(row, ev, ct);
        await _store.UpdateDeliveryResultAsync(deliveryId, new DeliveryResultUpdate(res.Ok ? "sent" : "failed", 1, null, res.Error, res.HttpStatus), ct); // tests are never retried
        await _audit.RecordAsync(new AuditRecord(actor.Name, "notifications.channel.test", res.Ok ? AuditOutcome.Success : AuditOutcome.Failure, actor.Ip, id, new { type = row.Type, ok = res.Ok, httpStatus = res.HttpStatus }), ct);
        return new ChannelTestResultDto(res.Ok, res.HttpStatus, res.Error);
    }

    public async Task<IReadOnlyList<DeliveryDto>> ListDeliveriesAsync(int limit, string? channelId, long? beforeId, CancellationToken ct) =>
        [.. (await _store.ListDeliveriesAsync(Math.Clamp(limit, 1, 500), channelId, beforeId, ct)).Select(d => new DeliveryDto(
            d.Id,
            Channels.Iso(d.Ts),
            d.ChannelId,
            d.ChannelName,
            d.EventType,
            d.Severity,
            d.Title,
            d.Status,
            d.Attempts,
            d.NextAttemptAt is { } n ? Channels.Iso(n) : null,
            d.LastError,
            d.HttpStatus))];

    /// <summary>Number of events suppressed so far for a channel + dedupe key (diagnostics, tests).</summary>
    public Task<int> SuppressedCountAsync(string channelId, string key, CancellationToken ct = default) => _store.SuppressedCountAsync($"{channelId}|{key}", ct);

    public Task PruneAsync(CancellationToken ct) => _store.PruneAsync(Now - (90L * 24 * 3600 * 1000), ct);

    // ---------------------------------------------------------------- emitting

    /// <summary>Fire-and-forget. Never throws; delivery failures never reach the caller.</summary>
    public void Notify(NotificationEvent notification)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(notification);
            Track(DispatchAsync(FromMonitor(notification), null));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.EmitFailed(_log, e.Message);
        }
    }

    public async Task FlushAsync()
    {
        while (!_inflight.IsEmpty)
        {
            await Task.WhenAll(_inflight.Keys.Select(async t =>
            {
                try
                {
                    await t;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // logged where it happened
                }
            }));
        }
    }

    private void Track(Task work)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await work;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.DispatchFailed(_log, ErrText(e));
            }
        });
        _inflight[task] = 0;
        _ = task.ContinueWith(t => _inflight.TryRemove(t, out _), TaskScheduler.Default);
    }

    private static OutboundEvent FromMonitor(NotificationEvent n)
    {
        JsonObject? data = null;
        if (n.Data is { Count: > 0 })
        {
            data = [];
            foreach (var (k, v) in n.Data)
            {
                data[k] = ToNode(v);
            }
        }

        return new OutboundEvent(n.Type, n.Severity, n.Title, n.Message, 0, string.IsNullOrEmpty(n.DedupeKey) ? null : n.DedupeKey, n.CooldownMs, data);
    }

    private static JsonNode? ToNode(object? v) => v switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create((double)f),
        decimal m => JsonValue.Create((double)m),
        DateTimeOffset o => JsonValue.Create(o.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
        DateTime t => JsonValue.Create(t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
        JsonNode n => JsonNode.Parse(n.ToJsonString()),
        _ => JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private async Task DispatchAsync(OutboundEvent raw, string? excludeChannelId)
    {
        var now = Now;
        var ev = Sanitize(raw with { Ts = raw.Ts == 0 ? now : raw.Ts });
        var key = DeliveryRules.KeyFor(ev.DedupeKey, ev.Type, ev.Title);
        var cooldown = DeliveryRules.CooldownFor(ev.CooldownMs, ev.Severity);
        var targets = (await _store.ListChannelsAsync(CancellationToken.None))
            .Where(c => c.Id != excludeChannelId && DeliveryRules.Matches(c.Enabled, c.MinSeverity, [.. c.Events], ev.Type, ev.Severity))
            .ToList();
        var sends = new List<Task>();
        var eventJson = ev.ToJson().ToJsonString();
        foreach (var ch in targets)
        {
            await _routing.WaitAsync();
            try
            {
                var dk = $"{ch.Id}|{key}";
                var last = await _store.LastSentAsync(dk, CancellationToken.None);
                DeliveryRow Row(string status) => new(0, now, ch.Id, null, ev.Type, ev.Severity, key, ev.Title, status, 0, null, null, null, eventJson);
                if (DeliveryRules.IsSuppressed(last, now, cooldown))
                {
                    await _store.InsertDeliveryAsync(Row("suppressed"), CancellationToken.None);
                    await _store.BumpSuppressedAsync(dk, CancellationToken.None);
                    continue;
                }

                await _store.MarkSentAsync(dk, now, CancellationToken.None);
                var id = await _store.InsertDeliveryAsync(Row("pending"), CancellationToken.None);
                sends.Add(AttemptSafeAsync(id));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.RoutingFailed(_log, ErrText(e));
            }
            finally
            {
                _routing.Release();
            }
        }

        await Task.WhenAll(sends);
    }

    private async Task AttemptSafeAsync(long id)
    {
        try
        {
            await AttemptAsync(id, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.DeliveryFailed(_log, ErrText(e));
        }
    }

    // ---------------------------------------------------------------- delivery + retries

    public async Task<int> ProcessRetriesAsync(CancellationToken ct)
    {
        var due = await _store.DueRetriesAsync(Now, 50, ct);
        var n = 0;
        foreach (var d in due)
        {
            if (_busy.ContainsKey(d.Id))
            {
                continue;
            }

            await AttemptAsync(d.Id, ct);
            n++;
        }

        return n;
    }

    private async Task AttemptAsync(long deliveryId, CancellationToken ct)
    {
        if (!_busy.TryAdd(deliveryId, 0))
        {
            return;
        }

        try
        {
            var d = await _store.GetDeliveryAsync(deliveryId, ct);
            if (d is null || d.Status is not ("pending" or "failed"))
            {
                return;
            }

            var ch = await _store.GetChannelAsync(d.ChannelId, ct);
            var attempts = d.Attempts + 1;
            SendOutcome res;
            if (ch is null)
            {
                res = new SendOutcome(false, null, "channel was deleted");
            }
            else
            {
                var ev = OutboundEvent.FromJson(d.EventJson) ?? new OutboundEvent("app.error", d.Severity, d.Title, string.Empty, d.Ts);
                res = await DeliverAsync(ch, ev, ct);
            }

            if (res.Ok)
            {
                await _store.UpdateDeliveryResultAsync(deliveryId, new DeliveryResultUpdate("sent", attempts, null, null, res.HttpStatus), ct);
                return;
            }

            var (status, next) = DeliveryRules.AfterFailure(attempts, ch is null, Now, _random());
            await _store.UpdateDeliveryResultAsync(deliveryId, new DeliveryResultUpdate(status, attempts, next, res.Error, res.HttpStatus), ct);
            if (status == "dead" && d.EventType is not ("app.error" or "test"))
            {
                RaiseSelfMonitor(d, ch, attempts, res.Error);
            }
        }
        finally
        {
            _busy.TryRemove(deliveryId, out _);
        }
    }

    private void RaiseSelfMonitor(DeliveryRow d, ChannelRow? ch, int attempts, string? error)
    {
        var name = ch?.Name ?? d.ChannelId;
        var ev = new OutboundEvent(
            "app.error",
            "critical",
            "Notification delivery failed",
            $"Giving up on \"{d.Title}\" for channel \"{name}\" after {attempts} attempt(s): {error ?? "unknown error"}",
            0,
            $"delivery-dead:{d.ChannelId}",
            3_600_000,
            new JsonObject { ["channel"] = name, ["event"] = d.EventType, ["attempts"] = attempts });
        Track(DispatchAsync(ev, d.ChannelId)); // never back into the channel that just failed; app.error never re-raises
    }

    private readonly record struct SendOutcome(bool Ok, int? HttpStatus, string? Error);

    /// <summary>Sends through the channel implementation; never throws; error text is redacted and short.</summary>
    private async Task<SendOutcome> DeliverAsync(ChannelRow ch, OutboundEvent ev, CancellationToken ct)
    {
        try
        {
            var kind = Channels.Get(ch.Type);
            if (kind is null)
            {
                return new SendOutcome(false, null, $"unknown channel type \"{ch.Type}\"");
            }

            var config = ParseConfig(kind, await LoadConfigAsync(ch, ct));
            var msg = kind.Render(config, ev);
            if (msg.Http is not null)
            {
                try
                {
                    var status = await _transport.SendRequestAsync(msg.Http, ct);
                    var ok = status is >= 200 and < 300;
                    return new SendOutcome(ok, status, ok ? null : $"HTTP {status}");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return new SendOutcome(false, null, CleanError(Slice(string.IsNullOrEmpty(e.Message) ? "request failed" : e.Message, 300)));
                }
            }

            try
            {
                await _transport.SendMailAsync(msg.Mail!, ct);
                return new SendOutcome(true, null, null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return new SendOutcome(false, null, CleanError(Slice(string.IsNullOrEmpty(e.Message) ? "mail failed" : e.Message, 300)));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new SendOutcome(false, null, CleanError(ErrText(e)));
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string Slice(string s, int max) => s.Length <= max ? s : s[..max];

    private string CleanError(string s) => Slice(Space.Replace(Redactor.RedactText(_scrubber.Scrub(s)), " "), 300);

    private static string ErrText(Exception e) => e.Message.Length > 0 ? e.Message : "unknown error";

    private OutboundEvent Sanitize(OutboundEvent e)
    {
        string Clean(string s) => Redactor.RedactText(_scrubber.Scrub(s));
        JsonObject? data = null;
        if (e.Data is not null)
        {
            data = (JsonObject)Redactor.RedactDeep(JsonNode.Parse(e.Data.ToJsonString()))!;
            foreach (var k in data.Select(p => p.Key).ToList())
            {
                if (data[k] is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    data[k] = Clean(s);
                }
            }
        }

        var clean = new OutboundEvent(e.Type, e.Severity, Slice(Clean(e.Title), 300), Slice(Clean(e.Message), 4000), e.Ts, e.DedupeKey, e.CooldownMs, data);

        // Final pass over the serialised form so nothing the walkers missed gets persisted.
        return OutboundEvent.FromJson(_scrubber.Scrub(clean.ToJson().ToJsonString())) ?? clean;
    }

    private async Task<ChannelRow> MustGetAsync(string id, CancellationToken ct) =>
        await _store.GetChannelAsync(id, ct) ?? throw AppException.NotFound("Notification channel");

    private async Task<JsonObject> LoadConfigAsync(ChannelRow row, CancellationToken ct)
    {
        var raw = await _store.GetConfigJsonAsync(row.SecretName, ct) ?? throw new InvalidOperationException($"channel {row.Id} has no stored configuration");
        return JsonNode.Parse(raw) as JsonObject ?? throw new InvalidOperationException($"channel {row.Id} has an invalid stored configuration");
    }

    private static JsonObject ParseConfig(IChannelKind kind, JsonNode? raw)
    {
        var r = kind.Parse(raw);
        if (r.Config is null)
        {
            throw AppException.BadRequest($"Invalid {kind.Type} configuration", r.Issues.Select(i => new ValidationIssue(i.Path, i.Message)).ToList());
        }

        return r.Config;
    }

    public async Task PrimeScrubberAsync(CancellationToken ct)
    {
        try
        {
            foreach (var c in await _store.ListChannelsAsync(ct))
            {
                FeedScrubber(await LoadConfigAsync(c, ct));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.ScrubberPrime(_log, ErrText(e));
        }
    }

    private void FeedScrubber(JsonObject config)
    {
        foreach (var (_, v) in config)
        {
            if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
            {
                _scrubber.Add(s);
                if (Uri.TryCreate(s, UriKind.Absolute, out var u))
                {
                    if (u.AbsolutePath.Length > 1)
                    {
                        _scrubber.Add(u.AbsolutePath);
                    }

                    foreach (var seg in u.AbsolutePath.Split('/'))
                    {
                        if (seg.Length >= 8)
                        {
                            _scrubber.Add(seg);
                        }
                    }

                    if (u.Query.Length > 1)
                    {
                        _scrubber.Add(u.Query[1..]);
                    }
                }
            }
            else if (v is JsonObject o)
            {
                foreach (var (_, h) in o)
                {
                    if (h is JsonValue hv && hv.TryGetValue<string>(out var hs))
                    {
                        _scrubber.Add(hs);
                    }
                }
            }
        }
    }

    private async Task<ChannelInfoDto> ToInfoAsync(ChannelRow row, CancellationToken ct)
    {
        var kind = Channels.Get(row.Type);
        var config = new JsonObject();
        try
        {
            var stored = await LoadConfigAsync(row, ct);
            var specs = (kind?.Fields ?? []).ToDictionary(f => f.Name, StringComparer.Ordinal);
            foreach (var (k, v) in stored)
            {
                specs.TryGetValue(k, out var spec);
                if (spec is null || spec.Secret)
                {
                    // Unknown keys are treated as secret; URL-like secrets show scheme://host/••••
                    var urlLike = v is JsonValue jv && jv.TryGetValue<string>(out var us) && (spec?.Kind == "url" || UrlLike().IsMatch(us));
                    config[k] = urlLike ? OutboundPolicy.MaskToSchemeHost(v!.GetValue<string>(), Redactor.Mask) : Redactor.Mask;
                }
                else
                {
                    config[k] = v is null ? null : JsonNode.Parse(v.ToJsonString());
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            config = [];
        }

        return new ChannelInfoDto(row.Id, row.Type, row.Name, row.Enabled, row.MinSeverity, row.Events, config, Channels.Iso(row.CreatedAt), Channels.Iso(row.UpdatedAt));
    }

    private static string CheckName(string? name)
    {
        var t = name?.Trim() ?? string.Empty;
        return t.Length is >= 1 and <= 80 ? t : throw AppException.BadRequest("Channel name must be 1-80 characters");
    }

    private static string CheckSeverity(string s) => NotificationCatalog.Severities.Contains(s) ? s : throw AppException.BadRequest("Invalid severity");

    private static List<string> CheckEvents(IReadOnlyList<string> events)
    {
        if (events.Count == 0)
        {
            throw AppException.BadRequest("events must be a non-empty list");
        }

        if (events.Any(e => e != "*" && !NotificationCatalog.EventTypes.Contains(e)))
        {
            throw AppException.BadRequest("Unknown event type in events");
        }

        return [.. events.Distinct(StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRe();

    [GeneratedRegex("^https?://", RegexOptions.IgnoreCase)]
    private static partial Regex UrlLike();

    private static partial class Log
    {
        [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "notification dispatch failed: {Error}")]
        public static partial void DispatchFailed(ILogger logger, string error);

        [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "notification routing failed: {Error}")]
        public static partial void RoutingFailed(ILogger logger, string error);

        [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "delivery failed: {Error}")]
        public static partial void DeliveryFailed(ILogger logger, string error);

        [LoggerMessage(EventId = 7004, Level = LogLevel.Warning, Message = "notification emit failed: {Error}")]
        public static partial void EmitFailed(ILogger logger, string error);

        [LoggerMessage(EventId = 7005, Level = LogLevel.Warning, Message = "notifications: could not load channel secrets: {Error}")]
        public static partial void ScrubberPrime(ILogger logger, string error);
    }
}

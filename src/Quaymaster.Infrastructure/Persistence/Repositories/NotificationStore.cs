using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quaymaster.Application.Notifications;
using Quaymaster.Application.Registry;

namespace Quaymaster.Infrastructure.Persistence.Repositories;

#pragma warning disable CA1812 // row types are instantiated by EF
internal sealed class ChannelSqlRow
{
    public string Id { get; set; } = "";

    public string Type { get; set; } = "";

    public string Name { get; set; } = "";

    public long Enabled { get; set; }

    public string MinSeverity { get; set; } = "info";

    public string Events { get; set; } = "[\"*\"]";

    public string SecretName { get; set; } = "";

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }
}

internal sealed class DeliverySqlRow
{
    public long Id { get; set; }

    public long Ts { get; set; }

    public string ChannelId { get; set; } = "";

    public string? ChannelName { get; set; }

    public string EventType { get; set; } = "";

    public string Severity { get; set; } = "";

    public string? DedupeKey { get; set; }

    public string Title { get; set; } = "";

    public string Status { get; set; } = "";

    public long Attempts { get; set; }

    public long? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public long? HttpStatus { get; set; }

    public string EventJson { get; set; } = "{}";
}

internal sealed class LongRow
{
    public long Value { get; set; }
}

internal sealed class TextRow
{
    public string Value { get; set; } = "";
}
#pragma warning restore CA1812

/// <summary>
/// <c>notification_channels</c>, <c>notification_deliveries</c>, <c>notification_dedupe</c> (Baseline tables, parameterised SQL).
/// The channel configuration (bot tokens, SMTP passwords, webhook URLs / HMAC keys, headers) is ONE JSON document encrypted with
/// AES-GCM in <c>secret_store</c> under <c>notify:&lt;id&gt;</c>; the channel row itself holds no secret.
/// </summary>
internal sealed class NotificationStore(IDbContextFactory<AppDbContext> factory, ISecretBox box) : INotificationStore
{
    private const string ChannelColumns = "id AS Id, type AS Type, name AS Name, enabled AS Enabled, min_severity AS MinSeverity, events AS Events, secret_name AS SecretName, created_at AS CreatedAt, updated_at AS UpdatedAt";

    private const string DeliverySelect = "SELECT d.id AS Id, d.ts AS Ts, d.channel_id AS ChannelId, c.name AS ChannelName, d.event_type AS EventType, d.severity AS Severity, d.dedupe_key AS DedupeKey, d.title AS Title, d.status AS Status, d.attempts AS Attempts, d.next_attempt_at AS NextAttemptAt, d.last_error AS LastError, d.http_status AS HttpStatus, d.event_json AS EventJson FROM notification_deliveries d LEFT JOIN notification_channels c ON c.id = d.channel_id";

    private static string Aad(string name) => $"secret_store:ciphertext:{name}";

    private static ChannelRow Map(ChannelSqlRow r)
    {
        IReadOnlyList<string> events = ["*"];
        try
        {
            if (JsonSerializer.Deserialize<List<JsonElement>>(r.Events) is { } list)
            {
                events = [.. list.Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)];
            }
        }
        catch (JsonException)
        {
            // keep the default
        }

        return new ChannelRow(r.Id, r.Type, r.Name, r.Enabled == 1, r.MinSeverity, events, r.SecretName, r.CreatedAt, r.UpdatedAt);
    }

    private static DeliveryRow Map(DeliverySqlRow r) =>
        new(r.Id, r.Ts, r.ChannelId, r.ChannelName, r.EventType, r.Severity, r.DedupeKey, r.Title, r.Status, (int)r.Attempts, r.NextAttemptAt, r.LastError, (int?)r.HttpStatus, r.EventJson);

    public async Task<IReadOnlyList<ChannelRow>> ListChannelsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<ChannelSqlRow>($"SELECT {ChannelColumns} FROM notification_channels ORDER BY name").ToListAsync(ct);
        return [.. rows.Select(Map)];
    }

    public async Task<ChannelRow?> GetChannelAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<ChannelSqlRow>($"SELECT id AS Id, type AS Type, name AS Name, enabled AS Enabled, min_severity AS MinSeverity, events AS Events, secret_name AS SecretName, created_at AS CreatedAt, updated_at AS UpdatedAt FROM notification_channels WHERE id = {id}").ToListAsync(ct);
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    public async Task<bool> ChannelNameExistsAsync(string name, string? exceptId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<TextRow>($"SELECT id AS Value FROM notification_channels WHERE name = {name} AND id IS NOT {exceptId}").ToListAsync(ct);
        return rows.Count > 0;
    }

    private static Task<int> PutSecretAsync(AppDbContext db, ISecretBox box, string name, string json, long now, CancellationToken ct)
    {
        var ciphertext = box.Encrypt(json, Aad(name));
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO secret_store (name, ciphertext, updated_at) VALUES ({name}, {ciphertext}, {now}) ON CONFLICT(name) DO UPDATE SET ciphertext=excluded.ciphertext, updated_at=excluded.updated_at",
            ct);
    }

    public async Task InsertChannelAsync(ChannelRow row, string configJson, CancellationToken ct)
    {
        var events = JsonSerializer.Serialize(row.Events);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PutSecretAsync(db, box, row.SecretName, configJson, row.UpdatedAt, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO notification_channels (id, type, name, enabled, min_severity, events, secret_name, created_at, updated_at) VALUES ({row.Id}, {row.Type}, {row.Name}, {(row.Enabled ? 1 : 0)}, {row.MinSeverity}, {events}, {row.SecretName}, {row.CreatedAt}, {row.UpdatedAt})",
            ct);
        await tx.CommitAsync(ct);
    }

    public async Task UpdateChannelAsync(ChannelRow row, string? configJson, CancellationToken ct)
    {
        var events = JsonSerializer.Serialize(row.Events);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (configJson is not null)
        {
            await PutSecretAsync(db, box, row.SecretName, configJson, row.UpdatedAt, ct);
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE notification_channels SET name={row.Name}, enabled={(row.Enabled ? 1 : 0)}, min_severity={row.MinSeverity}, events={events}, updated_at={row.UpdatedAt} WHERE id={row.Id}",
            ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteChannelAsync(ChannelRow row, CancellationToken ct)
    {
        var prefix = $"{row.Id}|";
        var len = prefix.Length;
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notification_channels WHERE id = {row.Id}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notification_dedupe WHERE substr(key, 1, {len}) = {prefix}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM secret_store WHERE name = {row.SecretName}", ct);
        await tx.CommitAsync(ct);
    }

    public async Task<string?> GetConfigJsonAsync(string secretName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<TextRow>($"SELECT ciphertext AS Value FROM secret_store WHERE name = {secretName}").ToListAsync(ct);
        return rows.Count == 0 ? null : box.Decrypt(rows[0].Value, Aad(secretName));
    }

    public async Task<long> InsertDeliveryAsync(DeliveryRow d, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO notification_deliveries (ts, channel_id, event_type, severity, dedupe_key, title, status, attempts, next_attempt_at, last_error, http_status, event_json) VALUES ({d.Ts}, {d.ChannelId}, {d.EventType}, {d.Severity}, {d.DedupeKey}, {d.Title}, {d.Status}, {d.Attempts}, {d.NextAttemptAt}, {d.LastError}, {d.HttpStatus}, {d.EventJson})",
            ct);
        var id = (await db.Database.SqlQuery<LongRow>($"SELECT last_insert_rowid() AS Value").ToListAsync(ct))[0].Value;
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<DeliveryRow?> GetDeliveryAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<DeliverySqlRow>($"{DeliverySelect} WHERE d.id = {{0}}", id).ToListAsync(ct);
        return rows.Count == 0 ? null : Map(rows[0]);
    }

    public async Task UpdateDeliveryResultAsync(long id, DeliveryResultUpdate u, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE notification_deliveries SET status={u.Status}, attempts={u.Attempts}, next_attempt_at={u.NextAttemptAt}, last_error={u.LastError}, http_status={u.HttpStatus} WHERE id={id}",
            ct);
    }

    public async Task<IReadOnlyList<DeliveryRow>> ListDeliveriesAsync(int limit, string? channelId, long? beforeId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<DeliverySqlRow>(
            $"{DeliverySelect} WHERE ({{0}} IS NULL OR d.channel_id = {{0}}) AND ({{1}} IS NULL OR d.id < {{1}}) ORDER BY d.id DESC LIMIT {{2}}",
            (object?)channelId ?? DBNull.Value,
            (object?)beforeId ?? DBNull.Value,
            limit).ToListAsync(ct);
        return [.. rows.Select(Map)];
    }

    public async Task<IReadOnlyList<DeliveryRow>> DueRetriesAsync(long now, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<DeliverySqlRow>(
            $"{DeliverySelect} WHERE d.status = 'failed' AND d.next_attempt_at IS NOT NULL AND d.next_attempt_at <= {{0}} ORDER BY d.next_attempt_at, d.id LIMIT {{1}}",
            now,
            limit).ToListAsync(ct);
        return [.. rows.Select(Map)];
    }

    public async Task PruneAsync(long olderThan, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notification_deliveries WHERE ts < {olderThan} AND status != 'failed'", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notification_dedupe WHERE last_sent_at < {olderThan}", ct);
    }

    public async Task<long?> LastSentAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<LongRow>($"SELECT last_sent_at AS Value FROM notification_dedupe WHERE key = {key}").ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0].Value;
    }

    public async Task MarkSentAsync(string key, long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO notification_dedupe (key, last_sent_at, suppressed) VALUES ({key}, {now}, 0) ON CONFLICT(key) DO UPDATE SET last_sent_at=excluded.last_sent_at",
            ct);
    }

    public async Task BumpSuppressedAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_dedupe SET suppressed = suppressed + 1 WHERE key = {key}", ct);
    }

    public async Task<int> SuppressedCountAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<LongRow>($"SELECT suppressed AS Value FROM notification_dedupe WHERE key = {key}").ToListAsync(ct);
        return rows.Count == 0 ? 0 : (int)rows[0].Value;
    }
}

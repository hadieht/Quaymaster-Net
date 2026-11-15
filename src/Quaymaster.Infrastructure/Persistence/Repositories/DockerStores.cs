using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Operations;
using Quaymaster.Domain.Stats;

namespace Quaymaster.Infrastructure.Persistence.Repositories;

// The tables below (events, stats_rollups, operations, kv, ignored_targets, audit_log) are owned by the Baseline
// migration. They are accessed with parameterised SQL projected onto plain row types, so no entity mapping (and no
// model-snapshot churn) is needed for tables that are only ever read/written in these exact shapes.

#pragma warning disable CA1812 // row types are instantiated by EF
internal sealed class EventRow
{
    public long Id { get; set; }

    public long Ts { get; set; }

    public string Type { get; set; } = "";

    public string Action { get; set; } = "";

    public string ActorId { get; set; } = "";

    public string? Name { get; set; }

    public string? Image { get; set; }

    public string? Project { get; set; }

    public string? Service { get; set; }

    public string Attributes { get; set; } = "{}";

    public long Synthetic { get; set; }
}

internal sealed class RollupRowSql
{
    public string ContainerId { get; set; } = "";

    public long Minute { get; set; }

    public string Name { get; set; } = "";

    public long Samples { get; set; }

    public double CpuAvg { get; set; }

    public double CpuMax { get; set; }

    public double MemAvg { get; set; }

    public double MemMax { get; set; }

    public long NetRx { get; set; }

    public long NetTx { get; set; }

    public long BlkRead { get; set; }

    public long BlkWrite { get; set; }
}

internal sealed class OperationRow
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    public string Target { get; set; } = "";

    public string Status { get; set; } = "";

    public string Actor { get; set; } = "";

    public long StartedAt { get; set; }

    public long? FinishedAt { get; set; }

    public string? ComposeVersion { get; set; }

    public string Output { get; set; } = "";

    public string? Error { get; set; }
}

internal sealed class IgnoredRow
{
    public long Id { get; set; }

    public string Kind { get; set; } = "";

    public string Name { get; set; } = "";

    public string WorkingDir { get; set; } = "";

    public long IgnoredAt { get; set; }

    public string IgnoredBy { get; set; } = "";
}

internal sealed class AuditRow
{
    public long Id { get; set; }

    public long Ts { get; set; }

    public string Actor { get; set; } = "";

    public string? Ip { get; set; }

    public string Action { get; set; } = "";

    public string? Target { get; set; }

    public string? Params { get; set; }

    public string Outcome { get; set; } = "";

    public long? DurationMs { get; set; }
}

internal sealed class KvRow
{
    public string Value { get; set; } = "";
}
#pragma warning restore CA1812

internal sealed class EventStore(IDbContextFactory<AppDbContext> factory) : IEventStore
{
    public async Task<IReadOnlyList<long>> InsertManyAsync(IReadOnlyList<NewEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = new List<long>(events.Count);
        foreach (var e in events)
        {
            var attrs = JsonSerializer.Serialize(e.Attributes);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO events (ts, type, action, actor_id, name, image, project, service, attributes, synthetic) VALUES ({e.Ts}, {e.Type}, {e.Action}, {e.ActorId}, {e.Name}, {e.Image}, {e.Project}, {e.Service}, {attrs}, {(e.Synthetic ? 1 : 0)})",
                ct);
            ids.Add(await db.Database.SqlQuery<long>($"SELECT last_insert_rowid() AS Value").SingleAsync(ct));
        }

        await tx.CommitAsync(ct);
        return ids;
    }

    public async Task<IReadOnlyList<DockerEventRecord>> ListAsync(EventQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new List<string>();
        var args = new List<object>();
        if (query.BeforeId is { } before)
        {
            where.Add($"id < {{{args.Count}}}");
            args.Add(before);
        }

        if (query.SinceId is { } since && since > 0)
        {
            where.Add($"id > {{{args.Count}}}");
            args.Add(since);
        }

        if (query.ContainerId is { Length: > 0 } cid)
        {
            where.Add($"actor_id = {{{args.Count}}}");
            args.Add(cid);
        }

        if (query.Project is { Length: > 0 } project)
        {
            where.Add($"project = {{{args.Count}}}");
            args.Add(project);
        }

        var sql = new StringBuilder("SELECT id AS Id, ts AS Ts, type AS Type, action AS Action, actor_id AS ActorId, name AS Name, image AS Image, project AS Project, service AS Service, attributes AS Attributes, synthetic AS Synthetic FROM events");
        if (where.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        }

        sql.Append(CultureInfo.InvariantCulture, $" ORDER BY id DESC LIMIT {{{args.Count}}}");
        args.Add(query.Limit);
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<EventRow>(sql.ToString(), [.. args]).ToListAsync(ct);
        return [.. rows.Select(r => new DockerEventRecord(
            r.Id,
            Iso.Format(DateTimeOffset.FromUnixTimeMilliseconds(r.Ts)),
            r.Type,
            r.Action,
            r.ActorId,
            r.Name,
            r.Image,
            r.Project,
            r.Service,
            JsonSerializer.Deserialize<Dictionary<string, string>>(r.Attributes) ?? [],
            r.Synthetic == 1))];
    }

    public async Task<long?> LastTimestampAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.SqlQuery<long?>($"SELECT MAX(ts) AS Value FROM events WHERE synthetic = 0").SingleAsync(ct);
    }

    /// <summary>Deletes in batches so a huge DELETE never holds the write lock for long (RT-26).</summary>
    public async Task<int> PruneAsync(long olderThanMs, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var total = 0;
        int removed;
        do
        {
            removed = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM events WHERE id IN (SELECT id FROM events WHERE ts < {olderThanMs} LIMIT 5000)", ct);
            total += removed;
        }
        while (removed >= 5000);

        return total;
    }
}

internal sealed class StatsStore(IDbContextFactory<AppDbContext> factory) : IStatsStore
{
    public async Task UpsertManyAsync(IReadOnlyList<RollupRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var r in rows)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO stats_rollups (container_id, minute, name, samples, cpu_avg, cpu_max, mem_avg, mem_max, net_rx, net_tx, blk_read, blk_write)
                   VALUES ({r.ContainerId}, {r.Minute}, {r.Name}, {r.Samples}, {r.CpuAvg}, {r.CpuMax}, {r.MemAvg}, {r.MemMax}, {r.NetRx}, {r.NetTx}, {r.BlkRead}, {r.BlkWrite})
                   ON CONFLICT(container_id, minute) DO UPDATE SET samples=excluded.samples, cpu_avg=excluded.cpu_avg, cpu_max=excluded.cpu_max,
                     mem_avg=excluded.mem_avg, mem_max=excluded.mem_max, net_rx=excluded.net_rx, net_tx=excluded.net_tx,
                     blk_read=excluded.blk_read, blk_write=excluded.blk_write",
                ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<RollupRow>> HistoryAsync(string containerId, long sinceMinute, int limit, CancellationToken ct, long? untilMinute = null)
    {
        var until = untilMinute ?? long.MaxValue;
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<RollupRowSql>(
            $"SELECT container_id AS ContainerId, minute AS Minute, name AS Name, samples AS Samples, cpu_avg AS CpuAvg, cpu_max AS CpuMax, mem_avg AS MemAvg, mem_max AS MemMax, net_rx AS NetRx, net_tx AS NetTx, blk_read AS BlkRead, blk_write AS BlkWrite FROM stats_rollups WHERE container_id = {containerId} AND minute >= {sinceMinute} AND minute <= {until} ORDER BY minute LIMIT {limit}")
            .ToListAsync(ct);
        return [.. rows.Select(r => new RollupRow(r.ContainerId, r.Minute, r.Name, (int)r.Samples, r.CpuAvg, r.CpuMax, r.MemAvg, r.MemMax, r.NetRx, r.NetTx, r.BlkRead, r.BlkWrite))];
    }

    public async IAsyncEnumerable<RollupRow> StreamAsync(long sinceMinute, long untilMinute, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = db.Database.SqlQuery<RollupRowSql>(
            $"SELECT container_id AS ContainerId, minute AS Minute, name AS Name, samples AS Samples, cpu_avg AS CpuAvg, cpu_max AS CpuMax, mem_avg AS MemAvg, mem_max AS MemMax, net_rx AS NetRx, net_tx AS NetTx, blk_read AS BlkRead, blk_write AS BlkWrite FROM stats_rollups WHERE minute >= {sinceMinute} AND minute <= {untilMinute} ORDER BY container_id, minute")
            .AsAsyncEnumerable();
        await foreach (var r in rows.WithCancellation(ct))
        {
            yield return new RollupRow(r.ContainerId, r.Minute, r.Name, (int)r.Samples, r.CpuAvg, r.CpuMax, r.MemAvg, r.MemMax, r.NetRx, r.NetTx, r.BlkRead, r.BlkWrite);
        }
    }

    public async Task<int> PruneAsync(long olderThanMinute, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var total = 0;
        int removed;
        do
        {
            removed = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM stats_rollups WHERE (container_id, minute) IN (SELECT container_id, minute FROM stats_rollups WHERE minute < {olderThanMinute} LIMIT 5000)",
                ct);
            total += removed;
        }
        while (removed >= 5000);

        return total;
    }
}

internal sealed class OperationStore(IDbContextFactory<AppDbContext> factory) : IOperationStore
{
    private const int MaxOutput = 256 * 1024;

    private static readonly JsonSerializerOptions ParamOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async Task CreateAsync(NewOperation operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var paramsJson = JsonSerializer.Serialize(operation.Params, ParamOptions);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO operations (id, kind, target, status, actor, started_at, compose_version, params) VALUES ({operation.Id}, {operation.Kind}, {operation.Target}, 'running', {operation.Actor}, {now}, {operation.ComposeVersion}, {paramsJson})",
            ct);
    }

    public async Task AppendOutputAsync(string id, string text, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var keep = -MaxOutput;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE operations SET output = substr(output || {text}, {keep}) WHERE id = {id}", ct);
    }

    public async Task FinishAsync(string id, string status, string? failure, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE operations SET status = {status}, finished_at = {now}, error = {failure} WHERE id = {id}", ct);
    }

    public async Task<OperationInfoDto?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Database.SqlQuery<OperationRow>($"SELECT id AS Id, kind AS Kind, target AS Target, status AS Status, actor AS Actor, started_at AS StartedAt, finished_at AS FinishedAt, compose_version AS ComposeVersion, output AS Output, error AS Error FROM operations WHERE id = {id}")
            .FirstOrDefaultAsync(ct);
        return row is null ? null : ToDto(row);
    }

    public async Task<IReadOnlyList<OperationInfoDto>> ListAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<OperationRow>($"SELECT id AS Id, kind AS Kind, target AS Target, status AS Status, actor AS Actor, started_at AS StartedAt, finished_at AS FinishedAt, compose_version AS ComposeVersion, output AS Output, error AS Error FROM operations ORDER BY started_at DESC LIMIT {limit}")
            .ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    public async Task<int> MarkInterruptedAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE operations SET status='failed', finished_at={now}, error='interrupted: Quaymaster stopped while this operation was running' WHERE status='running'",
            ct);
    }

    private static OperationInfoDto ToDto(OperationRow r) => new(
        r.Id,
        r.Kind,
        r.Target,
        r.Status,
        Iso.Format(DateTimeOffset.FromUnixTimeMilliseconds(r.StartedAt)),
        r.FinishedAt is { } f and > 0 ? Iso.Format(DateTimeOffset.FromUnixTimeMilliseconds(f)) : null,
        r.Actor,
        r.ComposeVersion,
        r.Output,
        r.Error);
}

internal sealed class KvStore(IDbContextFactory<AppDbContext> factory) : IKvStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Database.SqlQuery<KvRow>($"SELECT value AS Value FROM kv WHERE key = {key}").FirstOrDefaultAsync(ct);
        return row?.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO kv (key, value) VALUES ({key}, {value}) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ct);
    }
}

internal sealed class IgnoreStore(IDbContextFactory<AppDbContext> factory) : IIgnoreStore
{
    private const string Select = "SELECT id AS Id, kind AS Kind, name AS Name, working_dir AS WorkingDir, ignored_at AS IgnoredAt, ignored_by AS IgnoredBy FROM ignored_targets";

    public async Task<IReadOnlyList<IgnoredEntryDto>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<IgnoredRow>($"{Select} ORDER BY kind, name, working_dir").ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    public async Task<IgnoredEntryDto?> GetAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Database.SqlQueryRaw<IgnoredRow>($"{Select} WHERE id = {{0}}", id).FirstOrDefaultAsync(ct);
        return row is null ? null : ToDto(row);
    }

    /// <summary>Idempotent: re-ignoring an already ignored target returns the existing entry.</summary>
    public async Task<IgnoredEntryDto> AddAsync(string kind, string name, string workingDir, string by, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO ignored_targets (kind, name, working_dir, ignored_at, ignored_by) VALUES ({kind}, {name}, {workingDir}, {now}, {by})", ct);
        var row = await db.Database.SqlQueryRaw<IgnoredRow>($"{Select} WHERE kind = {{0}} AND name = {{1}} AND working_dir = {{2}}", kind, name, workingDir).FirstAsync(ct);
        return ToDto(row);
    }

    public async Task RemoveAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM ignored_targets WHERE id = {id}", ct);
    }

    private static IgnoredEntryDto ToDto(IgnoredRow r) =>
        new(r.Id, r.Kind, r.Name, r.WorkingDir, Iso.Format(DateTimeOffset.FromUnixTimeMilliseconds(r.IgnoredAt)), r.IgnoredBy);
}

internal sealed class AuditReader(IDbContextFactory<AppDbContext> factory) : IAuditReader
{
    public async Task<IReadOnlyList<AuditEntryDto>> ListAsync(AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new List<string>();
        var args = new List<object>();
        if (query.BeforeId is { } before)
        {
            where.Add($"id < {{{args.Count}}}");
            args.Add(before);
        }

        if (!string.IsNullOrEmpty(query.Action))
        {
            where.Add($"action = {{{args.Count}}}");
            args.Add(query.Action);
        }

        if (!string.IsNullOrEmpty(query.Actor))
        {
            where.Add($"actor = {{{args.Count}}}");
            args.Add(query.Actor);
        }

        var sql = new StringBuilder("SELECT id AS Id, ts AS Ts, actor AS Actor, ip AS Ip, action AS Action, target AS Target, params AS Params, outcome AS Outcome, duration_ms AS DurationMs FROM audit_log");
        if (where.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        }

        sql.Append(CultureInfo.InvariantCulture, $" ORDER BY id DESC LIMIT {{{args.Count}}}");
        args.Add(query.Limit);
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<AuditRow>(sql.ToString(), [.. args]).ToListAsync(ct);
        return [.. rows.Select(r => new AuditEntryDto(
            r.Id,
            Iso.Format(DateTimeOffset.FromUnixTimeMilliseconds(r.Ts)),
            r.Actor,
            r.Ip,
            r.Action,
            r.Target,
            string.IsNullOrEmpty(r.Params) ? null : JsonNode.Parse(r.Params),
            r.Outcome,
            r.DurationMs))];
    }
}

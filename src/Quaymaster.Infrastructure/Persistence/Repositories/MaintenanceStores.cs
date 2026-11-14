using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Quaymaster.Application.Maintenance;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;

namespace Quaymaster.Infrastructure.Persistence.Repositories;

// Tables owned by the Baseline migration (policies, windows, schedules, runs, run_steps), accessed with parameterised SQL like the
// other stores. The stored JSON bodies are the original server's, so a data directory written by it is read as-is.

#pragma warning disable CA1812 // row types are instantiated by EF
internal sealed class PolicySqlRow
{
    public string ScopeKind { get; set; } = "";

    public string ScopeKey { get; set; } = "";

    public string Body { get; set; } = "";

    public long UpdatedAt { get; set; }
}

internal sealed class WindowSqlRow
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Days { get; set; } = "[]";

    public string StartTime { get; set; } = "";

    public string EndTime { get; set; } = "";

    public string Tz { get; set; } = "";

    public int Enabled { get; set; }

    public long CreatedAt { get; set; }
}

internal sealed class ScheduleSqlRow
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Cron { get; set; } = "";

    public string Tz { get; set; } = "";

    public string JobType { get; set; } = "";

    public string Selector { get; set; } = "{}";

    public string Mode { get; set; } = "";

    public string? WindowId { get; set; }

    public int Enabled { get; set; }

    public long CursorUtc { get; set; }

    public long CreatedAt { get; set; }
}

internal sealed class RunSqlRow
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    public string Trigger { get; set; } = "";

    public string? ScheduleId { get; set; }

    public long? OccurrenceUtc { get; set; }

    public string? WindowId { get; set; }

    public long? WindowEndUtc { get; set; }

    public string Target { get; set; } = "";

    public string Status { get; set; } = "";

    public string? Reason { get; set; }

    public string? Plan { get; set; }

    public string? PlanHash { get; set; }

    public string RequestedBy { get; set; } = "";

    public long? ApprovalExpiresAt { get; set; }

    public string? ApprovedBy { get; set; }

    public long? ApprovedAt { get; set; }

    public string? ApprovedPlanHash { get; set; }

    public string? Result { get; set; }

    public string? Guidance { get; set; }

    public long CreatedAt { get; set; }

    public long? StartedAt { get; set; }

    public long? FinishedAt { get; set; }

    public string Options { get; set; } = "{}";
}

internal sealed class IntentSqlRow
{
    public long Id { get; set; }

    public string OperationId { get; set; } = "";

    public string? Service { get; set; }

    public string Action { get; set; } = "";

    public string? FromImage { get; set; }

    public string? ToImage { get; set; }

    public string ContainerIdsBefore { get; set; } = "[]";
}

internal sealed class RunStepSqlRow
{
    public int Id { get; set; }

    public int Seq { get; set; }

    public string? Service { get; set; }

    public string Step { get; set; } = "";

    public string Status { get; set; } = "";

    public long StartedAt { get; set; }

    public long? FinishedAt { get; set; }

    public string? Detail { get; set; }
}
#pragma warning restore CA1812

internal static class MaintenanceJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static string? Iso(long? ms) => ms is { } v ? ImageReferences.Iso(v) : null;
}

internal sealed class PolicyStore(IDbContextFactory<AppDbContext> factory) : IPolicyStore
{
    private const string Select = "SELECT scope_kind AS ScopeKind, scope_key AS ScopeKey, body AS Body, updated_at AS UpdatedAt FROM policies";

    private static PolicyRecord Map(PolicySqlRow r) => new(r.ScopeKind, r.ScopeKey, PolicyRules.Parse(JsonNode.Parse(r.Body)), r.UpdatedAt);

    public async Task<IReadOnlyList<PolicyRecord>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return [.. (await db.Database.SqlQueryRaw<PolicySqlRow>(Select + " ORDER BY scope_kind, scope_key").ToListAsync(ct)).Select(Map)];
    }

    public async Task<PolicyRecord?> GetAsync(string kind, string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<PolicySqlRow>(Select + " WHERE scope_kind = {0} AND scope_key = {1}", kind, key).FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }

    public async Task PutAsync(string kind, string key, PolicyInput policy, long now, CancellationToken ct)
    {
        var body = PolicyRules.ToJson(policy).ToJsonString();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO policies (scope_kind, scope_key, body, updated_at) VALUES ({kind}, {key}, {body}, {now})
               ON CONFLICT(scope_kind, scope_key) DO UPDATE SET body = excluded.body, updated_at = excluded.updated_at",
            ct);
    }

    public async Task<bool> DeleteAsync(string kind, string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM policies WHERE scope_kind = {kind} AND scope_key = {key}", ct) > 0;
    }
}

internal sealed class WindowStore(IDbContextFactory<AppDbContext> factory) : IWindowStore
{
    private const string Select = "SELECT id AS Id, name AS Name, days AS Days, start_time AS StartTime, end_time AS EndTime, tz AS Tz, enabled AS Enabled, created_at AS CreatedAt FROM windows";

    private static WindowRecord Map(WindowSqlRow r) =>
        new(r.Id, r.Name, JsonSerializer.Deserialize<List<int>>(r.Days) ?? [], r.StartTime, r.EndTime, r.Tz, r.Enabled != 0, r.CreatedAt);

    public async Task<IReadOnlyList<WindowRecord>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return [.. (await db.Database.SqlQueryRaw<WindowSqlRow>(Select + " ORDER BY created_at, name").ToListAsync(ct)).Select(Map)];
    }

    public async Task<WindowRecord?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<WindowSqlRow>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }

    public async Task<WindowRecord?> FindByNameAsync(string name, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<WindowSqlRow>(Select + " WHERE name = {0}", name).FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }

    public async Task<WindowRecord> CreateAsync(WindowWrite w, long now, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var days = JsonSerializer.Serialize(w.Days);
        var enabled = w.Enabled ? 1 : 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO windows (id, name, days, start_time, end_time, tz, enabled, created_at) VALUES ({id}, {w.Name}, {days}, {w.Start}, {w.End}, {w.Timezone}, {enabled}, {now})",
            ct);
        return new WindowRecord(id, w.Name, w.Days, w.Start, w.End, w.Timezone, w.Enabled, now);
    }

    public async Task<WindowRecord?> UpdateAsync(string id, WindowWrite w, CancellationToken ct)
    {
        var days = JsonSerializer.Serialize(w.Days);
        var enabled = w.Enabled ? 1 : 0;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var n = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE windows SET name = {w.Name}, days = {days}, start_time = {w.Start}, end_time = {w.End}, tz = {w.Timezone}, enabled = {enabled} WHERE id = {id}",
                ct);
            if (n == 0)
            {
                return null;
            }
        }

        return await GetAsync(id, ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM windows WHERE id = {id}", ct) > 0;
    }

    public async Task<bool> InUseAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.SqlQueryRaw<int>("SELECT 1 AS Value FROM schedules WHERE window_id = {0} LIMIT 1", id).AnyAsync(ct);
    }
}

internal sealed class ScheduleStore(IDbContextFactory<AppDbContext> factory) : IScheduleStore
{
    private const string Select = "SELECT id AS Id, name AS Name, cron AS Cron, tz AS Tz, job_type AS JobType, selector AS Selector, mode AS Mode, window_id AS WindowId, enabled AS Enabled, cursor_utc AS CursorUtc, created_at AS CreatedAt FROM schedules";

    private static ScheduleRecord Map(ScheduleSqlRow r) =>
        new(r.Id, r.Name, r.Cron, r.Tz, r.JobType, JsonNode.Parse(r.Selector) as JsonObject ?? [], r.Mode, r.WindowId, r.Enabled != 0, r.CursorUtc, r.CreatedAt);

    public async Task<IReadOnlyList<ScheduleRecord>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return [.. (await db.Database.SqlQueryRaw<ScheduleSqlRow>(Select + " ORDER BY created_at, name").ToListAsync(ct)).Select(Map)];
    }

    public async Task<ScheduleRecord?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<ScheduleSqlRow>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }

    public async Task<ScheduleRecord?> FindByNameAsync(string name, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<ScheduleSqlRow>(Select + " WHERE name = {0}", name).FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }

    public async Task<ScheduleRecord> CreateAsync(ScheduleWrite w, long cursorUtc, long now, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var selector = w.Selector.ToJsonString();
        var enabled = w.Enabled ? 1 : 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO schedules (id, name, cron, tz, job_type, selector, mode, window_id, enabled, cursor_utc, created_at)
               VALUES ({id}, {w.Name}, {w.Cron}, {w.Timezone}, {w.JobType}, {selector}, {w.Mode}, {w.WindowId}, {enabled}, {cursorUtc}, {now})",
            ct);
        return new ScheduleRecord(id, w.Name, w.Cron, w.Timezone, w.JobType, w.Selector, w.Mode, w.WindowId, w.Enabled, cursorUtc, now);
    }

    public async Task<ScheduleRecord?> UpdateAsync(string id, ScheduleWrite w, CancellationToken ct)
    {
        var selector = w.Selector.ToJsonString();
        var enabled = w.Enabled ? 1 : 0;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var n = await db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE schedules SET name = {w.Name}, cron = {w.Cron}, tz = {w.Timezone}, job_type = {w.JobType}, selector = {selector},
                   mode = {w.Mode}, window_id = {w.WindowId}, enabled = {enabled} WHERE id = {id}",
                ct);
            if (n == 0)
            {
                return null;
            }
        }

        return await GetAsync(id, ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM schedule_occurrences WHERE schedule_id = {id}", ct);
        return await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM schedules WHERE id = {id}", ct) > 0;
    }

    public async Task SetCursorAsync(string id, long cursorUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE schedules SET cursor_utc = {cursorUtc} WHERE id = {id}", ct);
    }
}

internal sealed class RunStore(IDbContextFactory<AppDbContext> factory) : IRunStore
{
    private const string Select = @"SELECT id AS Id, kind AS Kind, trigger AS Trigger, schedule_id AS ScheduleId, occurrence_utc AS OccurrenceUtc, window_id AS WindowId,
        window_end_utc AS WindowEndUtc, target AS Target, status AS Status, reason AS Reason, plan AS Plan, plan_hash AS PlanHash, requested_by AS RequestedBy,
        approval_expires_at AS ApprovalExpiresAt, approved_by AS ApprovedBy, approved_at AS ApprovedAt, approved_plan_hash AS ApprovedPlanHash,
        result AS Result, guidance AS Guidance, created_at AS CreatedAt, started_at AS StartedAt, finished_at AS FinishedAt, options AS Options FROM runs";

    private static RunInfo ToInfo(RunSqlRow r) => new()
    {
        Id = r.Id,
        Kind = r.Kind,
        Trigger = r.Trigger,
        ScheduleId = r.ScheduleId,
        Occurrence = MaintenanceJson.Iso(r.OccurrenceUtc),
        WindowId = r.WindowId,
        WindowEnd = MaintenanceJson.Iso(r.WindowEndUtc),
        Target = r.Target,
        Status = r.Status,
        Reason = r.Reason,
        PlanHash = r.PlanHash,
        RequestedBy = r.RequestedBy,
        ApprovalExpiresAt = MaintenanceJson.Iso(r.ApprovalExpiresAt),
        ApprovedBy = r.ApprovedBy,
        ApprovedAt = MaintenanceJson.Iso(r.ApprovedAt),
        ApprovedPlanHash = r.ApprovedPlanHash,
        Result = r.Result is null ? null : JsonSerializer.Deserialize<RunResult>(r.Result, MaintenanceJson.Options),
        Guidance = r.Guidance is null ? null : JsonSerializer.Deserialize<List<string>>(r.Guidance, MaintenanceJson.Options),
        CreatedAt = ImageReferences.Iso(r.CreatedAt),
        StartedAt = MaintenanceJson.Iso(r.StartedAt),
        FinishedAt = MaintenanceJson.Iso(r.FinishedAt),
    };

    public async Task InsertAsync(NewRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var plan = run.Plan is null ? null : JsonSerializer.Serialize(run.Plan, MaintenanceJson.Options);
        var options = run.Options.ToJsonString();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO runs (id, kind, trigger, schedule_id, occurrence_utc, window_id, target, status, reason, plan, plan_hash, requested_by, approval_expires_at, options, created_at)
               VALUES ({run.Id}, {run.Kind}, {run.Trigger}, {run.ScheduleId}, {run.OccurrenceUtc}, {run.WindowId}, {run.Target}, {run.Status}, {run.Reason}, {plan}, {run.Plan?.PlanHash}, {run.RequestedBy}, {run.ApprovalExpiresAt}, {options}, {run.CreatedAt})",
            ct);
    }

    public async Task<RunMeta?> GetMetaAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<RunSqlRow>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        return r is null ? null : new RunMeta(JsonNode.Parse(r.Options) as JsonObject ?? [], r.ApprovalExpiresAt, r.StartedAt, r.WindowEndUtc, r.WindowId, r.ScheduleId);
    }

    public async Task<IReadOnlyList<RunInfo>> ListAsync(int limit, string? status, string? target, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var sql = Select + " WHERE 1 = 1";
        var args = new List<object>();
        if (status is not null)
        {
            sql += " AND status = {" + args.Count + "}";
            args.Add(status);
        }

        if (target is not null)
        {
            sql += " AND target = {" + args.Count + "}";
            args.Add(target);
        }

        sql += " ORDER BY created_at DESC, rowid DESC LIMIT {" + args.Count + "}";
        args.Add(limit);
        var rows = await db.Database.SqlQueryRaw<RunSqlRow>(sql, [.. args]).ToListAsync(ct);
        return [.. rows.Select(ToInfo)];
    }

    public async Task<RunInfo?> RunningAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<RunSqlRow>(Select + " WHERE status = 'running' LIMIT 1").FirstOrDefaultAsync(ct);
        return r is null ? null : ToInfo(r);
    }

    public async Task<bool> ApproveAsync(string id, string by, string planHash, long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE runs SET status = 'approved', approved_by = {by}, approved_at = {now}, approved_plan_hash = {planHash} WHERE id = {id} AND status = 'awaiting_approval'", ct) > 0;
    }

    public async Task<bool> MarkRunningAsync(string id, long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE runs SET status = 'running', started_at = {now} WHERE id = {id} AND status = 'approved'", ct) > 0;
    }

    public async Task SetWindowEndAsync(string id, long windowEndUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE runs SET window_end_utc = {windowEndUtc} WHERE id = {id}", ct);
    }

    public async Task FinishAsync(string id, string status, RunResult? result, IReadOnlyList<string>? guidance, string? reason, long now, CancellationToken ct)
    {
        var res = result is null ? null : JsonSerializer.Serialize(result, MaintenanceJson.Options);
        var guide = guidance is null ? null : JsonSerializer.Serialize(guidance, MaintenanceJson.Options);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE runs SET status = {status}, result = {res}, guidance = {guide}, reason = {reason}, finished_at = {now} WHERE id = {id}", ct);
    }

    public async Task<IReadOnlyList<RunInfo>> ExpireApprovalsAsync(long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<RunSqlRow>(
            Select + " WHERE status = 'awaiting_approval' AND approval_expires_at IS NOT NULL AND approval_expires_at <= {0}", now).ToListAsync(ct);
        if (rows.Count > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE runs SET status = 'skipped', finished_at = {now}, reason = 'approval expired' WHERE status = 'awaiting_approval' AND approval_expires_at IS NOT NULL AND approval_expires_at <= {now}", ct);
        }

        return [.. rows.Select(ToInfo)];
    }

    public async Task<IReadOnlyList<RunInfo>> InterruptRunningAsync(long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQueryRaw<RunSqlRow>(Select + " WHERE status = 'running' ORDER BY created_at").ToListAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE runs SET status = 'interrupted', finished_at = {now}, reason = 'Quaymaster stopped while this run was in progress' WHERE status = 'running'", ct);
        return [.. rows.Select(ToInfo)];
    }

    public async Task<int> InterruptApprovedAsync(long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE runs SET status = 'interrupted', finished_at = {now}, reason = 'Quaymaster stopped before this approved run started; nothing was changed' WHERE status = 'approved'", ct);
    }

    public async Task<int> StartStepAsync(string runId, int seq, string? service, string stepName, long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO run_steps (run_id, seq, service, step, status, started_at) VALUES ({runId}, {seq}, {service}, {stepName}, 'running', {now})", ct);
        return (int)(await db.Database.SqlQueryRaw<long>("SELECT last_insert_rowid() AS Value").ToListAsync(ct))[0];
    }

    public async Task FinishStepAsync(int stepId, string status, JsonObject? detail, long now, CancellationToken ct)
    {
        var d = detail?.ToJsonString();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE run_steps SET status = {status}, finished_at = {now}, detail = {d} WHERE id = {stepId}", ct);
    }

    public async Task FailOpenStepsAsync(string runId, long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE run_steps SET status = 'failed', finished_at = {now} WHERE run_id = {runId} AND status = 'running'", ct);
    }

    public async Task<long> AddIntentAsync(NewIntent intent, long now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var ids = JsonSerializer.Serialize(intent.ContainerIdsBefore, MaintenanceJson.Options);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO intents (operation_id, service, action, from_image, to_image, container_ids_before, created_at) VALUES ({intent.OperationId}, {intent.Service}, {intent.Action}, {intent.FromImage}, {intent.ToImage}, {ids}, {now})", ct);
        return (await db.Database.SqlQueryRaw<long>("SELECT last_insert_rowid() AS Value").ToListAsync(ct))[0];
    }

    public async Task CompleteIntentAsync(long id, long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE intents SET completed_at = {now} WHERE id = {id}", ct);
    }

    public async Task<IReadOnlyList<IntentInfo>> OpenIntentsAsync(string? operationId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        const string cols = "SELECT id AS Id, operation_id AS OperationId, service AS Service, action AS Action, from_image AS FromImage, to_image AS ToImage, container_ids_before AS ContainerIdsBefore FROM intents WHERE completed_at IS NULL";
        var rows = operationId is null
            ? await db.Database.SqlQueryRaw<IntentSqlRow>(cols + " ORDER BY id").ToListAsync(ct)
            : await db.Database.SqlQueryRaw<IntentSqlRow>(cols + " AND operation_id = {0} ORDER BY id", operationId).ToListAsync(ct);
        return [.. rows.Select(r => new IntentInfo(r.Id, r.OperationId, r.Service, r.Action, r.FromImage, r.ToImage, JsonSerializer.Deserialize<List<string>>(r.ContainerIdsBefore, MaintenanceJson.Options) ?? []))];
    }

    public async Task<RunDetail?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<RunSqlRow>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        if (r is null)
        {
            return null;
        }

        var steps = await db.Database.SqlQueryRaw<RunStepSqlRow>(
            "SELECT id AS Id, seq AS Seq, service AS Service, step AS Step, status AS Status, started_at AS StartedAt, finished_at AS FinishedAt, detail AS Detail FROM run_steps WHERE run_id = {0} ORDER BY seq, id", id)
            .ToListAsync(ct);
        var info = new RunInfo
        {
            Id = r.Id,
            Kind = r.Kind,
            Trigger = r.Trigger,
            ScheduleId = r.ScheduleId,
            Occurrence = MaintenanceJson.Iso(r.OccurrenceUtc),
            WindowId = r.WindowId,
            WindowEnd = MaintenanceJson.Iso(r.WindowEndUtc),
            Target = r.Target,
            Status = r.Status,
            Reason = r.Reason,
            PlanHash = r.PlanHash,
            RequestedBy = r.RequestedBy,
            ApprovalExpiresAt = MaintenanceJson.Iso(r.ApprovalExpiresAt),
            ApprovedBy = r.ApprovedBy,
            ApprovedAt = MaintenanceJson.Iso(r.ApprovedAt),
            ApprovedPlanHash = r.ApprovedPlanHash,
            Result = r.Result is null ? null : JsonSerializer.Deserialize<RunResult>(r.Result, MaintenanceJson.Options),
            Guidance = r.Guidance is null ? null : JsonSerializer.Deserialize<List<string>>(r.Guidance, MaintenanceJson.Options),
            CreatedAt = ImageReferences.Iso(r.CreatedAt),
            StartedAt = MaintenanceJson.Iso(r.StartedAt),
            FinishedAt = MaintenanceJson.Iso(r.FinishedAt),
        };
        return new RunDetail
        {
            Info = info,
            Plan = r.Plan is null ? null : JsonSerializer.Deserialize<Plan>(r.Plan, MaintenanceJson.Options),
            Steps = [.. steps.Select(s => new RunStepDto(s.Id, s.Seq, s.Service, s.Step, s.Status, ImageReferences.Iso(s.StartedAt), MaintenanceJson.Iso(s.FinishedAt), s.Detail is null ? null : JsonNode.Parse(s.Detail) as JsonObject))],
        };
    }
}

/// <summary>Probe verification (RT-10): GET without following redirects, 5 s timeout; the status must equal the expected one.</summary>
internal sealed class HttpProbeVerifier : IProbeVerifier
{
    public async Task VerifyAsync(string url, int expectStatus, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var res = await client.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode != expectStatus)
            {
                throw Application.Errors.AppException.BadRequest(
                    $"The probe answered HTTP {(int)res.StatusCode}, expected {expectStatus}. Probes are only enabled once they pass against the running version.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            var why = e is TaskCanceledException ? "timeout" : e.Message;
            throw Application.Errors.AppException.BadRequest(
                $"The probe could not be reached from Quaymaster ({why}). It must be reachable from Quaymaster's network (publish a port or attach Quaymaster to the network).");
        }
    }
}

/// <summary>The health gate's HTTP probe: GET without following redirects (5 s), resolves to the status code.</summary>
internal sealed class HttpStatusProbe : IHttpProbe
{
    public async Task<int> StatusAsync(string url, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var res = await client.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, ct);
        return (int)res.StatusCode;
    }
}

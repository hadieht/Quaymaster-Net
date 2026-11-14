using System.Text.Json.Nodes;
using Quaymaster.Domain.Maintenance;

namespace Quaymaster.Application.Maintenance;

/// <summary>Policy store: one row per (scope_kind, scope_key), the body holds only the fields that are set.</summary>
public interface IPolicyStore
{
    Task<IReadOnlyList<PolicyRecord>> ListAsync(CancellationToken ct);

    Task<PolicyRecord?> GetAsync(string kind, string key, CancellationToken ct);

    Task PutAsync(string kind, string key, PolicyInput policy, long now, CancellationToken ct);

    /// <summary>Returns false when nothing was stored under (kind, key).</summary>
    Task<bool> DeleteAsync(string kind, string key, CancellationToken ct);
}

public sealed record PolicyRecord(string Kind, string Key, PolicyInput Policy, long UpdatedAt);

public sealed record WindowRecord(string Id, string Name, IReadOnlyList<int> Days, string Start, string End, string Timezone, bool Enabled, long CreatedAt)
{
    public TimeMath.WindowDef Def => new(Days, Start, End, Timezone);
}

public sealed record WindowWrite(string Name, IReadOnlyList<int> Days, string Start, string End, string Timezone, bool Enabled);

public interface IWindowStore
{
    Task<IReadOnlyList<WindowRecord>> ListAsync(CancellationToken ct);

    Task<WindowRecord?> GetAsync(string id, CancellationToken ct);

    Task<WindowRecord?> FindByNameAsync(string name, CancellationToken ct);

    Task<WindowRecord> CreateAsync(WindowWrite w, long now, CancellationToken ct);

    Task<WindowRecord?> UpdateAsync(string id, WindowWrite w, CancellationToken ct);

    Task<bool> DeleteAsync(string id, CancellationToken ct);

    /// <summary>True while a schedule references the window.</summary>
    Task<bool> InUseAsync(string id, CancellationToken ct);
}

public sealed record ScheduleRecord(
    string Id,
    string Name,
    string Cron,
    string Timezone,
    string JobType,
    JsonObject Selector,
    string Mode,
    string? WindowId,
    bool Enabled,
    long CursorUtc,
    long CreatedAt);

public sealed record ScheduleWrite(string Name, string Cron, string Timezone, string JobType, JsonObject Selector, string Mode, string? WindowId, bool Enabled);

public interface IScheduleStore
{
    Task<IReadOnlyList<ScheduleRecord>> ListAsync(CancellationToken ct);

    Task<ScheduleRecord?> GetAsync(string id, CancellationToken ct);

    Task<ScheduleRecord?> FindByNameAsync(string name, CancellationToken ct);

    /// <summary><paramref name="cursorUtc"/> = creation time: a schedule never fires for the past.</summary>
    Task<ScheduleRecord> CreateAsync(ScheduleWrite w, long cursorUtc, long now, CancellationToken ct);

    Task<ScheduleRecord?> UpdateAsync(string id, ScheduleWrite w, CancellationToken ct);

    Task<bool> DeleteAsync(string id, CancellationToken ct);

    /// <summary>T4b: the scheduler advances the cursor after each fired occurrence.</summary>
    Task SetCursorAsync(string id, long cursorUtc, CancellationToken ct);
}

/// <summary>A run row as written by the planner (dry runs) and, in T4b, by the executor. Times are epoch milliseconds.</summary>
public sealed record NewRun(
    string Id,
    string Kind,
    string Trigger,
    string Target,
    string Status,
    string? Reason,
    Plan? Plan,
    string RequestedBy,
    JsonObject Options,
    long CreatedAt);

/// <summary>T4a: insert + read of the <c>runs</c> table. T4b extends it (status transitions, steps, listing).</summary>
public interface IRunStore
{
    Task InsertAsync(NewRun run, CancellationToken ct);

    Task<RunDetail?> GetAsync(string id, CancellationToken ct);
}

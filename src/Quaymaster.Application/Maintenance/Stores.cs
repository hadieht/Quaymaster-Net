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
    long CreatedAt,
    string? ScheduleId = null,
    long? OccurrenceUtc = null,
    string? WindowId = null,
    long? ApprovalExpiresAt = null);

/// <summary>The raw (epoch ms) columns the wire <see cref="RunInfo"/> flattens to ISO strings, plus the stored options.</summary>
public sealed record RunMeta(JsonObject Options, long? ApprovalExpiresAt, long? StartedAt, long? WindowEndUtc, string? WindowId, string? ScheduleId);

/// <summary>A write-ahead intent to be persisted BEFORE a side-effecting sub-step (RT-14).</summary>
public sealed record NewIntent(string OperationId, string? Service, string Action, string? FromImage, string? ToImage, IReadOnlyList<string> ContainerIdsBefore);

/// <summary>The <c>runs</c>, <c>run_steps</c> and <c>intents</c> tables.</summary>
public interface IRunStore
{
    Task InsertAsync(NewRun run, CancellationToken ct);

    Task<RunDetail?> GetAsync(string id, CancellationToken ct);

    Task<RunMeta?> GetMetaAsync(string id, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<RunInfo>> ListAsync(int limit, string? status, string? target, CancellationToken ct);

    /// <summary>A run that is currently <c>running</c>, if any.</summary>
    Task<RunInfo?> RunningAsync(CancellationToken ct);

    /// <summary>Compare-and-set <c>awaiting_approval → approved</c>. False when the run is no longer awaiting approval.</summary>
    Task<bool> ApproveAsync(string id, string by, string planHash, long now, CancellationToken ct);

    /// <summary>Compare-and-set <c>approved → running</c>. False when the run was not approved.</summary>
    Task<bool> MarkRunningAsync(string id, long now, CancellationToken ct);

    Task SetWindowEndAsync(string id, long windowEndUtc, CancellationToken ct);

    /// <summary>Sets the final status.</summary>
    Task FinishAsync(string id, string status, RunResult? result, IReadOnlyList<string>? guidance, string? reason, long now, CancellationToken ct);

    /// <summary>Overdue approvals become <c>skipped</c> ("approval expired"). Returns them.</summary>
    Task<IReadOnlyList<RunInfo>> ExpireApprovalsAsync(long now, CancellationToken ct);

    /// <summary>Startup: <c>running</c> → <c>interrupted</c> (never resumed). Returns the runs that were running.</summary>
    Task<IReadOnlyList<RunInfo>> InterruptRunningAsync(long now, CancellationToken ct);

    /// <summary>Startup: <c>approved</c> but never started → <c>interrupted</c> (never run later on a stale approval).</summary>
    Task<int> InterruptApprovedAsync(long now, CancellationToken ct);

    Task<int> StartStepAsync(string runId, int seq, string? service, string stepName, long now, CancellationToken ct);

    Task FinishStepAsync(int stepId, string status, JsonObject? detail, long now, CancellationToken ct);

    Task FailOpenStepsAsync(string runId, long now, CancellationToken ct);

    Task<long> AddIntentAsync(NewIntent intent, long now, CancellationToken ct);

    Task CompleteIntentAsync(long id, long now, CancellationToken ct);

    /// <summary>Open (not completed) intents, of one run or of all of them.</summary>
    Task<IReadOnlyList<IntentInfo>> OpenIntentsAsync(string? operationId, CancellationToken ct);
}

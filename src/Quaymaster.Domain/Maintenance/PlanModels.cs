using System.Text.Json.Serialization;

namespace Quaymaster.Domain.Maintenance;

// Wire shapes of shared/src/maintenance.ts (camelCase; `T | null` is written as null, optional `?` members are omitted when null).

public sealed record PlanMount(string Type, string Source, string Target, bool ReadOnly);

public sealed record PlanContainer(string Id, string Name);

public sealed record PlanHealth(string Kind, double TimeoutSeconds, double StabilizationSeconds, ProbeSpec? Probe);

public sealed record DataRisk(bool Flagged, IReadOnlyList<PlanMount> Mounts, string Note);

public sealed record Unattended(bool Eligible, IReadOnlyList<string> Blockers);

public sealed record PlanService
{
    public required string Service { get; init; }

    /// <summary><c>update</c> or <c>dependent</c>.</summary>
    public required string Role { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DependentOf { get; init; }

    public required IReadOnlyList<PlanContainer> Containers { get; init; }

    public required string Ref { get; init; }

    public required string FromImageId { get; init; }

    public string? FromDigest { get; init; }

    public string? ToDigest { get; init; }

    public string? ConfigHash { get; init; }

    public required bool WasRunning { get; init; }

    public required bool OneShot { get; init; }

    public required bool AutoRollback { get; init; }

    public required string AutoRollbackReason { get; init; }

    public required PlanHealth Health { get; init; }

    public required string Downtime { get; init; }

    public required DataRisk DataRisk { get; init; }

    public required bool Critical { get; init; }

    public required bool RequireApproval { get; init; }

    public required string UpdateMode { get; init; }

    public required Unattended Unattended { get; init; }

    public required bool Quarantined { get; init; }

    public bool IsUpdate => Role == "update";
}

public sealed record PreflightCheck(string Name, bool Ok, string Detail);

public sealed record ExcludedService(string Service, string Reason);

public sealed record PlanPreflight(bool Ok, IReadOnlyList<PreflightCheck> Checks);

public sealed record PlanValidation(bool Valid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed record PlanDrift(string Service, IReadOnlyList<string> Running, string Wanted);

public sealed record ImpactDependent(string Service, string Via, string Kind);

public sealed record SharedTag(string Ref, string ContainerName, string? Project, string? Service, bool InRun, string Note);

public sealed record PlanImpact(IReadOnlyList<ImpactDependent> Dependents, IReadOnlyList<SharedTag> SharedTag);

public sealed record PlanDisk(long? RequiredBytes, long? FreeBytes, long FloorBytes, bool Ok);

public sealed record PlanBlocked(string Code, string Message);

public sealed record Plan
{
    public int Version { get; init; } = 1;

    /// <summary><c>compose</c> or <c>standalone</c>.</summary>
    public string Kind { get; init; } = "compose";

    public required string Target { get; init; }

    public required string CreatedAt { get; init; }

    public required IReadOnlyList<string> Order { get; init; }

    public required IReadOnlyList<PlanService> Services { get; init; }

    public required IReadOnlyList<ExcludedService> Excluded { get; init; }

    public required PlanPreflight Preflight { get; init; }

    public required PlanValidation Validation { get; init; }

    public required IReadOnlyList<PlanDrift> Drift { get; init; }

    public required PlanImpact Impact { get; init; }

    public required PlanDisk Disk { get; init; }

    public PlanBlocked? Blocked { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required string PlanHash { get; init; }
}

public static class RunStatuses
{
    public const string Planned = "planned";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Partial = "partial";
    public const string RolledBack = "rolled_back";
    public const string Skipped = "skipped";
    public const string Interrupted = "interrupted";
    public const string ApprovalStale = "approval_stale";
    public const string Blocked = "blocked";
    public const string Rejected = "rejected";

    public static readonly IReadOnlyList<string> All =
    [
        Planned, AwaitingApproval, Approved, Running, Completed, Failed, Partial, RolledBack, Skipped, Interrupted, ApprovalStale, Blocked, Rejected,
    ];

    /// <summary>Statuses of runs that can no longer change (restore applies to these).</summary>
    public static bool IsActive(string status) => status is Running or Approved or AwaitingApproval or Planned;
}

public static class ServiceOutcomes
{
    public const string Updated = "updated";
    public const string RolledBack = "rolled_back";
    public const string Failed = "failed";
    public const string FailedLeftAsIs = "failed_left_as_is";
    public const string Skipped = "skipped";
    public const string DependentRecreated = "dependent_recreated";
}

public sealed record ServiceResult(string Service, string Outcome, string Detail);

public sealed record RunResult(IReadOnlyList<ServiceResult> Services, string Summary);

public sealed record RunInfo
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Trigger { get; init; }

    public string? ScheduleId { get; init; }

    public string? Occurrence { get; init; }

    public string? WindowId { get; init; }

    public string? WindowEnd { get; init; }

    public required string Target { get; init; }

    public required string Status { get; init; }

    public string? Reason { get; init; }

    public string? PlanHash { get; init; }

    public required string RequestedBy { get; init; }

    public string? ApprovalExpiresAt { get; init; }

    public string? ApprovedBy { get; init; }

    public string? ApprovedAt { get; init; }

    public string? ApprovedPlanHash { get; init; }

    public RunResult? Result { get; init; }

    public IReadOnlyList<string>? Guidance { get; init; }

    public required string CreatedAt { get; init; }

    public string? StartedAt { get; init; }

    public string? FinishedAt { get; init; }
}

public sealed record RunStepDto(int Id, int Seq, string? Service, string Step, string Status, string StartedAt, string? FinishedAt, System.Text.Json.Nodes.JsonObject? Detail);

public sealed record RunDetail
{
    public required RunInfo Info { get; init; }

    public Plan? Plan { get; init; }

    public required IReadOnlyList<RunStepDto> Steps { get; init; }
}

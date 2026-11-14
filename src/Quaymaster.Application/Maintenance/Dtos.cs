using System.Text.Json.Nodes;
using Quaymaster.Domain.Maintenance;

namespace Quaymaster.Application.Maintenance;

// Wire format of shared/src/maintenance.ts (camelCase). Request bodies use nullable members so the validators can report "Required".

public sealed record PolicyDto(string ScopeKind, string ScopeKey, JsonObject Policy, string UpdatedAt);

public sealed record PolicyListDto(IReadOnlyList<PolicyDto> Policies);

/// <summary>Response of PUT /maintenance/policies/:kind/:key.</summary>
public sealed record PolicyPutDto(string ScopeKind, string ScopeKey, JsonObject Policy);

public sealed record WindowBody(string? Name, IReadOnlyList<int>? Days, string? Start, string? End, string? Timezone, bool? Enabled);

public sealed record WindowDto(
    string Id,
    string Name,
    IReadOnlyList<int> Days,
    string Start,
    string End,
    string Timezone,
    bool Enabled,
    string CreatedAt,
    bool OpenNow,
    string? NextStart);

public sealed record WindowListDto(IReadOnlyList<WindowDto> Windows);

public sealed record SelectorBody(string? Project, string? Container, IReadOnlyList<string>? Services);

public sealed record ScheduleBody(
    string? Name,
    string? Cron,
    string? Timezone,
    string? JobType,
    SelectorBody? Selector,
    string? Mode,
    string? WindowId,
    bool? Enabled);

public sealed record ScheduleDto(
    string Id,
    string Name,
    string Cron,
    string Timezone,
    string JobType,
    JsonObject Selector,
    string Mode,
    string? WindowId,
    bool Enabled,
    string CreatedAt,
    IReadOnlyList<string> NextRuns);

public sealed record ScheduleListDto(IReadOnlyList<ScheduleDto> Schedules);

public sealed record PlanBody(string? Project, IReadOnlyList<string>? Services, bool? ApplyDrift, bool? AllowUnhealthy);

public sealed record OkDto(bool Ok);

/// <summary><c>RunDetail</c> on the wire: the run fields flattened, plus <c>plan</c> and <c>steps</c>.</summary>
public sealed record RunDetailDto(
    string Id,
    string Kind,
    string Trigger,
    string? ScheduleId,
    string? Occurrence,
    string? WindowId,
    string? WindowEnd,
    string Target,
    string Status,
    string? Reason,
    string? PlanHash,
    string RequestedBy,
    string? ApprovalExpiresAt,
    string? ApprovedBy,
    string? ApprovedAt,
    string? ApprovedPlanHash,
    RunResult? Result,
    IReadOnlyList<string>? Guidance,
    string CreatedAt,
    string? StartedAt,
    string? FinishedAt,
    Plan? Plan,
    IReadOnlyList<RunStepDto> Steps)
{
    public static RunDetailDto From(RunDetail d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var i = d.Info;
        return new RunDetailDto(
            i.Id, i.Kind, i.Trigger, i.ScheduleId, i.Occurrence, i.WindowId, i.WindowEnd, i.Target, i.Status, i.Reason, i.PlanHash,
            i.RequestedBy, i.ApprovalExpiresAt, i.ApprovedBy, i.ApprovedAt, i.ApprovedPlanHash, i.Result, i.Guidance, i.CreatedAt,
            i.StartedAt, i.FinishedAt, d.Plan, d.Steps);
    }
}

/// <summary>Body of <c>POST /maintenance/runs</c>.</summary>
public sealed record RunBody(
    string? Project,
    IReadOnlyList<string>? Services,
    bool? ApplyDrift,
    bool? AllowUnhealthy,
    bool? StartNow,
    string? Confirm,
    string? WindowId,
    bool? IgnoreWindow);

public sealed record ApproveBody(string? PlanHash);

public sealed record StandaloneBody(string? Container, string? Confirm);

public sealed record RestoreBody(string? Confirm, IReadOnlyList<string>? Services);

public sealed record RunListDto(IReadOnlyList<RunInfo> Runs);

public sealed record OperationStartedDto(string OperationId);

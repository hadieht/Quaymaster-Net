using System.Globalization;
using System.Text.RegularExpressions;
using Quaymaster.Domain.Registry;

namespace Quaymaster.Domain.Maintenance;

/// <summary>The slice of <c>docker inspect</c> the health gate reads.</summary>
public sealed record InspectLite(string Image, int RestartCount, string Status, bool Running, bool Restarting, int ExitCode, string StartedAt, string? Health);

/// <summary>What the health gate checks (<c>Kind</c> = healthcheck | stability | oneshot).</summary>
public sealed record HealthSpec(string Kind, long TimeoutMs, long StabilizationMs, ProbeSpec? Probe, string ExpectedImageId);

public sealed record HealthResult(bool Ok, string Reason, long ElapsedMs);

/// <summary>An open write-ahead intent (RT-14): what the executor was about to do to a service when it stopped.</summary>
public sealed record IntentInfo(long Id, string OperationId, string? Service, string Action, string? FromImage, string? ToImage, IReadOnlyList<string> ContainerIdsBefore);

/// <summary>One live container of a service as the crash assessment sees it.</summary>
public sealed record LiveContainer(string Id, string ImageId, string State, string Status);

public sealed record ProjectTarget(string Project, string WorkingDir, IReadOnlyList<string> Files);

/// <summary>Result of comparing an interrupted intent with the live state.</summary>
public sealed record IntentAssessment(IReadOnlyList<string> Guidance, bool Down, bool Unhealthy, bool Unverified, string Title);

public static partial class ShellQuote
{
    [GeneratedRegex("^[A-Za-z0-9_@%+=:,./-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRe();

    /// <summary>POSIX single-quote quoting, so a printed recovery command can be pasted as is.</summary>
    public static string Q(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Length > 0 && SafeRe().IsMatch(s) ? s : "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}

/// <summary>Approval rules (RT-08): bound to the plan hash, valid for a limited time.</summary>
public static class ApprovalRules
{
    public static bool IsExpired(long? expiresAtMs, long nowMs) => expiresAtMs is { } e && e <= nowMs;

    public static bool Matches(string? planHash, string approvedHash) => planHash is not null && string.Equals(planHash, approvedHash, StringComparison.Ordinal);

    /// <summary>
    /// The unattended truth table (RT-23): policy auto ∧ schedule auto ∧ no approval required ∧ not critical - for EVERY service in
    /// the plan (the per-service blockers already encode the policy part). Anything else waits for a human.
    /// </summary>
    public static bool IsUnattended(bool scheduled, string? scheduleMode, Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return scheduled && scheduleMode == "auto" && plan.Services.Count > 0 && plan.Services.All(s => s.Unattended.Eligible);
    }
}

/// <summary>Window and timing rules of the executor.</summary>
public static class ExecutionRules
{
    /// <summary>RT-09: a service is only started when its health gate can finish inside the window.</summary>
    public static bool FitsWindow(long? windowEndMs, long nowMs, double healthTimeoutSeconds) =>
        windowEndMs is not { } end || nowMs + (long)(healthTimeoutSeconds * 1000) <= end;

    /// <summary>Two runs overlap when they touch the same project (the unit of Compose ownership).</summary>
    public static bool Overlaps(string projectA, string projectB) => string.Equals(projectA, projectB, StringComparison.Ordinal);

    /// <summary>The run status when the rollout stopped before every service was done.</summary>
    public static string StopStatus(bool anyUpdated) => anyUpdated ? RunStatuses.Partial : RunStatuses.Skipped;

    /// <summary>The summary line stored with the run result.</summary>
    public static string Summary(string status, string target, int updated, string? reason) =>
        status == RunStatuses.Completed ? string.Create(CultureInfo.InvariantCulture, $"Updated {updated} service(s) in {target}") : reason ?? status;
}

/// <summary>Builds the manual recovery commands printed when a run did not end cleanly.</summary>
public static class RecoveryCommands
{
    public static string RetagCommand(string imageId, string repoTag) => $"docker tag {imageId} {ShellQuote.Q(repoTag)}";

    public static string UpCommand(ProjectTarget t, string service)
    {
        ArgumentNullException.ThrowIfNull(t);
        var files = string.Join(' ', t.Files.Select(f => $"--file {ShellQuote.Q(f)}"));
        return $"docker compose --project-name {ShellQuote.Q(t.Project)} --project-directory {ShellQuote.Q(t.WorkingDir)} {files} up -d --no-deps --no-build --pull never -- {ShellQuote.Q(service)}";
    }

    /// <summary>Exact steps to put one service back on its previous image (health-gate failure, auto-rollback off or failed).</summary>
    public static IReadOnlyList<string> ManualRestore(ProjectTarget t, PlanService s, string repoTag)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(s);
        var lines = new List<string>
        {
            $"To restore {s.Service} to the previous image ({ShortId(s.FromImageId)}), run:",
            RetagCommand(s.FromImageId, repoTag),
            UpCommand(t, s.Service),
        };
        if (s.DataRisk.Flagged)
        {
            lines.Add("Note: the service has persistent mounts. If the new version migrated its data, the old image may not start; restore your backup first.");
        }

        return lines;
    }

    public static string ShortId(string? id) => id is null ? "?" : id[..Math.Min(19, id.Length)];

    /// <summary>Docker's <c>Status</c> text carries <c>(healthy)</c> / <c>(unhealthy)</c> / <c>(health: starting)</c>.</summary>
    public static string? HealthFromStatus(string? status)
    {
        if (status is null)
        {
            return null;
        }

        if (status.Contains("(unhealthy)", StringComparison.Ordinal))
        {
            return "unhealthy";
        }

        if (status.Contains("(healthy)", StringComparison.Ordinal))
        {
            return "healthy";
        }

        return status.Contains("(health: starting)", StringComparison.Ordinal) ? "starting" : null;
    }

    /// <summary>
    /// Compares the LIVE state with the intent instead of trusting the intent alone (RT-14): a crash during the health gate must not
    /// read as "nothing changed". <paramref name="target"/> is null when the project can no longer be resolved.
    /// </summary>
    public static IntentAssessment Assess(string project, IntentInfo it, IReadOnlyList<LiveContainer> live, PlanService? plannedService, ProjectTarget? target)
    {
        ArgumentNullException.ThrowIfNull(it);
        ArgumentNullException.ThrowIfNull(live);
        var service = it.Service ?? string.Empty;
        var down = live.Count == 0 || live.Any(c => c.State != "running");
        var unhealthy = !down && live.Any(c => HealthFromStatus(c.Status) == "unhealthy");
        var onFrom = it.FromImage is not null && live.Count > 0 && live.All(c => c.ImageId == it.FromImage);
        var onTo = it.ToImage is not null && live.Count > 0 && live.All(c => c.ImageId == it.ToImage);
        var where = live.Count == 0 ? "no container" : onFrom ? "the previous image" : onTo ? "the new image" : "a different image";
        var guidance = new List<string>
        {
            $"{service}: interrupted during \"{it.Action}\" (from {ShortId(it.FromImage)} to {ShortId(it.ToImage)}); it is currently {(down ? "NOT running" : "running")} on {where}.{(unhealthy ? " Docker reports it UNHEALTHY." : string.Empty)}",
        };
        var needsRestore = it.FromImage is not null && !onFrom;
        var tagMayBeMoved = it.Action == "deploy" && it.ToImage is not null;
        if (down || unhealthy || needsRestore || tagMayBeMoved)
        {
            var repoTag = plannedService is null ? null : RepoTagOf(plannedService.Ref);
            if (plannedService is not null && repoTag is not null && it.FromImage is not null)
            {
                guidance.Add("Put the shared tag back on the previous image:");
                guidance.Add(RetagCommand(plannedService.FromImageId, repoTag));
            }

            if (down || unhealthy || needsRestore)
            {
                guidance.Add(target is null
                    ? $"Bring {service} back with docker compose up -d --no-deps {service} in project {project}."
                    : UpCommand(target, service));
            }
        }

        return new IntentAssessment(guidance, down, unhealthy, !down && !unhealthy && (needsRestore || it.Action == "deploy"), $"{project}/{service}");
    }

    /// <summary><c>repo:tag</c> as the engine knows it for a configured reference (a missing tag means <c>latest</c>).</summary>
    public static string? RepoTagOf(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ImageReferences.Parse(reference) is { } r ? $"{ImageReferences.EngineName(r)}:{r.Tag ?? "latest"}" : null;
    }
}

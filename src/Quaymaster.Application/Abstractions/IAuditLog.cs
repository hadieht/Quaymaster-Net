using Quaymaster.Domain.Auditing;

namespace Quaymaster.Application.Abstractions;

/// <summary>What happened, to whom, from where. Parameters must never contain secrets.</summary>
public sealed record AuditRecord(
    string Actor,
    string Action,
    AuditOutcome Outcome,
    string? Ip = null,
    string? Target = null,
    object? Params = null,
    long? DurationMs = null);

/// <summary>
/// The append-only audit writer. Every authentication / user event and (from later phases) every mutation is
/// recorded through this single abstraction.
/// </summary>
public interface IAuditLog
{
    Task RecordAsync(AuditRecord record, CancellationToken ct = default);

    /// <summary>Applies the retention policy (QM_AUDIT_RETENTION_DAYS). Returns the number of deleted rows.</summary>
    Task<int> PruneAsync(CancellationToken ct = default);
}

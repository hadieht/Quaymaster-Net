using Quaymaster.Domain.Updates;

namespace Quaymaster.Application.Updates;

public interface IQuarantineService
{
    /// <summary>Quarantines (target, digest). False when it is already active; a cleared entry is reactivated.</summary>
    Task<bool> AddAsync(string target, string digest, string? runId, string? reason, CancellationToken ct);

    Task<bool> IsQuarantinedAsync(string target, string digest, CancellationToken ct);

    Task<IReadOnlyList<QuarantineEntry>> ListAsync(CancellationToken ct);

    Task<int> ClearAsync(string target, string? digest, CancellationToken ct);
}

/// <summary>RT-22: a failed (target, remote digest) pair is skipped by unattended runs until an admin clears it.</summary>
public sealed class QuarantineService(IQuarantineStore store, TimeProvider time) : IQuarantineService
{
    public async Task<bool> AddAsync(string target, string digest, string? runId, string? reason, CancellationToken ct)
    {
        var existing = await store.FindAsync(target, digest, ct);
        if (existing is { ClearedAt: null })
        {
            return false;
        }

        await store.UpsertAsync(new QuarantineRow(target, digest, runId, reason, time.GetUtcNow().ToUnixTimeMilliseconds(), null), ct);
        return true;
    }

    public async Task<bool> IsQuarantinedAsync(string target, string digest, CancellationToken ct) =>
        await store.FindAsync(target, digest, ct) is { ClearedAt: null };

    public async Task<IReadOnlyList<QuarantineEntry>> ListAsync(CancellationToken ct) =>
        [.. (await store.ListActiveAsync(ct)).OrderByDescending(r => r.CreatedAt).Select(r => new QuarantineEntry(r.Target, r.Digest, r.RunId, r.Reason, r.CreatedAt))];

    public Task<int> ClearAsync(string target, string? digest, CancellationToken ct) =>
        store.ClearAsync(target, digest, time.GetUtcNow().ToUnixTimeMilliseconds(), ct);
}

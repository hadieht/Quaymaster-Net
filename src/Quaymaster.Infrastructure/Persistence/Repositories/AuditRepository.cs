using Microsoft.EntityFrameworkCore;
using Quaymaster.Application.Abstractions;
using Quaymaster.Domain.Auditing;

namespace Quaymaster.Infrastructure.Persistence.Repositories;

internal sealed class AuditRepository(IDbContextFactory<AppDbContext> factory) : IAuditRepository
{
    private const int PruneBatch = 2000;

    public async Task AddAsync(AuditEntry entry, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AuditLog.Add(entry);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cutoff = olderThan.ToUnixTimeMilliseconds();
        var total = 0;
        int removed;
        do
        {
            // Small batches (the append-only trigger refuses rows younger than 30 days; older rows may go).
            removed = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM audit_log WHERE id IN (SELECT id FROM audit_log WHERE ts < {cutoff} ORDER BY id LIMIT {PruneBatch})",
                ct);
            total += removed;
        }
        while (removed >= PruneBatch);

        return total;
    }
}

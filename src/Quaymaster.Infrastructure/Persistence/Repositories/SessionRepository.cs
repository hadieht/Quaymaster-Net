using Microsoft.EntityFrameworkCore;
using Quaymaster.Application.Abstractions;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Infrastructure.Persistence.Repositories;

internal sealed class SessionRepository(IDbContextFactory<AppDbContext> factory) : ISessionRepository
{
    public async Task AddAsync(Session session, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Session?> FindAsync(string idHash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.IdHash == idHash, ct);
    }

    public async Task TouchAsync(string idHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Sessions.Where(s => s.IdHash == idHash).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeen, now), ct);
    }

    public async Task DeleteAsync(string idHash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Sessions.Where(s => s.IdHash == idHash).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<string>> DeleteForUserAsync(long userId, string? exceptIdHash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var keep = exceptIdHash ?? string.Empty;
        var hashes = await db.Sessions.Where(s => s.UserId == userId && s.IdHash != keep).Select(s => s.IdHash).ToListAsync(ct);
        await db.Sessions.Where(s => s.UserId == userId && s.IdHash != keep).ExecuteDeleteAsync(ct);
        return hashes;
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var idleCutoff = now - idleTimeout;
        return await db.Sessions.Where(s => s.ExpiresAt < now || s.LastSeen < idleCutoff).ExecuteDeleteAsync(ct);
    }
}

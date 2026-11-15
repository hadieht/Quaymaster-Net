using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quaymaster.Application.Abstractions;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Infrastructure.Persistence.Repositories;

internal sealed class UserRepository(IDbContextFactory<AppDbContext> factory) : IUserRepository
{
    private const int SqliteConstraint = 19;

    private readonly SemaphoreSlim _adminGate = new(1, 1);

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.CountAsync(ct);
    }

    public async Task<int> CountActiveAdminsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.CountAsync(u => u.Role == Role.Admin && !u.Disabled, ct);
    }

    public async Task<User?> FindByIdAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public async Task<User?> FindByUsernameAsync(string username, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, ct);
    }

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(ct);
    }

    public async Task<User> AddAsync(User user, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            throw new UsernameTakenException();
        }

        return user;
    }

    public async Task SetPasswordHashAsync(long id, string passwordHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.PasswordHash, passwordHash).SetProperty(u => u.PasswordChangedAt, now), ct);
    }

    public async Task<UserMutation> PatchGuardedAsync(long id, UserPatch patch, CancellationToken ct)
    {
        // One process owns the database (single-instance lock), so an in-process gate makes count + write atomic.
        await _adminGate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
            {
                return new UserMutation(null, false);
            }

            if (LastAdminRule.PatchRemovesAdmin(user, patch)
                && LastAdminRule.PatchViolates(user, patch, await db.Users.CountAsync(u => u.Role == Role.Admin && !u.Disabled, ct)))
            {
                return new UserMutation(user, true);
            }

            var q = db.Users.Where(u => u.Id == id);
            if (patch.Role is { } role)
            {
                await q.ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, role), ct);
            }

            if (patch.LogsRead is { } logsRead)
            {
                await q.ExecuteUpdateAsync(s => s.SetProperty(u => u.LogsRead, logsRead), ct);
            }

            if (patch.Disabled is { } disabled)
            {
                await q.ExecuteUpdateAsync(s => s.SetProperty(u => u.Disabled, disabled), ct);
            }

            return new UserMutation(await db.Users.AsNoTracking().FirstAsync(u => u.Id == id, ct), false);
        }
        finally
        {
            _adminGate.Release();
        }
    }

    public async Task<UserMutation> DeleteGuardedAsync(long id, CancellationToken ct)
    {
        await _adminGate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
            {
                return new UserMutation(null, false);
            }

            if (LastAdminRule.DeleteViolates(user, await db.Users.CountAsync(u => u.Role == Role.Admin && !u.Disabled, ct)))
            {
                return new UserMutation(user, true);
            }

            await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(ct);
            return new UserMutation(user, false);
        }
        finally
        {
            _adminGate.Release();
        }
    }
}

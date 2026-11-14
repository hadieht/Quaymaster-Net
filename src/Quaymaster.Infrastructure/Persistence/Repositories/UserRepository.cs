using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Quaymaster.Application.Abstractions;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Infrastructure.Persistence.Repositories;

internal sealed class UserRepository(IDbContextFactory<AppDbContext> factory) : IUserRepository
{
    private const int SqliteConstraint = 19;

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

    public async Task UpdateAsync(User user, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Users.Update(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(ct);
    }
}

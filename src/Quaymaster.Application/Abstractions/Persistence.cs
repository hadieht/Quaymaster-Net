using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Application.Abstractions;

public interface IUserRepository
{
    Task<int> CountAsync(CancellationToken ct);
    Task<int> CountActiveAdminsAsync(CancellationToken ct);
    Task<User?> FindByIdAsync(long id, CancellationToken ct);

    /// <summary>Case-insensitive (the column is COLLATE NOCASE).</summary>
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct);

    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct);

    /// <summary>Inserts and returns the user with its generated id. Throws <see cref="UsernameTakenException"/> on a duplicate.</summary>
    Task<User> AddAsync(User user, CancellationToken ct);

    Task UpdateAsync(User user, CancellationToken ct);
    Task DeleteAsync(long id, CancellationToken ct);
}

public sealed class UsernameTakenException : Exception
{
    public UsernameTakenException() : base("Username already exists") { }
}

public interface ISessionRepository
{
    Task AddAsync(Session session, CancellationToken ct);
    Task<Session?> FindAsync(string idHash, CancellationToken ct);
    Task TouchAsync(string idHash, DateTimeOffset now, CancellationToken ct);
    Task DeleteAsync(string idHash, CancellationToken ct);

    /// <summary>The user's sessions, most recently seen first.</summary>
    Task<IReadOnlyList<Session>> ListForUserAsync(long userId, CancellationToken ct);

    /// <summary>Deletes every session of a user (optionally keeping one) and returns the removed id hashes.</summary>
    Task<IReadOnlyList<string>> DeleteForUserAsync(long userId, string? exceptIdHash, CancellationToken ct);

    Task<int> DeleteExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout, CancellationToken ct);
}

/// <summary>Append-only: there is no update and no per-row delete, only the retention prune.</summary>
public interface IAuditRepository
{
    Task AddAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>Deletes (in batches) rows older than the cutoff; the database trigger refuses rows younger than 30 days.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct);
}

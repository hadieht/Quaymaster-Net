using Quaymaster.Application.Abstractions;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Identity;

namespace Quaymaster.UnitTests.Fakes;

internal sealed class InMemoryUserRepository : IUserRepository
{
    private readonly List<User> _users = [];
    private long _nextId = 1;

    public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(_users.Count);

    public Task<int> CountActiveAdminsAsync(CancellationToken ct) => Task.FromResult(_users.Count(u => u.IsActiveAdmin));

    public Task<User?> FindByIdAsync(long id, CancellationToken ct) => Task.FromResult(_users.FirstOrDefault(u => u.Id == id));

    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct) =>
        Task.FromResult(_users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<User>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<User>>(_users.ToList());

    public Task<User> AddAsync(User user, CancellationToken ct)
    {
        if (_users.Any(u => string.Equals(u.Username, user.Username, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UsernameTakenException();
        }

        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, _nextId++);
        _users.Add(user);
        return Task.FromResult(user);
    }

    public Task SetPasswordHashAsync(long id, string passwordHash, DateTimeOffset now, CancellationToken ct)
    {
        _users.FirstOrDefault(u => u.Id == id)?.SetPasswordHash(passwordHash, now);
        return Task.CompletedTask;
    }

    public Task<UserMutation> PatchGuardedAsync(long id, UserPatch patch, CancellationToken ct)
    {
        var user = _users.FirstOrDefault(u => u.Id == id);
        if (user is null)
        {
            return Task.FromResult(new UserMutation(null, false));
        }

        if (LastAdminRule.PatchViolates(user, patch, _users.Count(u => u.IsActiveAdmin)))
        {
            return Task.FromResult(new UserMutation(user, true));
        }

        user.Apply(patch);
        return Task.FromResult(new UserMutation(user, false));
    }

    public Task<UserMutation> DeleteGuardedAsync(long id, CancellationToken ct)
    {
        var user = _users.FirstOrDefault(u => u.Id == id);
        if (user is null)
        {
            return Task.FromResult(new UserMutation(null, false));
        }

        if (LastAdminRule.DeleteViolates(user, _users.Count(u => u.IsActiveAdmin)))
        {
            return Task.FromResult(new UserMutation(user, true));
        }

        _users.Remove(user);
        return Task.FromResult(new UserMutation(user, false));
    }
}

internal sealed class InMemorySessionRepository : ISessionRepository
{
    public Dictionary<string, Session> Rows { get; } = [];

    public Task AddAsync(Session session, CancellationToken ct)
    {
        Rows[session.IdHash] = session;
        return Task.CompletedTask;
    }

    public Task<Session?> FindAsync(string idHash, CancellationToken ct) =>
        Task.FromResult(Rows.GetValueOrDefault(idHash));

    public Task TouchAsync(string idHash, DateTimeOffset now, CancellationToken ct)
    {
        Rows[idHash].Touch(now);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string idHash, CancellationToken ct)
    {
        Rows.Remove(idHash);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Session>> ListForUserAsync(long userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Session>>([.. Rows.Values.Where(s => s.UserId == userId).OrderByDescending(s => s.LastSeen)]);

    public Task<IReadOnlyList<string>> DeleteForUserAsync(long userId, string? exceptIdHash, CancellationToken ct)
    {
        var gone = Rows.Values.Where(s => s.UserId == userId && s.IdHash != exceptIdHash).Select(s => s.IdHash).ToList();
        foreach (var hash in gone)
        {
            Rows.Remove(hash);
        }

        return Task.FromResult<IReadOnlyList<string>>(gone);
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, TimeSpan idleTimeout, CancellationToken ct)
    {
        var gone = Rows.Values.Where(s => s.Evaluate(now, idleTimeout) == SessionState.Expired).Select(s => s.IdHash).ToList();
        foreach (var hash in gone)
        {
            Rows.Remove(hash);
        }

        return Task.FromResult(gone.Count);
    }
}

internal sealed class RecordingAuditLog : IAuditLog
{
    public List<AuditRecord> Records { get; } = [];

    public Task RecordAsync(AuditRecord record, CancellationToken ct = default)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task<int> PruneAsync(CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>Cheap deterministic hasher for service tests ("hash:" + password).</summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public Task<string> HashAsync(string password, CancellationToken ct = default) => Task.FromResult("hash:" + password);

    public Task<bool> VerifyAsync(string password, string storedHash, CancellationToken ct = default) =>
        Task.FromResult(storedHash == "hash:" + password);

    public Task DummyVerifyAsync(string password, CancellationToken ct = default) => Task.CompletedTask;

    public bool IsValidHash(string storedHash) => storedHash.StartsWith("hash:", StringComparison.Ordinal);
}

internal static class Outcomes
{
    public static IEnumerable<string> Actions(this RecordingAuditLog log) => log.Records.Select(r => r.Action);

    public static AuditOutcome OutcomeOf(this RecordingAuditLog log, string action) =>
        log.Records.Last(r => r.Action == action).Outcome;
}

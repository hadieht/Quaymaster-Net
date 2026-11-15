using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Application.Users;

public interface IUserService
{
    Task<IReadOnlyList<UserInfoDto>> ListAsync(CancellationToken ct);

    /// <summary>Creates an account (validates name + password strength, hashes). No audit: callers audit in their own words (setup vs. admin create).</summary>
    Task<User> CreateAccountAsync(string username, string password, Role role, bool logsRead, CancellationToken ct);

    /// <summary>Used for the first admin from the environment with an already-computed hash.</summary>
    Task<User> CreateAccountWithHashAsync(string username, string passwordHash, Role role, CancellationToken ct);

    Task<UserInfoDto> CreateAsync(string username, string password, Role role, bool logsRead, Actor actor, CancellationToken ct);

    Task<UserInfoDto> UpdateAsync(long id, UserPatch patch, Actor actor, CancellationToken ct);

    Task DeleteAsync(long id, Actor actor, CancellationToken ct);

    /// <summary>Admin password reset; signs the user out everywhere.</summary>
    Task SetPasswordAsync(long id, string password, Actor actor, CancellationToken ct);
}

public sealed class UserService(
    IUserRepository users,
    ISessionService sessions,
    ISessionRevocationBus bus,
    IPasswordHasher hasher,
    IAuditLog audit,
    TimeProvider time) : IUserService
{
    public async Task<IReadOnlyList<UserInfoDto>> ListAsync(CancellationToken ct) =>
        (await users.ListAsync(ct)).Select(UserInfoDto.From).ToList();

    public async Task<User> CreateAccountAsync(string username, string password, Role role, bool logsRead, CancellationToken ct)
    {
        PasswordPolicy.ValidateUsername(username);
        PasswordPolicy.ValidateStrength(password);
        if (await users.FindByUsernameAsync(username, ct) is not null)
        {
            throw Taken();
        }
        var hash = await hasher.HashAsync(password, ct);
        return await AddAsync(new User(username, hash, role, logsRead, time.GetUtcNow()), ct);
    }

    public Task<User> CreateAccountWithHashAsync(string username, string passwordHash, Role role, CancellationToken ct)
    {
        PasswordPolicy.ValidateUsername(username);
        return AddAsync(new User(username, passwordHash, role, false, time.GetUtcNow()), ct);
    }

    public async Task<UserInfoDto> CreateAsync(string username, string password, Role role, bool logsRead, Actor actor, CancellationToken ct)
    {
        var user = await CreateAccountAsync(username, password, role, logsRead, ct);
        await audit.RecordAsync(
            new AuditRecord(actor.Name, "user.create", AuditOutcome.Success, actor.Ip, user.Username, new { role = user.Role.ToWire() }), ct);
        return UserInfoDto.From(user);
    }

    public async Task<UserInfoDto> UpdateAsync(long id, UserPatch patch, Actor actor, CancellationToken ct)
    {
        var result = await users.PatchGuardedAsync(id, patch, ct);
        var user = result.User ?? throw AppException.NotFound("User");
        if (result.LastAdminViolation)
        {
            throw AppException.Conflict("Cannot demote or disable the last active admin");
        }

        // a privilege change revokes all sessions of the user (and closes their streams)
        if (!patch.IsEmpty)
        {
            await sessions.RevokeForUserAsync(id, null, ct);
        }

        await audit.RecordAsync(
            new AuditRecord(
                actor.Name,
                "user.update",
                AuditOutcome.Success,
                actor.Ip,
                user.Username,
                new { role = patch.Role?.ToWire(), logsRead = patch.LogsRead, disabled = patch.Disabled }),
            ct);
        return UserInfoDto.From(user);
    }

    public async Task DeleteAsync(long id, Actor actor, CancellationToken ct)
    {
        var result = await users.DeleteGuardedAsync(id, ct);
        var user = result.User ?? throw AppException.NotFound("User");
        if (result.LastAdminViolation)
        {
            throw AppException.Conflict("Cannot delete the last active admin");
        }

        bus.Publish(new SessionRevocation(UserId: id));
        await audit.RecordAsync(new AuditRecord(actor.Name, "user.delete", AuditOutcome.Success, actor.Ip, user.Username), ct);
    }

    public async Task SetPasswordAsync(long id, string password, Actor actor, CancellationToken ct)
    {
        PasswordPolicy.ValidateStrength(password);
        _ = await users.FindByIdAsync(id, ct) ?? throw AppException.NotFound("User");
        await users.SetPasswordHashAsync(id, await hasher.HashAsync(password, ct), time.GetUtcNow(), ct);
        await sessions.RevokeForUserAsync(id, null, ct);
        await audit.RecordAsync(new AuditRecord(actor.Name, "user.set_password", AuditOutcome.Success, actor.Ip, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct);
    }

    private async Task<User> AddAsync(User user, CancellationToken ct)
    {
        try
        {
            return await users.AddAsync(user, ct);
        }
        catch (UsernameTakenException)
        {
            throw Taken();
        }
    }

    private static AppException Taken() => AppException.Conflict("Username already exists");
}

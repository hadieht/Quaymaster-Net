using System.Security.Cryptography;
using System.Text;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Security;
using Quaymaster.Domain.Auditing;

namespace Quaymaster.Application.Auth;

public interface IAuthService
{
    Task<(string Token, AuthContext Context)> LoginAsync(string username, string password, string ip, string? userAgent, CancellationToken ct);

    Task LogoutAsync(AuthContext context, Actor actor, CancellationToken ct);

    Task LogoutAllAsync(AuthContext context, Actor actor, CancellationToken ct);

    /// <summary>Verifies the current password, sets the new one and signs out every other session.</summary>
    Task ChangePasswordAsync(AuthContext context, string current, string newPassword, Actor actor, CancellationToken ct);
}

public sealed class AuthService(
    IUserRepository users,
    ISessionService sessions,
    IPasswordHasher hasher,
    ILoginThrottle throttle,
    IAuditLog audit,
    TimeProvider time) : IAuthService
{
    public async Task<(string Token, AuthContext Context)> LoginAsync(
        string username, string password, string ip, string? userAgent, CancellationToken ct)
    {
        // 1. throttle BEFORE hashing
        try
        {
            throttle.BeforeAttempt(username, ip);
        }
        catch (AppException)
        {
            await AuditLoginAsync(username, ip, AuditOutcome.Denied, "rate limited", ct);
            throw;
        }

        var user = await users.FindByUsernameAsync(username, ct);
        var ok = false;
        if (user is { Disabled: false })
        {
            ok = await hasher.VerifyAsync(password, user.PasswordHash, ct);
        }
        else
        {
            await hasher.DummyVerifyAsync(password, ct);
        }

        if (!ok || user is null)
        {
            var delay = throttle.OnFailure(username, ip);
            await AuditLoginAsync(username, ip, AuditOutcome.Failure, "invalid credentials", ct);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, ct);
            }
            throw AppException.Unauthorized("Invalid username or password");
        }

        throttle.OnSuccess(username, ip);
        var created = await sessions.CreateAsync(user, ip, userAgent, ct);
        await audit.RecordAsync(new AuditRecord(user.Username, "auth.login", AuditOutcome.Success, ip), ct);
        return created;
    }

    public async Task LogoutAsync(AuthContext context, Actor actor, CancellationToken ct)
    {
        await sessions.LogoutAsync(context, ct);
        await audit.RecordAsync(new AuditRecord(context.User.Username, "auth.logout", AuditOutcome.Success, actor.Ip), ct);
    }

    public async Task LogoutAllAsync(AuthContext context, Actor actor, CancellationToken ct)
    {
        await sessions.RevokeForUserAsync(context.User.Id, null, ct);
        await audit.RecordAsync(new AuditRecord(context.User.Username, "auth.logout_all", AuditOutcome.Success, actor.Ip), ct);
    }

    public async Task ChangePasswordAsync(AuthContext context, string current, string newPassword, Actor actor, CancellationToken ct)
    {
        PasswordPolicy.ValidateStrength(newPassword);

        // The "current password" check is a password oracle for anyone holding a session cookie: throttle it
        // per (account, session) with the same limiter as login.
        var key = "pw:" + context.Session.IdHash;
        throttle.BeforeAttempt(context.User.Username, key);
        if (!await hasher.VerifyAsync(current, context.User.PasswordHash, ct))
        {
            var delay = throttle.OnFailure(context.User.Username, key);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, ct);
            }
            throw AppException.Forbidden("Current password is incorrect");
        }

        throttle.OnSuccess(context.User.Username, key);
        var user = await users.FindByIdAsync(context.User.Id, ct) ?? throw AppException.Unauthorized();
        user.SetPasswordHash(await hasher.HashAsync(newPassword, ct), time.GetUtcNow());
        await users.UpdateAsync(user, ct);
        await sessions.RevokeForUserAsync(user.Id, context.Session.IdHash, ct);
        await audit.RecordAsync(new AuditRecord(user.Username, "auth.password_change", AuditOutcome.Success, actor.Ip), ct);
    }

    /// <summary>Never store the raw username of a failed login (people type passwords there).</summary>
    private async Task AuditLoginAsync(string username, string ip, AuditOutcome outcome, string why, CancellationToken ct)
    {
        var known = await users.FindByUsernameAsync(username, ct);
        var actor = known is not null
            ? known.Username
            : "unknown:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(username.ToLowerInvariant())))[..12];
        await audit.RecordAsync(new AuditRecord(actor, "auth.login", outcome, ip, Params: new { reason = why }), ct);
    }
}

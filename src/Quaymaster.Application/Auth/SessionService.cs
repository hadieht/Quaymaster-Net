using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Configuration;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Application.Auth;

/// <summary>An authenticated request: the account and the session that proved it.</summary>
public sealed record AuthContext(User User, Session Session);

public interface ISessionService
{
    /// <summary>Creates a brand-new session (new random id every time: rotation on login). Returns the cookie token.</summary>
    Task<(string Token, AuthContext Context)> CreateAsync(User user, string? ip, string? userAgent, CancellationToken ct);

    /// <summary>Resolves a cookie token; expired sessions are deleted on the spot. Null = not authenticated.</summary>
    Task<AuthContext?> AuthenticateAsync(string? token, CancellationToken ct);

    /// <summary>Re-validation for long-lived streams; does not extend the idle timer.</summary>
    Task<bool> IsStillValidAsync(string idHash, Role expectedRole, CancellationToken ct);

    string CsrfToken(AuthContext context);

    bool VerifyCsrf(AuthContext context, string? provided);

    SessionInfoDto ToInfo(AuthContext context);

    Task LogoutAsync(AuthContext context, CancellationToken ct);

    /// <summary>Deletes every session of the user. <paramref name="except"/> keeps one (password change).</summary>
    Task RevokeForUserAsync(long userId, string? except, CancellationToken ct);

    Task PurgeExpiredAsync(CancellationToken ct);
}

public sealed class SessionService(
    ISessionRepository sessions,
    IUserRepository users,
    ISessionRevocationBus bus,
    SessionOptions options,
    TimeProvider time) : ISessionService
{
    private const int MaxTokenLength = 128;

    /// <summary>SHA-256 hex of the cookie token: the only form in which a session id is stored.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<(string Token, AuthContext Context)> CreateAsync(User user, string? ip, string? userAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var session = new Session(
            HashToken(token),
            user.Id,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            time.GetUtcNow(),
            options.AbsoluteLifetime,
            ip,
            userAgent is { Length: > 200 } ? userAgent[..200] : userAgent);
        await sessions.AddAsync(session, ct);
        return (token, new AuthContext(user, session));
    }

    public async Task<AuthContext?> AuthenticateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return null;
        }
        var idHash = HashToken(token);
        var session = await sessions.FindAsync(idHash, ct);
        if (session is null)
        {
            return null;
        }
        var now = time.GetUtcNow();
        if (session.Evaluate(now, options.IdleTimeout) == SessionState.Expired)
        {
            await sessions.DeleteAsync(idHash, ct);
            bus.Publish(new SessionRevocation([idHash]));
            return null;
        }

        var user = await users.FindByIdAsync(session.UserId, ct);
        if (user is null || user.Disabled)
        {
            return null;
        }
        if (session.NeedsTouch(now))
        {
            await sessions.TouchAsync(idHash, now, ct);
            session.Touch(now);
        }

        return new AuthContext(user, session);
    }

    public async Task<bool> IsStillValidAsync(string idHash, Role expectedRole, CancellationToken ct)
    {
        var session = await sessions.FindAsync(idHash, ct);
        if (session is null || time.GetUtcNow() >= session.ExpiresAt)
        {
            return false;
        }
        var user = await users.FindByIdAsync(session.UserId, ct);
        return user is { Disabled: false } && user.Role == expectedRole;
    }

    /// <summary>HMAC-SHA256(key = per-session secret, message = session id hash), hex.</summary>
    public string CsrfToken(AuthContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(context.Session.CsrfSecret), Encoding.UTF8.GetBytes(context.Session.IdHash));
        return Convert.ToHexStringLower(mac);
    }

    public bool VerifyCsrf(AuthContext context, string? provided)
    {
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }
        var expected = Encoding.UTF8.GetBytes(CsrfToken(context));
        var got = Encoding.UTF8.GetBytes(provided);
        return got.Length == expected.Length && CryptographicOperations.FixedTimeEquals(got, expected);
    }

    public SessionInfoDto ToInfo(AuthContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SessionInfoDto(
            UserInfoDto.From(context.User),
            CsrfToken(context),
            Iso.Format(context.Session.ExpiresAt),
            Iso.Format(context.Session.IdleExpiresAt(options.IdleTimeout)));
    }

    public async Task LogoutAsync(AuthContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        await sessions.DeleteAsync(context.Session.IdHash, ct);
        bus.Publish(new SessionRevocation([context.Session.IdHash]));
    }

    public async Task RevokeForUserAsync(long userId, string? except, CancellationToken ct)
    {
        var gone = await sessions.DeleteForUserAsync(userId, except, ct);
        if (except is null)
        {
            bus.Publish(new SessionRevocation(UserId: userId));
        }
        else if (gone.Count > 0)
        {
            bus.Publish(new SessionRevocation(gone));
        }
    }

    public async Task PurgeExpiredAsync(CancellationToken ct)
    {
        var removed = await sessions.DeleteExpiredAsync(time.GetUtcNow(), options.IdleTimeout, ct);
        if (removed > 0)
        {
            bus.Publish(new SessionRevocation());
        }
    }
}

namespace Quaymaster.Domain.Identity;

public enum SessionState
{
    Valid,
    Expired,
}

/// <summary>
/// A server-side login session. Only the SHA-256 of the cookie token is stored (<see cref="IdHash"/>).
/// Lifetime rules: absolute expiry (<see cref="ExpiresAt"/>) and an idle timeout measured from <see cref="LastSeen"/>.
/// </summary>
public sealed class Session
{
    /// <summary>LastSeen is only persisted when it is older than this, to avoid a write per request.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(30);

    private Session() { }

    public Session(
        string idHash,
        long userId,
        string csrfSecret,
        DateTimeOffset now,
        TimeSpan absoluteLifetime,
        string? ip,
        string? userAgent)
    {
        IdHash = idHash;
        UserId = userId;
        CsrfSecret = csrfSecret;
        CreatedAt = now;
        LastSeen = now;
        ExpiresAt = now + absoluteLifetime;
        Ip = ip;
        UserAgent = userAgent;
    }

    public string IdHash { get; private set; } = "";
    public long UserId { get; private set; }
    public string CsrfSecret { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeen { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public string? Ip { get; private set; }
    public string? UserAgent { get; private set; }

    public SessionState Evaluate(DateTimeOffset now, TimeSpan idleTimeout) =>
        now >= ExpiresAt || now - LastSeen >= idleTimeout ? SessionState.Expired : SessionState.Valid;

    public bool NeedsTouch(DateTimeOffset now) => now - LastSeen > TouchInterval;

    public void Touch(DateTimeOffset now) => LastSeen = now;

    /// <summary>The moment the session dies if the user does nothing: the earlier of absolute and idle expiry.</summary>
    public DateTimeOffset IdleExpiresAt(TimeSpan idleTimeout)
    {
        var idle = LastSeen + idleTimeout;
        return idle < ExpiresAt ? idle : ExpiresAt;
    }
}

namespace Quaymaster.Application.Security;

/// <summary>The exact browser origins accepted on state-changing requests.</summary>
public sealed class AllowedOrigins
{
    private readonly HashSet<string> _origins;

    private AllowedOrigins(HashSet<string> origins) => _origins = origins;

    public IReadOnlyCollection<string> Origins => _origins;

    public bool Contains(string origin) => _origins.Contains(origin);

    /// <summary>
    /// QM_PUBLIC_ORIGIN when set; otherwise derived from the bind address: <c>http://host:port</c>, plus the
    /// loopback aliases when bound to loopback / any-address.
    /// </summary>
    public static AllowedOrigins From(IReadOnlyList<string> publicOrigins, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(publicOrigins);
        if (publicOrigins.Count > 0)
        {
            return new AllowedOrigins(new HashSet<string>(publicOrigins, StringComparer.Ordinal));
        }
        var set = new HashSet<string>(StringComparer.Ordinal) { $"http://{FormatHost(host)}:{port}" };
        if (host is "127.0.0.1" or "localhost" or "::1" or "0.0.0.0" or "::")
        {
            set.Add($"http://127.0.0.1:{port}");
            set.Add($"http://localhost:{port}");
        }

        return new AllowedOrigins(set);
    }

    private static string FormatHost(string host) => host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
}

public enum MutationDecision
{
    Allowed,
    OriginRejected,
    CsrfRejected,
}

/// <summary>
/// Pure CSRF/Origin decision for one request. Safe methods are exempt (and must never have side effects).
/// Everything else needs an exact Origin match; requests carrying a session also need the session-bound
/// token in <c>X-QM-CSRF</c>.
/// </summary>
public static class MutationGuard
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    /// <param name="session">True when the request must present a CSRF token (a session exists and the route is not public).</param>
    /// <param name="verifyCsrf">Constant-time check of the provided token against the session's token.</param>
    public static MutationDecision Evaluate(
        string method,
        string? origin,
        AllowedOrigins allowed,
        string? csrfHeader,
        bool session,
        Func<string?, bool> verifyCsrf)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(verifyCsrf);
        if (SafeMethods.Contains(method))
        {
            return MutationDecision.Allowed;
        }
        if (string.IsNullOrEmpty(origin) || !allowed.Contains(origin))
        {
            return MutationDecision.OriginRejected;
        }
        if (session && !verifyCsrf(csrfHeader))
        {
            return MutationDecision.CsrfRejected;
        }
        return MutationDecision.Allowed;
    }
}

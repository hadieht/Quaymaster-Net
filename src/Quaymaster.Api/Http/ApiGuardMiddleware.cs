using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Security;

namespace Quaymaster.Api.Http;

/// <summary>
/// Authentication + CSRF/Origin gate for everything under <c>/api/</c>, fail-closed:
/// <list type="number">
/// <item>The path is canonicalised; anything that is, or is routed to, an <c>/api/</c> route is an API request.</item>
/// <item>Non-public API requests need a valid session (401 otherwise). Public ones (status, setup, login) only
/// pick up a session if a cookie is present.</item>
/// <item>State-changing requests (not GET/HEAD/OPTIONS) need an exact Origin match; authenticated ones also the
/// session-bound <c>X-QM-CSRF</c> token (403 <c>origin</c> / <c>csrf</c>).</item>
/// </list>
/// </summary>
public sealed class ApiGuardMiddleware(
    RequestDelegate next,
    ISessionService sessions,
    SessionCookie cookie,
    AllowedOrigins origins)
{
    public static readonly IReadOnlySet<string> PublicPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        "/healthz",
        "/api/v1/auth/status",
        "/api/v1/auth/login",
        "/api/v1/auth/setup",
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = CanonicalPath.Of(context.Request);
        var routed = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? string.Empty;
        var isApi = CanonicalPath.IsApi(path) || routed.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        if (!isApi)
        {
            await next(context);
            return;
        }

        var isPublic = PublicPaths.Contains(path);
        var token = cookie.Read(context.Request);
        var auth = isPublic && token is null ? null : await sessions.AuthenticateAsync(token, context.RequestAborted);
        if (auth is null && !isPublic)
        {
            throw AppException.Unauthorized();
        }

        context.SetAuth(auth);

        // public endpoints (login/setup) have no session yet: Origin is enough there
        var decision = MutationGuard.Evaluate(
            context.Request.Method,
            context.Request.Headers.Origin.FirstOrDefault(),
            origins,
            context.Request.Headers["X-QM-CSRF"].FirstOrDefault(),
            !isPublic,
            provided => auth is not null && sessions.VerifyCsrf(auth, provided));
        switch (decision)
        {
            case MutationDecision.OriginRejected:
                throw new AppException(403, ErrorCode.Origin, "Missing or unexpected Origin header");
            case MutationDecision.CsrfRejected:
                throw new AppException(403, ErrorCode.Csrf, "Missing or invalid X-QM-CSRF token");
        }

        await next(context);
    }
}

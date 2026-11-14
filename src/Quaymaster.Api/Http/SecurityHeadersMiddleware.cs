using Quaymaster.Application.Configuration;

namespace Quaymaster.Api.Http;

/// <summary>
/// Adds the original server's response headers to every response: strict CSP (no inline script/style), clickjacking,
/// sniffing, referrer and permission lockdowns, HSTS when served over HTTPS (QM_COOKIE_SECURE=true), and
/// <c>Cache-Control: no-store</c> for the API and the health probe.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, QmConfig config)
{
    public static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>
    {
        ["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'",
        ["X-Frame-Options"] = "DENY",
        ["X-Content-Type-Options"] = "nosniff",
        ["Referrer-Policy"] = "no-referrer",
        ["Cross-Origin-Resource-Policy"] = "same-origin",
        ["Cross-Origin-Opener-Policy"] = "same-origin",
        ["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()",
    };

    public const string Hsts = "max-age=15552000; includeSubDomains";

    public Task InvokeAsync(HttpContext context)
    {
        Apply(context, config.CookieSecure);
        return next(context);
    }

    internal static void Apply(HttpContext context, bool secure)
    {
        var headers = context.Response.Headers;
        foreach (var (name, value) in Headers)
        {
            headers[name] = value;
        }

        if (secure)
        {
            headers["Strict-Transport-Security"] = Hsts;
        }

        var path = CanonicalPath.Of(context.Request);
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path == "/healthz")
        {
            headers["Cache-Control"] = "no-store";
        }
    }
}

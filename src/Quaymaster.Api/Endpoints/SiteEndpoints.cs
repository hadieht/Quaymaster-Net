using System.Text.RegularExpressions;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Errors;

namespace Quaymaster.Api.Endpoints;

/// <summary>Health probe, the unknown-route envelope and the SPA fallback.</summary>
public static partial class SiteEndpoints
{
    /// <summary>The built UI directory, or null when it has not been built (API-only mode).</summary>
    public static string? ResolveWebDir(QmConfig config, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(config);
        var dir = config.WebDir ?? Path.Combine(contentRoot, "web", "dist");
        return File.Exists(Path.Combine(dir, "index.html")) ? dir : null;
    }

    public static void Map(IEndpointRouteBuilder app, string? webDir)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Contract: exactly { "status": "ok" }. Public, no details.
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

        // Unknown API route (any method): the envelope, never the SPA.
        app.MapFallback("/api/{**rest}", (HttpContext _) => RouteNotFound());

        // Client-side routes: GET without a file extension gets index.html; everything else is a 404 envelope.
        app.MapFallback("{**path}", (HttpContext http) => Spa(http, webDir));
    }

    private static IResult Spa(HttpContext http, string? webDir)
    {
        var path = http.Request.Path.Value ?? "/";
        if (webDir is null || !HttpMethods.IsGet(http.Request.Method) || FileExtension().IsMatch(path))
        {
            return RouteNotFound();
        }

        http.Response.Headers.CacheControl = "no-cache";
        return Results.File(Path.Combine(webDir, "index.html"), "text/html; charset=utf-8");
    }

    private static IResult RouteNotFound() => throw new AppException(404, ErrorCode.NotFound, "Route not found");

    [GeneratedRegex(@"\.[a-z0-9]{1,8}$", RegexOptions.IgnoreCase)]
    private static partial Regex FileExtension();
}

namespace Quaymaster.Api.Http;

/// <summary>
/// The request path as the router sees it: percent-escapes decoded (bounded, so double-encoding cannot hide a prefix),
/// backslashes turned into slashes and leading slash runs collapsed. Auth decisions MUST use this, never the raw
/// request line (audit C-01: <c>/%61pi/v1/...</c> is routed to the API but does not start with "/api").
/// Dot segments are deliberately NOT collapsed: the router does not collapse them either.
/// </summary>
public static class CanonicalPath
{
    private const int MaxDecodeRounds = 3;

    public static string Of(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Normalize(request.Path.Value ?? "/");
    }

    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var p = path.Replace('\\', '/');
        for (var i = 0; i < MaxDecodeRounds && p.Contains('%', StringComparison.Ordinal); i++)
        {
            p = Uri.UnescapeDataString(p).Replace('\\', '/');
        }

        return "/" + p.TrimStart('/');
    }

    public static bool IsApi(string canonicalPath) => canonicalPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
}

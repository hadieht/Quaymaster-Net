using System.Net;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;

namespace Quaymaster.Api.Http;

/// <summary>Per-request facts the pipeline computes once and endpoints read.</summary>
public static class RequestContext
{
    private const string AuthKey = "qm.auth";

    public static void SetAuth(this HttpContext context, AuthContext? auth) => context.Items[AuthKey] = auth;

    public static AuthContext? GetAuth(this HttpContext context) =>
        context.Items.TryGetValue(AuthKey, out var value) ? value as AuthContext : null;

    /// <summary>The authenticated caller, or a 401 (defence in depth: the guard middleware already enforces this).</summary>
    public static AuthContext RequireAuth(this HttpContext context) =>
        context.GetAuth() ?? throw AppException.Unauthorized();

    /// <summary>Client address as the rate limiter and audit log see it (after trusted-proxy processing).</summary>
    public static string ClientIp(this HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    public static Actor ActorOf(this HttpContext context) =>
        new(context.GetAuth()?.User.Username ?? "anonymous", context.ClientIp());

    public static string? UserAgent(this HttpContext context) => context.Request.Headers.UserAgent.FirstOrDefault();

    /// <summary>Parses the Cookie header: first occurrence of a name wins, malformed escapes are skipped.</summary>
    public static IReadOnlyDictionary<string, string> Cookies(string? header)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(header))
        {
            return result;
        }

        foreach (var part in header.Split(';'))
        {
            var i = part.IndexOf('=', StringComparison.Ordinal);
            if (i < 0)
            {
                continue;
            }

            var name = part[..i].Trim();
            if (name.Length == 0 || result.ContainsKey(name))
            {
                continue;
            }

            result[name] = WebUtility.UrlDecode(part[(i + 1)..].Trim().Replace("+", "%2B", StringComparison.Ordinal));
        }

        return result;
    }
}

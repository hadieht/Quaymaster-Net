using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Notifications;

/// <summary>Why an outbound URL / address is refused. The message never echoes the full URL (it is a secret as a whole).</summary>
public sealed class OutboundPolicyException(string message) : Exception(message);

/// <summary>
/// The single outbound HTTP client policy of the original (RT-21), as a pure decision: http/https only, no userinfo,
/// unspecified / multicast / broadcast / link-local destinations refused (cloud metadata!), loopback and RFC1918
/// ranges ALLOWED (home servers, self-hosted ntfy/gotify). Redirects only to the same host, at most 3.
/// </summary>
public static partial class OutboundPolicy
{
    public const int DefaultTimeoutMs = 5000;
    public const int MaxResponseBytes = 64 * 1024;
    public const int MaxRedirects = 3;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemeStart();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex Octet();

    /// <summary>Strict dotted-quad parse (what <c>net.isIP</c> accepts as IPv4).</summary>
    public static byte[]? ParseV4(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4)
        {
            return null;
        }

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (!Octet().IsMatch(parts[i]) || !int.TryParse(parts[i], out var n) || n > 255)
            {
                return null;
            }

            bytes[i] = (byte)n;
        }

        return bytes;
    }

    private static string? CheckV4(IReadOnlyList<int> b)
    {
        var (a, c) = (b[0], b[1]);
        if (a == 0)
        {
            return "unspecified";
        }

        if (a == 169 && c == 254)
        {
            return "link-local (cloud metadata range)";
        }

        if (a >= 224)
        {
            return a == 255 ? "broadcast" : "multicast";
        }

        return null;
    }

    /// <summary>Returns why an IP literal must not be contacted, or null when it is allowed.</summary>
    public static string? DisallowedAddressReason(string ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        var v4 = ParseV4(ip);
        if (v4 is not null)
        {
            return CheckV4([.. v4.Select(x => (int)x)]);
        }

        var bare = ip.Split('%')[0];
        if (bare.Contains(':', StringComparison.Ordinal) && IPAddress.TryParse(bare, out var addr) && addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = addr.GetAddressBytes();
            var g = new int[8];
            for (var i = 0; i < 8; i++)
            {
                g[i] = (bytes[2 * i] << 8) | bytes[(2 * i) + 1];
            }

            if (g.All(x => x == 0))
            {
                return "unspecified";
            }

            if (g.Take(5).All(x => x == 0) && g[5] == 0xffff)
            {
                return CheckV4([(g[6] >> 8) & 255, g[6] & 255, (g[7] >> 8) & 255, g[7] & 255]);
            }

            if ((g[0] & 0xffc0) == 0xfe80)
            {
                return "link-local";
            }

            if ((g[0] & 0xff00) == 0xff00)
            {
                return "multicast";
            }

            return null;
        }

        return "not an IP address";
    }

    public static bool IsIpLiteral(string host) => ParseV4(host) is not null || (host.Contains(':', StringComparison.Ordinal) && IPAddress.TryParse(host.Split('%')[0], out _));

    /// <summary>Validates scheme, userinfo and literal-IP hosts. Throws <see cref="OutboundPolicyException"/> without echoing the URL.</summary>
    public static Uri Validate(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        // a bare "/path" is an absolute FILE uri on Unix; WHATWG `new URL("/path")` throws -> "Invalid URL"
        if (!SchemeStart().IsMatch(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            throw new OutboundPolicyException("Invalid URL");
        }

        if (u.Scheme is not ("http" or "https"))
        {
            throw new OutboundPolicyException("Only http and https URLs are allowed");
        }

        if (!string.IsNullOrEmpty(u.UserInfo))
        {
            throw new OutboundPolicyException("URLs with embedded credentials (user:password@) are not allowed");
        }

        if (string.IsNullOrEmpty(u.Host))
        {
            throw new OutboundPolicyException("URL has no host");
        }

        var host = u.Host.TrimStart('[').TrimEnd(']');
        if (IsIpLiteral(host))
        {
            var why = DisallowedAddressReason(host);
            if (why is not null)
            {
                throw new OutboundPolicyException($"Destination address {host} is not allowed ({why})");
            }
        }

        return u;
    }

    /// <summary><c>scheme://host/••••</c> (userinfo dropped); notification URLs are secrets as a whole.</summary>
    public static string MaskToSchemeHost(string url, string mask)
    {
        var m = UrlParts().Match(url);
        if (!m.Success)
        {
            return mask;
        }

        var host = Regex.Replace(m.Groups[3].Value, "^.*@", string.Empty);
        return $"{m.Groups[1].Value}{host}/{mask}";
    }

    [GeneratedRegex(@"^([a-z][a-z0-9+.-]*://)([^/?#]*@)?([^/?#]*)([^?#]*)(\?[^#]*)?(#.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlParts();
}

using Quaymaster.Application.Configuration;

namespace Quaymaster.Api.Http;

/// <summary>The session cookie: <c>qm_session</c>, or <c>__Host-qm_session</c> with <c>Secure</c> over HTTPS.</summary>
public sealed class SessionCookie(QmConfig config)
{
    public string Name => config.CookieName;

    public string? Read(HttpRequest request) =>
        RequestContext.Cookies(request.Headers.Cookie.ToString()).TryGetValue(Name, out var token) ? token : null;

    public void Set(HttpResponse response, string token)
    {
        var maxAge = (long)config.Session.AbsoluteLifetime.TotalSeconds;
        response.Headers.Append("Set-Cookie", Build(token, maxAge));
    }

    public void Clear(HttpResponse response) => response.Headers.Append("Set-Cookie", Build(string.Empty, 0));

    private string Build(string value, long maxAge) =>
        $"{Name}={value}; Path=/; HttpOnly; SameSite=Strict; Max-Age={maxAge}{(config.CookieSecure ? "; Secure" : string.Empty)}";
}

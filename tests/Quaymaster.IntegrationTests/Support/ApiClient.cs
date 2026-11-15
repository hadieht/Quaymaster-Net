using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Quaymaster.IntegrationTests.Support;

public sealed record ApiResponse(int Status, HttpResponseHeaders Headers, HttpContentHeaders ContentHeaders, string Body)
{
    public JsonElement Json => JsonElement.Parse(Body);

    public string? Header(string name) =>
        Headers.TryGetValues(name, out var values) || ContentHeaders.TryGetValues(name, out values) ? values.First() : null;

    public string ErrorCode => Json.GetProperty("error").GetProperty("code").GetString()!;

    public string ErrorMessage => Json.GetProperty("error").GetProperty("message").GetString()!;

    public IReadOnlyList<(string Path, string Message)> Details =>
        Json.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(d => (d.GetProperty("path").GetString()!, d.GetProperty("message").GetString()!)).ToList();

    public string? SetCookie => Headers.TryGetValues("Set-Cookie", out var v) ? v.FirstOrDefault() : null;
}

/// <summary>A browser-like client: remembers the session cookie and CSRF token, sends the Origin header.</summary>
public sealed class ApiClient(HttpClient http, QmFactory factory)
{
    private const string Base = "/api/v1";

    public string? Cookie { get; set; }

    public string? Csrf { get; set; }

    public string CookieName { get; set; } = "qm_session";

    public string Origin { get; set; } = QmFactory.Origin;

    public QmFactory Factory => factory;

    public Task<ApiResponse> GetAsync(string path, bool auth = true) => SendAsync(HttpMethod.Get, path, auth: auth);

    public Task<ApiResponse> PostAsync(string path, object? body = null, bool auth = true, bool origin = true, bool csrf = true) =>
        SendAsync(HttpMethod.Post, path, body, auth, origin, csrf);

    public async Task<ApiResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        bool auth = true,
        bool origin = true,
        bool csrf = true,
        string? rawBody = null,
        string contentType = "application/json",
        string? csrfOverride = null,
        string? originOverride = null)
    {
        // "/api/..." and "~/..." are absolute; everything else is relative to /api/v1
        var url = path.StartsWith("/api/", StringComparison.Ordinal) ? path : path.StartsWith('~') ? path[1..] : Base + path;
        using var request = new HttpRequestMessage(method, url);
        if (rawBody is not null || body is not null)
        {
            request.Content = new StringContent(rawBody ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (auth && Cookie is not null)
        {
            request.Headers.Add("Cookie", $"{CookieName}={Cookie}");
        }

        if (origin)
        {
            request.Headers.Add("Origin", originOverride ?? Origin);
        }

        if (csrf && (csrfOverride ?? Csrf) is { } token)
        {
            request.Headers.Add("X-QM-CSRF", token);
        }

        using var response = await http.SendAsync(request);
        return new ApiResponse((int)response.StatusCode, response.Headers, response.Content.Headers, await response.Content.ReadAsStringAsync());
    }

    private void Adopt(ApiResponse response)
    {
        if (response.Status != 200)
        {
            return;
        }

        if (response.SetCookie is { } cookie)
        {
            var pair = cookie.Split(';')[0];
            CookieName = pair[..pair.IndexOf('=', StringComparison.Ordinal)];
            Cookie = pair[(pair.IndexOf('=', StringComparison.Ordinal) + 1)..];
        }

        Csrf = response.Json.GetProperty("csrfToken").GetString();
    }

    public async Task<ApiResponse> SetupAsync(string username, string password, string? token = null)
    {
        var response = await PostAsync("/auth/setup", new { token = token ?? factory.ReadSetupToken(), username, password }, auth: false);
        Adopt(response);
        return response;
    }

    public async Task<ApiResponse> LoginAsync(string username, string password)
    {
        var response = await PostAsync("/auth/login", new { username, password }, auth: false);
        Adopt(response);
        return response;
    }

    public void SignedOut()
    {
        Cookie = null;
        Csrf = null;
    }
}

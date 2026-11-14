using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quaymaster.Application.Registry;
using Quaymaster.Domain.Registry;

namespace Quaymaster.Infrastructure.Registry;

public sealed record RegistryClientOptions
{
    /// <summary>http (instead of https) is allowed for these hosts; loopback is always allowed (QM_REGISTRY_INSECURE).</summary>
    public IReadOnlyList<string> InsecureHosts { get; init; } = [];

    /// <summary>Extra token-realm hosts per registry host, in addition to the built-in ones (QM_REGISTRY_TOKEN_HOSTS).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RealmAllow { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Minimum gap between two requests to one registry host.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Random jitter added before each request (ms).</summary>
    public Func<int> JitterMs { get; init; } = () => Random.Shared.Next(0, 150);

    /// <summary>Maximum automatic retries on HTTP 429.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Retry-After (and backoff) cap.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Docker Registry v2 client: manifest digest by HEAD with token authentication (port of <c>registry/client.ts</c>).
/// <list type="bullet">
/// <item>credentials are sent only to the registry itself or to an allow-listed token realm, and only over https (loopback /
/// explicitly insecure hosts may use http) - RT-20</item>
/// <item>per-host throttle with jitter, serialised requests, honours <c>Retry-After</c> on 429 and then backs the host off so a
/// rate-limited registry is not hammered</item>
/// <item>redirects are never followed: a manifest request is not redirected legitimately</item>
/// </list>
/// </summary>
public sealed partial class RegistryClient : IRegistryClient
{
    /// <summary>Media types accepted so the registry returns the manifest/index as published (RT-20).</summary>
    public const string ManifestAccept =
        "application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, "
        + "application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json";

    /// <summary>Token realms that legitimately live on another host than the registry (RT-20).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRealmAllow = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["registry-1.docker.io"] = ["auth.docker.io"],
        ["ghcr.io"] = ["ghcr.io"],
        ["quay.io"] = ["quay.io"],
        ["lscr.io"] = ["ghcr.io"],
        ["registry.gitlab.com"] = ["gitlab.com"],
        ["public.ecr.aws"] = ["public.ecr.aws"],

        // docker.n8n.io is fronted by Docker Hub and hands out tokens from Docker Hub's auth service
        ["docker.n8n.io"] = ["auth.docker.io"],
    };

    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<RegistryCredential?>> _credentials;
    private readonly RegistryClientOptions _o;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, IReadOnlyList<string>> _realms;
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequest = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _backoffUntil = new(StringComparer.OrdinalIgnoreCase);

    public RegistryClient(
        HttpClient http,
        RegistryClientOptions options,
        Func<string, CancellationToken, Task<RegistryCredential?>> credentials,
        TimeProvider time,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _o = options;
        _credentials = credentials;
        _time = time;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _realms = MergeRealmAllow(DefaultRealmAllow, options.RealmAllow);
    }

    /// <summary>Number of HTTP requests issued (tests assert on throttling/caching).</summary>
    public int Requests => Volatile.Read(ref _requests);

    private int _requests;

    /// <summary>Union per registry: configured token hosts add to the built-in ones, they never replace them.</summary>
    public static Dictionary<string, IReadOnlyList<string>> MergeRealmAllow(IReadOnlyDictionary<string, IReadOnlyList<string>> baseMap, IReadOnlyDictionary<string, IReadOnlyList<string>> extra)
    {
        ArgumentNullException.ThrowIfNull(baseMap);
        ArgumentNullException.ThrowIfNull(extra);
        var output = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in baseMap)
        {
            output[k] = [.. v];
        }

        foreach (var (k, v) in extra)
        {
            output[k] = [.. (output.TryGetValue(k, out var existing) ? existing : []).Union(v, StringComparer.OrdinalIgnoreCase)];
        }

        return output;
    }

    public static bool IsLoopbackHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var h = PortRe().Replace(host, string.Empty).Trim('[', ']');
        return h == "localhost" || h == "::1" || Loopback4Re().IsMatch(h);
    }

    private bool IsInsecure(string host) => IsLoopbackHost(host) || _o.InsecureHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    private string BaseUrl(string host) => $"{(IsInsecure(host) ? "http" : "https")}://{host}";

    public DateTimeOffset? BackedOffUntil(ImageRef image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return _backoffUntil.TryGetValue(ImageReferences.ApiHost(image), out var until) && until > _time.GetUtcNow() ? until : null;
    }

    public async Task<ManifestHead> HeadManifestAsync(ImageRef image, string? reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        var r = reference ?? image.Digest ?? image.Tag ?? "latest";
        using var res = await RequestAsync(image, $"/v2/{image.Repo}/manifests/{r}", HttpMethod.Head, ct);
        if (res.Headers.TryGetValues("Docker-Content-Digest", out var values) && values.FirstOrDefault() is { } digest && ImageReferences.IsDigest(digest))
        {
            return new ManifestHead(digest, res.Content.Headers.ContentType?.MediaType);
        }

        // some registries omit the header on HEAD: fall back to a GET and hash the body
        var doc = await GetManifestAsync(image, r, ct);
        return new ManifestHead(doc.Digest, doc.MediaType);
    }

    public async Task<ManifestDoc> GetManifestAsync(ImageRef image, string? reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        var r = reference ?? image.Digest ?? image.Tag ?? "latest";
        using var res = await RequestAsync(image, $"/v2/{image.Repo}/manifests/{r}", HttpMethod.Get, ct);
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 4 * 1024 * 1024)
        {
            throw new RegistryException("invalid", "manifest too large");
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new RegistryException("invalid", "registry returned a manifest that is not JSON");
        }

        using (json)
        {
            var root = json.RootElement;
            var header = res.Headers.TryGetValues("Docker-Content-Digest", out var hv) ? hv.FirstOrDefault() : null;
            var digest = header is not null && ImageReferences.IsDigest(header) ? header : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            var manifests = new List<ManifestPlatform>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("manifests", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in ms.EnumerateArray())
                {
                    var platform = m.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;
                    manifests.Add(new ManifestPlatform(
                        m.TryGetProperty("digest", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                        platform is { } pl && pl.TryGetProperty("os", out var os) ? os.GetString() : null,
                        platform is { } pl2 && pl2.TryGetProperty("architecture", out var arch) ? arch.GetString() : null));
                }
            }

            var configSize = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("size", out var cs) && cs.TryGetInt64(out var cv) ? cv : 0;
            long layers = 0;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("layers", out var ls) && ls.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in ls.EnumerateArray())
                {
                    layers += l.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sv) ? sv : 0;
                }
            }

            var mediaType = res.Content.Headers.ContentType?.MediaType
                ?? (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("mediaType", out var mt) ? mt.GetString() : null)
                ?? string.Empty;
            return new ManifestDoc(digest, mediaType, manifests, configSize, layers, bytes.Length);
        }
    }

    public async Task<long> CompressedSizeAsync(ImageRef image, string digest, CancellationToken ct)
    {
        var doc = await GetManifestAsync(image, digest, ct);
        if (doc.Manifests.Count > 0)
        {
            var m = doc.Manifests.FirstOrDefault(x => x.Os == "linux" && x.Architecture == "amd64") ?? doc.Manifests[0];
            doc = await GetManifestAsync(image, m.Digest, ct);
        }

        return doc.LayersSize + doc.ConfigSize;
    }

    // ---- internals -------------------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> RequestAsync(ImageRef image, string path, HttpMethod method, CancellationToken ct)
    {
        var host = ImageReferences.ApiHost(image);
        var gate = _gates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_backoffUntil.TryGetValue(host, out var until) && _time.GetUtcNow() < until)
            {
                throw new RegistryException("rate_limited", $"{host} is rate limiting us; backing off for {Math.Ceiling((until - _time.GetUtcNow()).TotalSeconds)}s", 429);
            }

            var attempt = 0;
            while (true)
            {
                await ThrottleAsync(host, ct);
                var res = await OnceAsync(image, host, path, method, ct);
                if (res.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return Check(res, host);
                }

                var retryAfter = res.Headers.RetryAfter?.Delta;
                res.Dispose();
                var wait = TimeSpan.FromTicks(Math.Min(
                    retryAfter is { TotalSeconds: > 0 } ra ? ra.Ticks : TimeSpan.FromSeconds(2 * Math.Pow(2, attempt)).Ticks,
                    _o.MaxBackoff.Ticks));
                if (attempt >= _o.MaxRetries)
                {
                    _backoffUntil[host] = _time.GetUtcNow() + wait;
                    throw new RegistryException("rate_limited", $"{host} answered HTTP 429 (rate limited)", 429);
                }

                attempt++;
                await _delay(wait, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static HttpResponseMessage Check(HttpResponseMessage res, string host)
    {
        if (res.IsSuccessStatusCode)
        {
            return res;
        }

        var status = (int)res.StatusCode;
        res.Dispose();
        throw status switch
        {
            404 => new RegistryException("not_found", $"{host}: manifest not found", 404),
            401 or 403 => new RegistryException("unauthorized", $"{host}: authentication required or denied (HTTP {status})", status),
            _ => new RegistryException("other", $"{host}: HTTP {status}", status),
        };
    }

    /// <summary>One authenticated attempt (handles the 401 challenge once).</summary>
    private async Task<HttpResponseMessage> OnceAsync(ImageRef image, string host, string path, HttpMethod method, CancellationToken ct)
    {
        var url = BaseUrl(host) + path;
        var scopeKey = $"{host}|{image.Repo}";
        string? bearer = _tokens.TryGetValue(scopeKey, out var cached) && cached.ExpiresAt > _time.GetUtcNow() ? cached.Token : null;
        var res = await SendAsync(url, method, bearer is null ? null : new AuthenticationHeaderValue("Bearer", bearer), ct);
        if (res.StatusCode != HttpStatusCode.Unauthorized)
        {
            return res;
        }

        var challenge = ParseChallenge(res.Headers.WwwAuthenticate.FirstOrDefault()?.ToString());
        if (challenge is null)
        {
            return res;
        }

        var creds = await _credentials(host, ct);
        if (challenge.Scheme == "bearer")
        {
            var token = await FetchTokenAsync(host, challenge, image, creds, ct);
            if (token is null)
            {
                return res;
            }

            _tokens[scopeKey] = (token.Value.Token, _time.GetUtcNow() + token.Value.Ttl);
            res.Dispose();
            return await SendAsync(url, method, new AuthenticationHeaderValue("Bearer", token.Value.Token), ct);
        }

        if (challenge.Scheme == "basic" && creds is not null)
        {
            // Basic credentials only ever go over https (or an explicitly trusted insecure host): BaseUrl decides the scheme
            res.Dispose();
            return await SendAsync(url, method, new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}"))), ct);
        }

        return res;
    }

    private bool RealmAllowed(string host, Uri realm)
    {
        if (realm.Scheme != Uri.UriSchemeHttps && !(IsInsecure(host) && realm.Scheme == Uri.UriSchemeHttp))
        {
            return false;
        }

        if (string.Equals(realm.Authority, host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _realms.TryGetValue(host, out var allowed) && allowed.Contains(realm.Host, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<(string Token, TimeSpan Ttl)?> FetchTokenAsync(string host, Challenge ch, ImageRef image, RegistryCredential? creds, CancellationToken ct)
    {
        if (!ch.Params.TryGetValue("realm", out var realmRaw) || !Uri.TryCreate(realmRaw, UriKind.Absolute, out var realm))
        {
            return null;
        }

        // RT-20: never send credentials (or even a token request) to a realm the registry did not earn
        if (!RealmAllowed(host, realm))
        {
            throw new RegistryException("unauthorized", $"{host}: refusing token realm on host {realm.Authority}", 401);
        }

        var query = new List<string>();
        foreach (var part in realm.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith("service=", StringComparison.Ordinal) && !part.StartsWith("scope=", StringComparison.Ordinal))
            {
                query.Add(part);
            }
        }

        if (ch.Params.TryGetValue("service", out var service))
        {
            query.Add("service=" + Uri.EscapeDataString(service));
        }

        query.Add("scope=" + Uri.EscapeDataString(ch.Params.TryGetValue("scope", out var scope) ? scope : $"repository:{image.Repo}:pull"));
        var url = new UriBuilder(realm) { Query = string.Join('&', query) }.Uri.ToString();
        var auth = creds is null ? null : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}")));
        using var res = await SendAsync(url, HttpMethod.Get, auth, ct, accept: false);
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RegistryException("rate_limited", $"{realm.Authority} answered HTTP 429", 429);
        }

        if (!res.IsSuccessStatusCode)
        {
            throw new RegistryException("unauthorized", $"{realm.Authority}: token request failed (HTTP {(int)res.StatusCode})", (int)res.StatusCode);
        }

        string? token = null;
        var expiresIn = 60;
        try
        {
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            token = (root.TryGetProperty("token", out var t) ? t.GetString() : null) ?? (root.TryGetProperty("access_token", out var at) ? at.GetString() : null);
            if (root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var e))
            {
                expiresIn = e;
            }
        }
        catch (JsonException)
        {
            // handled below as "no token"
        }

        return string.IsNullOrEmpty(token)
            ? throw new RegistryException("unauthorized", $"{realm.Authority}: token response had no token", 401)
            : (token, TimeSpan.FromSeconds(Math.Max(10, expiresIn - 10)));
    }

    private async Task<HttpResponseMessage> SendAsync(string url, HttpMethod method, AuthenticationHeaderValue? auth, CancellationToken ct, bool accept = true)
    {
        Interlocked.Increment(ref _requests);
        using var request = new HttpRequestMessage(method, url);
        if (accept)
        {
            request.Headers.TryAddWithoutValidation("Accept", ManifestAccept);
        }

        request.Headers.Authorization = auth;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_o.Timeout);
        try
        {
            // HEAD responses have no body: ResponseHeadersRead keeps everything cheap
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new RegistryException("network", $"request failed: {(e is TaskCanceledException ? "timeout" : e.Message)}");
        }
    }

    private async Task ThrottleAsync(string host, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var last = _lastRequest.TryGetValue(host, out var l) ? l : DateTimeOffset.MinValue;
        var wait = TimeSpan.FromMilliseconds(Math.Max(0, (last + _o.MinInterval - now).TotalMilliseconds) + _o.JitterMs());
        if (wait > TimeSpan.Zero)
        {
            await _delay(wait, ct);
        }

        _lastRequest[host] = _time.GetUtcNow();
    }

    // ---- WWW-Authenticate ----------------------------------------------------------------------------------------------------

    internal sealed record Challenge(string Scheme, IReadOnlyDictionary<string, string> Params);

    internal static Challenge? ParseChallenge(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var m = SchemeRe().Match(header);
        if (!m.Success)
        {
            return null;
        }

        var scheme = m.Groups[1].Value.ToLowerInvariant();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match p in ParamRe().Matches(m.Groups[2].Value))
        {
            parameters[p.Groups[1].Value.ToLowerInvariant()] = p.Groups[2].Value;
        }

        return new Challenge(scheme is "bearer" or "basic" ? scheme : "other", parameters);
    }

    [GeneratedRegex(@"^\s*(\w+)\s*(.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SchemeRe();

    [GeneratedRegex("(\\w+)=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ParamRe();

    [GeneratedRegex(@":\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex PortRe();

    [GeneratedRegex(@"^127\.\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex Loopback4Re();
}

/// <summary>Registers the registry client with the stored credentials and QM_REGISTRY_INSECURE / QM_REGISTRY_TOKEN_HOSTS.</summary>
internal static class RegistryClientFactory
{
    public static RegistryClient Create(Quaymaster.Application.Configuration.QmConfig config, Quaymaster.Application.Updates.IRegistryCredentialStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(config);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Quaymaster");
        return new RegistryClient(
            http,
            new RegistryClientOptions { InsecureHosts = config.RegistryInsecure, RealmAllow = config.RegistryTokenRealms },
            (host, ct) => store.GetAsync(host, ct),
            time);
    }
}

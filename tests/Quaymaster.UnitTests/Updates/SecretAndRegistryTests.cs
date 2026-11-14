using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Registry;
using Quaymaster.Domain.Registry;
using Quaymaster.Infrastructure.Registry;
using Quaymaster.Infrastructure.Security;
using Xunit;

namespace Quaymaster.UnitTests.Updates;

public class SecretBoxTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public void Encrypts_and_decrypts_round_trip_with_a_fresh_iv_each_time()
    {
        var box = new SecretBox(Key);
        var a = box.Encrypt("hunter2", "secret_store:ciphertext:registry:ghcr.io");
        var b = box.Encrypt("hunter2", "secret_store:ciphertext:registry:ghcr.io");
        a.Should().NotBe(b);
        a.Should().NotContain("hunter2");
        a.Split('.').Should().HaveCount(5).And.StartWith("v1");
        box.Decrypt(a, "secret_store:ciphertext:registry:ghcr.io").Should().Be("hunter2");
        box.Decrypt(box.Encrypt(string.Empty, "x"), "x").Should().BeEmpty();
        var unicode = "pässwörd-ﬁnal-🔑";
        box.Decrypt(box.Encrypt(unicode, "x"), "x").Should().Be(unicode);
    }

    [Fact]
    public void A_ciphertext_cannot_be_moved_to_another_row_or_tampered_with()
    {
        var box = new SecretBox(Key);
        var ct = box.Encrypt("secret", "secret_store:ciphertext:registry:a.io");
        ((Func<string>)(() => box.Decrypt(ct, "secret_store:ciphertext:registry:b.io"))).Should().Throw<CryptographicException>();
        var parts = ct.Split('.');
        parts[4] = parts[4].Length > 2 ? "AA" + parts[4][2..] : "AA";
        ((Func<string>)(() => box.Decrypt(string.Join('.', parts), "secret_store:ciphertext:registry:a.io"))).Should().Throw<CryptographicException>();
        ((Func<string>)(() => box.Decrypt("v2.a.b.c.d", "x"))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void A_different_master_key_is_rejected_by_key_id()
    {
        var ct = new SecretBox(Key).Encrypt("s", "x");
        var other = new SecretBox(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        other.KeyId.Should().NotBe(new SecretBox(Key).KeyId);
        ((Func<string>)(() => other.Decrypt(ct, "x"))).Should().Throw<CryptographicException>().WithMessage("*different QM_SECRET_KEY*");
    }

    [Fact]
    public void Rejects_keys_shorter_than_32_bytes() =>
        ((Func<SecretBox>)(() => new SecretBox(new byte[16]))).Should().Throw<ArgumentException>();

    [Fact]
    public void Decrypts_a_ciphertext_produced_by_the_original_node_implementation()
    {
        // produced by server/src/crypto/secretbox.ts (hkdfSync sha256, salt "quaymaster", info "secrets-v1", AES-256-GCM)
        const string FromNode = "v1.c17cbdfe.AAECAwQFBgcICQoL.432TnPpDAVmfCmZFNIs7gQ.PS9KSybprsEuA8sK9R80OQ";
        var box = new SecretBox(Key);
        box.KeyId.Should().Be("c17cbdfe");
        box.Decrypt(FromNode, "secret_store:ciphertext:registry:ghcr.io").Should().Be("s3cr3t-pässword");
    }
}

public class RegistryClientTests
{
    private static readonly string Digest = "sha256:" + new string('a', 64);

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? Accept)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Headers.Accept.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string? digest = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK);
        if (digest is not null)
        {
            r.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
        }

        return r;
    }

    private static HttpResponseMessage Challenge(string header)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        r.Headers.TryAddWithoutValidation("WWW-Authenticate", header);
        return r;
    }

    private static HttpResponseMessage Token(string token = "tok", int expires = 300) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"{{\"token\":\"{token}\",\"expires_in\":{expires}}}") };

    private sealed class Rig
    {
        public required RegistryClient Client { get; init; }

        public required Stub Http { get; init; }

        public required List<TimeSpan> Delays { get; init; }

        public required FakeTimeProvider Time { get; init; }
    }

    private static Rig Make(Func<HttpRequestMessage, HttpResponseMessage> respond, RegistryClientOptions? options = null, RegistryCredential? creds = null)
    {
        var stub = new Stub(respond);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var delays = new List<TimeSpan>();
        var client = new RegistryClient(
            new HttpClient(stub),
            options ?? new RegistryClientOptions { JitterMs = () => 0 },
            (_, _) => Task.FromResult(creds),
            time,
            (t, _) =>
            {
                delays.Add(t);
                time.Advance(t);
                return Task.CompletedTask;
            });
        return new Rig { Client = client, Http = stub, Delays = delays, Time = time };
    }

    private static ImageRef Ref(string s) => ImageReferences.Parse(s)!;

    [Fact]
    public async Task Head_follows_the_401_bearer_challenge_and_returns_the_served_digest()
    {
        var rig = Make(req => req.RequestUri!.Host switch
        {
            "auth.docker.io" => Token("T1"),
            _ when req.Headers.Authorization?.Parameter == "T1" => Ok(Digest),
            _ => Challenge("Bearer realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:library/nginx:pull\""),
        });

        var head = await rig.Client.HeadManifestAsync(Ref("nginx:1.27"), null, default);

        head.Digest.Should().Be(Digest);
        rig.Http.Seen.Select(s => (s.Method.Method, s.Url)).Should().Equal(
            ("HEAD", "https://registry-1.docker.io/v2/library/nginx/manifests/1.27"),
            ("GET", "https://auth.docker.io/token?service=registry.docker.io&scope=repository%3Alibrary%2Fnginx%3Apull"),
            ("HEAD", "https://registry-1.docker.io/v2/library/nginx/manifests/1.27"));
        rig.Http.Seen[0].Accept.Should().Contain("application/vnd.oci.image.index.v1+json").And.Contain("application/vnd.docker.distribution.manifest.v2+json");
        rig.Http.Seen[1].Auth.Should().BeNull("anonymous token request without stored credentials");
    }

    [Fact]
    public async Task The_token_is_cached_per_registry_and_repository_so_the_dance_happens_once()
    {
        var rig = Make(req => req.RequestUri!.Host == "auth.docker.io" ? Token("T1") : req.Headers.Authorization?.Parameter == "T1" ? Ok(Digest) : Challenge("Bearer realm=\"https://auth.docker.io/token\",service=\"s\""));
        await rig.Client.HeadManifestAsync(Ref("nginx:1"), null, default);
        await rig.Client.HeadManifestAsync(Ref("nginx:2"), null, default);
        rig.Http.Seen.Count(s => s.Url.StartsWith("https://auth.docker.io", StringComparison.Ordinal)).Should().Be(1);
        await rig.Client.HeadManifestAsync(Ref("redis:7"), null, default);
        rig.Http.Seen.Count(s => s.Url.StartsWith("https://auth.docker.io", StringComparison.Ordinal)).Should().Be(2, "another repository needs its own token");
    }

    [Fact]
    public async Task Stored_credentials_go_to_the_token_realm_as_basic_auth()
    {
        var rig = Make(
            req => req.RequestUri!.Host == "ghcr.io" && req.RequestUri.AbsolutePath == "/token"
                ? Token("G")
                : req.Headers.Authorization?.Parameter == "G" ? Ok(Digest) : Challenge("Bearer realm=\"https://ghcr.io/token\",service=\"ghcr.io\""),
            creds: new RegistryCredential("me", "pw"));
        await rig.Client.HeadManifestAsync(Ref("ghcr.io/o/app:latest"), null, default);
        rig.Http.Seen[1].Auth.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me:pw")));
        rig.Http.Seen[0].Auth.Should().BeNull("credentials are not sent before the challenge");
    }

    [Fact]
    public async Task A_token_realm_on_an_unlisted_host_is_refused_without_contacting_it()
    {
        var rig = Make(_ => Challenge("Bearer realm=\"https://evil.example.net/token\",service=\"x\""), creds: new RegistryCredential("me", "pw"));
        var act = async () => await rig.Client.HeadManifestAsync(Ref("ghcr.io/o/app"), null, default);
        (await act.Should().ThrowAsync<RegistryException>()).Which.Kind.Should().Be("unauthorized");
        rig.Http.Seen.Should().ContainSingle("nothing, least of all credentials, was sent to the rogue realm");
    }

    [Fact]
    public async Task A_plain_http_realm_is_refused_for_secure_registries()
    {
        var rig = Make(_ => Challenge("Bearer realm=\"http://ghcr.io/token\""));
        var act = async () => await rig.Client.HeadManifestAsync(Ref("ghcr.io/o/app"), null, default);
        (await act.Should().ThrowAsync<RegistryException>()).Which.Kind.Should().Be("unauthorized");
        rig.Http.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Configured_token_hosts_are_added_to_the_built_in_ones()
    {
        var options = new RegistryClientOptions
        {
            JitterMs = () => 0,
            RealmAllow = new Dictionary<string, IReadOnlyList<string>> { ["reg.example.com"] = ["sso.example.com"] },
        };
        var rig = Make(req => req.RequestUri!.Host == "sso.example.com" ? Token("S") : req.Headers.Authorization?.Parameter == "S" ? Ok(Digest) : Challenge("Bearer realm=\"https://sso.example.com/t\""), options);
        (await rig.Client.HeadManifestAsync(Ref("reg.example.com/a/b:1"), null, default)).Digest.Should().Be(Digest);
        RegistryClient.MergeRealmAllow(RegistryClient.DefaultRealmAllow, options.RealmAllow)["registry-1.docker.io"].Should().Contain("auth.docker.io");
        RegistryClient.MergeRealmAllow(RegistryClient.DefaultRealmAllow, options.RealmAllow)["reg.example.com"].Should().Contain("sso.example.com");
    }

    [Fact]
    public async Task Loopback_and_explicitly_insecure_hosts_use_http_everything_else_https()
    {
        var rig = Make(_ => Ok(Digest), new RegistryClientOptions { JitterMs = () => 0, InsecureHosts = ["lab.internal:5000"] });
        await rig.Client.HeadManifestAsync(Ref("127.0.0.1:5055/qm/app:1"), null, default);
        await rig.Client.HeadManifestAsync(Ref("localhost/app:1"), null, default);
        await rig.Client.HeadManifestAsync(Ref("lab.internal:5000/app:1"), null, default);
        await rig.Client.HeadManifestAsync(Ref("other.example.com/app:1"), null, default);
        rig.Http.Seen.Select(s => s.Url[..s.Url.IndexOf('/', 8)]).Should().Equal("http://127.0.0.1:5055", "http://localhost", "http://lab.internal:5000", "https://other.example.com");
        RegistryClient.IsLoopbackHost("127.0.0.1:5055").Should().BeTrue();
        RegistryClient.IsLoopbackHost("[::1]:5000").Should().BeTrue();
        RegistryClient.IsLoopbackHost("128.0.0.1").Should().BeFalse();
    }

    [Fact]
    public async Task A_429_is_retried_after_Retry_After_then_succeeds()
    {
        var calls = 0;
        var rig = Make(_ =>
        {
            if (++calls == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            }

            return Ok(Digest);
        });
        (await rig.Client.HeadManifestAsync(Ref("ghcr.io/o/app"), null, default)).Digest.Should().Be(Digest);
        rig.Delays.Should().Contain(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_host_that_keeps_answering_429_is_backed_off_and_then_left_alone()
    {
        var rig = Make(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return r;
        });
        var image = Ref("ghcr.io/o/app");
        var first = async () => await rig.Client.HeadManifestAsync(image, null, default);
        (await first.Should().ThrowAsync<RegistryException>()).Which.Kind.Should().Be("rate_limited");
        rig.Http.Seen.Should().HaveCount(3, "one request plus two retries");
        rig.Client.BackedOffUntil(image).Should().NotBeNull();

        var before = rig.Http.Seen.Count;
        var second = async () => await rig.Client.HeadManifestAsync(image, null, default);
        (await second.Should().ThrowAsync<RegistryException>()).Which.Message.Should().Contain("backing off");
        rig.Http.Seen.Count.Should().Be(before, "no request while backed off");

        rig.Time.Advance(TimeSpan.FromSeconds(31));
        rig.Client.BackedOffUntil(image).Should().BeNull();
    }

    [Fact]
    public async Task Requests_to_one_host_are_spaced_by_the_minimum_interval_plus_jitter()
    {
        var rig = Make(_ => Ok(Digest), new RegistryClientOptions { JitterMs = () => 40, MinInterval = TimeSpan.FromMilliseconds(250) });
        await rig.Client.HeadManifestAsync(Ref("ghcr.io/o/a"), null, default);
        rig.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(40), "first request: only the jitter");
        await rig.Client.HeadManifestAsync(Ref("ghcr.io/o/b"), null, default);
        rig.Delays.Last().Should().BeGreaterThan(TimeSpan.FromMilliseconds(250)).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(290));
    }

    [Fact]
    public async Task Error_statuses_map_to_registry_error_kinds()
    {
        (await FailKind(HttpStatusCode.NotFound)).Should().Be("not_found");
        (await FailKind(HttpStatusCode.Forbidden)).Should().Be("unauthorized");
        (await FailKind(HttpStatusCode.InternalServerError)).Should().Be("other");
        (await FailKind(HttpStatusCode.Unauthorized)).Should().Be("unauthorized", "401 without a challenge");

        static async Task<string> FailKind(HttpStatusCode code)
        {
            var rig = Make(_ => new HttpResponseMessage(code));
            var act = async () => await rig.Client.HeadManifestAsync(Ref("ghcr.io/o/app"), null, default);
            return (await act.Should().ThrowAsync<RegistryException>()).Which.Kind;
        }
    }

    [Fact]
    public async Task A_transport_failure_is_a_network_error()
    {
        var rig = Make(_ => throw new HttpRequestException("connection refused"));
        var act = async () => await rig.Client.HeadManifestAsync(Ref("ghcr.io/o/app"), null, default);
        (await act.Should().ThrowAsync<RegistryException>()).Which.Kind.Should().Be("network");
    }

    [Fact]
    public async Task When_HEAD_has_no_digest_header_the_manifest_body_is_hashed()
    {
        var body = "{\"schemaVersion\":2,\"layers\":[]}";
        var rig = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        var head = await rig.Client.HeadManifestAsync(Ref("ghcr.io/o/app"), null, default);
        head.Digest.Should().Be("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
        rig.Http.Seen.Select(s => s.Method.Method).Should().Equal("HEAD", "GET");
    }

    [Fact]
    public async Task Manifests_are_parsed_for_platform_digests_and_compressed_size()
    {
        var index = $"{{\"manifests\":[{{\"digest\":\"{Digest}\",\"platform\":{{\"os\":\"linux\",\"architecture\":\"amd64\"}}}}]}}";
        var manifest = "{\"config\":{\"size\":10},\"layers\":[{\"size\":100},{\"size\":200}]}";
        var rig = Make(req => req.RequestUri!.AbsolutePath.EndsWith(Digest, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(index) });
        var doc = await rig.Client.GetManifestAsync(Ref("ghcr.io/o/app"), "latest", default);
        doc.Manifests.Should().ContainSingle().Which.Should().Be(new ManifestPlatform(Digest, "linux", "amd64"));
        (await rig.Client.CompressedSizeAsync(Ref("ghcr.io/o/app"), "sha256:" + new string('9', 64), default)).Should().Be(310);
    }

    [Theory]
    [InlineData("Bearer realm=\"https://a/t\",service=\"s\",scope=\"x:y\"", "bearer", "realm", "https://a/t")]
    [InlineData("Basic realm=\"Registry\"", "basic", "realm", "Registry")]
    [InlineData("Negotiate", "other", null, null)]
    public void Parses_WWW_Authenticate(string header, string scheme, string? key, string? value)
    {
        var c = RegistryClient.ParseChallenge(header)!;
        c.Scheme.Should().Be(scheme);
        if (key is not null)
        {
            c.Params[key].Should().Be(value);
        }
    }

    [Fact]
    public void An_empty_challenge_is_null() => RegistryClient.ParseChallenge("  ").Should().BeNull();
}

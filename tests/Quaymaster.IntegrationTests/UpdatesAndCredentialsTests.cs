using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Registry;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Updates;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

internal static class T3Support
{
    public static readonly string NewDigest = "sha256:" + new string('f', 64);
    public static readonly string NginxDigest = "sha256:" + new string('d', 64);

    /// <summary>Waits until an operation is no longer running and returns its record.</summary>
    public static async Task<JsonElement> OperationAsync(ApiClient client, string id)
    {
        for (var i = 0; i < 300; i++)
        {
            var r = await client.GetAsync($"/operations/{id}");
            var op = r.Json.Clone();
            if (op.GetProperty("status").GetString() != "running")
            {
                return op;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("operation did not finish");
    }

    /// <summary>Plenty of disk for pull preflights.</summary>
    public static void RoomyDisk(FakeDocker docker)
    {
        docker.TotalBytes = 2_000_000_000_000;
        docker.FreeBytes = 1_000_000_000_000;
    }

    public static JsonElement Single(JsonElement array, string property, string value) =>
        array.EnumerateArray().Single(e => e.GetProperty(property).GetString() == value);
}

public sealed class UpdatesApiTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null) => _h = await DockerHarness.CreateAsync(settings);

    private static string Key(string s) => $"docker.io/library/{s}";

    [Fact]
    public async Task Get_updates_lists_every_image_in_use_with_the_wire_shape_of_the_original()
    {
        var h = await HarnessAsync();
        h.Registry.Digests[Key("nginx:1.27")] = T3Support.NginxDigest;

        var r = await h.Viewer.GetAsync("/updates");

        r.Status.Should().Be(200, r.Body);
        var images = r.Json.GetProperty("images");
        var nginx = T3Support.Single(images, "ref", "nginx:1.27");
        nginx.GetProperty("state").GetString().Should().Be("up_to_date");
        nginx.GetProperty("localDigest").GetString().Should().Be(T3Support.NginxDigest);
        nginx.GetProperty("remoteDigest").GetString().Should().Be(T3Support.NginxDigest);
        nginx.GetProperty("source").GetString().Should().Be("registry");
        nginx.GetProperty("quarantined").GetBoolean().Should().BeFalse();
        nginx.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
        nginx.GetProperty("checkedAt").GetString().Should().EndWith("Z");
        nginx.GetProperty("usedBy").EnumerateArray().Select(u => u.GetProperty("containerName").GetString()).Should().Equal("web");
        DockerHarness.Keys(nginx.GetProperty("usedBy")[0]).Should().BeEquivalentTo("containerId", "containerName", "project", "service", "imageId");
        DockerHarness.Keys(nginx).Should().BeEquivalentTo("ref", "state", "reason", "localDigest", "remoteDigest", "checkedAt", "source", "quarantined", "usedBy");
    }

    [Fact]
    public async Task A_different_remote_digest_is_an_update_and_a_staged_copy_makes_it_update_staged()
    {
        var h = await HarnessAsync();
        h.Registry.Digests[Key("nginx:1.27")] = T3Support.NewDigest;

        var first = T3Support.Single((await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images"), "ref", "nginx:1.27");
        first.GetProperty("state").GetString().Should().Be("update_available");
        first.GetProperty("remoteDigest").GetString().Should().Be(T3Support.NewDigest);

        // stage it exactly the way a pull would: tag in the daemon + a row
        var image = Quaymaster.Domain.Registry.ImageReferences.Parse("nginx:1.27")!;
        var tag = Quaymaster.Domain.Registry.ImageReferences.StagedTag(image, T3Support.NewDigest);
        h.Docker.ImageList[0].Tags.Add(tag);
        await h.Factory.Services.GetRequiredService<IStagedStore>().PutAsync(new StagedImage(tag, "docker.io/library/nginx:1.27", T3Support.NewDigest, FakeDocker.NginxId, 1), default);

        var second = T3Support.Single((await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images"), "ref", "nginx:1.27");
        second.GetProperty("state").GetString().Should().Be("update_staged");
    }

    [Fact]
    public async Task A_staged_row_whose_image_vanished_is_dropped_and_the_update_is_available_again()
    {
        var h = await HarnessAsync();
        h.Registry.Digests[Key("nginx:1.27")] = T3Support.NewDigest;
        var image = Quaymaster.Domain.Registry.ImageReferences.Parse("nginx:1.27")!;
        var tag = Quaymaster.Domain.Registry.ImageReferences.StagedTag(image, T3Support.NewDigest);
        var staged = h.Factory.Services.GetRequiredService<IStagedStore>();
        await staged.PutAsync(new StagedImage(tag, "x", T3Support.NewDigest, null, 1), default);

        var s = T3Support.Single((await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images"), "ref", "nginx:1.27");

        s.GetProperty("state").GetString().Should().Be("update_available");
        (await staged.GetAsync(tag, default)).Should().BeNull();
    }

    [Fact]
    public async Task Built_locally_images_are_local_only_and_unreachable_registries_are_unknown()
    {
        var h = await HarnessAsync();
        var images = (await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images");
        var postgres = T3Support.Single(images, "ref", "postgres:17");
        postgres.GetProperty("state").GetString().Should().Be("local_only");
        postgres.GetProperty("reason").GetString().Should().Contain("not found in the registry");

        h.Registry.Errors[Key("nginx:1.27")] = new RegistryException("network", "request failed: timeout");
        var web = T3Support.Single((await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images"), "ref", "nginx:1.27");
        web.GetProperty("state").GetString().Should().Be("unknown");
        web.GetProperty("reason").GetString().Should().Be("request failed: timeout");
        web.GetProperty("localDigest").GetString().Should().Be(T3Support.NginxDigest);
    }

    [Fact]
    public async Task When_the_registry_fails_the_daemon_is_asked_and_the_source_says_so()
    {
        var h = await HarnessAsync();
        h.Registry.Errors[Key("nginx:1.27")] = new RegistryException("unauthorized", "denied", 401);
        h.Docker.DistributionDigests["nginx:1.27"] = T3Support.NewDigest;
        var web = T3Support.Single((await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images"), "ref", "nginx:1.27");
        web.GetProperty("state").GetString().Should().Be("update_available");
        web.GetProperty("source").GetString().Should().Be("engine");
    }

    [Fact]
    public async Task Answers_are_cached_for_the_check_ttl_and_a_check_forces_a_fresh_lookup()
    {
        var h = await HarnessAsync(new() { ["QM_CHECK_TTL_MINUTES"] = "10" });
        h.Registry.Digests[Key("nginx:1.27")] = T3Support.NginxDigest;

        await h.Viewer.GetAsync("/updates");
        var after1 = h.Registry.Heads.Count(k => k == Key("nginx:1.27"));
        after1.Should().Be(1);
        var cached = T3Support.Single((await h.Viewer.GetAsync("/updates")).Json.GetProperty("images"), "ref", "nginx:1.27");
        h.Registry.Heads.Count(k => k == Key("nginx:1.27")).Should().Be(1, "inside the TTL the cache answers");
        cached.GetProperty("source").GetString().Should().Be("cache");

        await h.Admin.PostAsync("/updates/check", new { });
        h.Registry.Heads.Count(k => k == Key("nginx:1.27")).Should().Be(2, "a check bypasses the cache");

        h.Factory.Time.Advance(TimeSpan.FromMinutes(11));
        await h.Viewer.GetAsync("/updates");
        h.Registry.Heads.Count(k => k == Key("nginx:1.27")).Should().Be(3, "the TTL expired");
    }

    [Fact]
    public async Task Failures_are_cached_for_at_most_five_minutes()
    {
        var h = await HarnessAsync(new() { ["QM_CHECK_TTL_MINUTES"] = "60" });
        h.Registry.Errors[Key("nginx:1.27")] = new RegistryException("network", "down");
        await h.Viewer.GetAsync("/updates");
        await h.Viewer.GetAsync("/updates");
        h.Registry.Heads.Count(k => k == Key("nginx:1.27")).Should().Be(1);
        h.Factory.Time.Advance(TimeSpan.FromMinutes(6));
        await h.Viewer.GetAsync("/updates");
        h.Registry.Heads.Count(k => k == Key("nginx:1.27")).Should().Be(2);
    }

    [Fact]
    public async Task Ignored_targets_are_skipped_by_update_detection()
    {
        var h = await HarnessAsync();
        h.Registry.Digests[Key("alpine:3")] = T3Support.NewDigest;
        T3Support.Single((await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images"), "ref", "alpine:3");

        (await h.Admin.PostAsync("/ignored", new { kind = "container", name = "solo", confirm = "solo" })).Status.Should().Be(201);

        var after = (await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images");
        after.EnumerateArray().Select(i => i.GetProperty("ref").GetString()).Should().NotContain("alpine:3");
        (await h.Viewer.GetAsync("/updates")).Json.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("ref").GetString()).Should().NotContain("alpine:3");
    }

    [Fact]
    public async Task Check_is_admin_only_and_audited()
    {
        var h = await HarnessAsync();
        (await h.Viewer.PostAsync("/updates/check", new { })).Status.Should().Be(403);
        h.Registry.Heads.Should().BeEmpty();
        (await h.Admin.PostAsync("/updates/check", new { })).Status.Should().Be(200);
        var audit = (await h.Admin.GetAsync("/audit?action=updates.check")).Json.GetProperty("entries");
        audit.GetArrayLength().Should().Be(1);
        audit[0].GetProperty("outcome").GetString().Should().Be("success");
    }

    [Fact]
    public async Task Unauthenticated_requests_are_refused()
    {
        var h = await HarnessAsync();
        var anon = h.Factory.NewClient();
        (await anon.GetAsync("/updates")).Status.Should().Be(401);
        (await anon.GetAsync("/updates/quarantine")).Status.Should().Be(401);
        (await anon.PostAsync("/updates/check", new { })).Status.Should().Be(401);
    }

    [Fact]
    public async Task Quarantine_is_listed_flagged_on_the_update_and_cleared_by_admins()
    {
        var h = await HarnessAsync();
        h.Registry.Digests[Key("nginx:1.27")] = T3Support.NewDigest;
        var target = "docker.io/library/nginx:1.27";
        var quarantine = h.Factory.Services.GetRequiredService<IQuarantineService>();
        await quarantine.AddAsync(target, T3Support.NewDigest, "run-9", "health check failed after update", default);

        var list = await h.Viewer.GetAsync("/updates/quarantine");
        list.Status.Should().Be(200);
        var entry = list.Json.GetProperty("entries").EnumerateArray().Single();
        entry.GetProperty("target").GetString().Should().Be(target);
        entry.GetProperty("digest").GetString().Should().Be(T3Support.NewDigest);
        entry.GetProperty("runId").GetString().Should().Be("run-9");
        entry.GetProperty("reason").GetString().Should().Contain("health check");
        entry.GetProperty("createdAt").GetString().Should().EndWith("Z");

        var web = T3Support.Single((await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images"), "ref", "nginx:1.27");
        web.GetProperty("quarantined").GetBoolean().Should().BeTrue();

        (await h.Viewer.PostAsync("/updates/quarantine/clear", new { target })).Status.Should().Be(403);
        (await h.Admin.PostAsync("/updates/quarantine/clear", new { target, digest = "sha256:xyz" })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/updates/quarantine/clear", new { })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/updates/quarantine/clear", new { target, extra = 1 })).Status.Should().Be(400);

        var wrongDigest = await h.Admin.PostAsync("/updates/quarantine/clear", new { target, digest = "sha256:" + new string('1', 64) });
        wrongDigest.Json.GetProperty("cleared").GetInt32().Should().Be(0);
        var cleared = await h.Admin.PostAsync("/updates/quarantine/clear", new { target, digest = T3Support.NewDigest });
        cleared.Status.Should().Be(200);
        cleared.Json.GetProperty("cleared").GetInt32().Should().Be(1);
        (await h.Viewer.GetAsync("/updates/quarantine")).Json.GetProperty("entries").GetArrayLength().Should().Be(0);
        T3Support.Single((await h.Admin.PostAsync("/updates/check", new { })).Json.GetProperty("images"), "ref", "nginx:1.27").GetProperty("quarantined").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Clearing_without_a_digest_clears_every_digest_of_the_target_and_works_in_read_only_mode()
    {
        var h = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var q = h.Factory.Services.GetRequiredService<IQuarantineService>();
        await q.AddAsync("t:1", "sha256:" + new string('1', 64), null, null, default);
        await q.AddAsync("t:1", "sha256:" + new string('2', 64), null, null, default);
        (await h.Admin.PostAsync("/updates/quarantine/clear", new { target = "t:1" })).Json.GetProperty("cleared").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Checking_is_allowed_in_read_only_mode_because_it_changes_nothing()
    {
        var h = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        (await h.Admin.PostAsync("/updates/check", new { })).Status.Should().Be(200);
        h.Docker.Mutations.Should().BeEmpty();
    }
}

public sealed class RegistryCredentialsApiTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null) => _h = await DockerHarness.CreateAsync(settings);

    private const string Password = "p@ss-w0rd-NEVER-LEAK";

    [Fact]
    public async Task Put_stores_the_credential_encrypted_and_never_returns_the_password()
    {
        var h = await HarnessAsync();
        var put = await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "GHCR.io", username = "bot", password = Password });
        put.Status.Should().Be(200, put.Body);
        put.Body.Should().NotContain(Password);
        put.Json.GetProperty("host").GetString().Should().Be("GHCR.io");
        put.Json.GetProperty("username").GetString().Should().Be("bot");
        DockerHarness.Keys(put.Json).Should().BeEquivalentTo("host", "username");

        var list = await h.Admin.GetAsync("/registries/credentials");
        list.Status.Should().Be(200);
        list.Body.Should().NotContain(Password).And.NotContain("password");
        var c = list.Json.GetProperty("credentials").EnumerateArray().Single();
        c.GetProperty("host").GetString().Should().Be("ghcr.io", "hosts are stored lower-case");
        c.GetProperty("username").GetString().Should().Be("bot");
        c.GetProperty("updatedAt").GetString().Should().EndWith("Z");
        DockerHarness.Keys(c).Should().BeEquivalentTo("host", "username", "updatedAt");

        // at rest: ciphertext only
        await using var db = new SqliteConnection($"Data Source={h.Factory.DatabaseFile};Mode=ReadOnly");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name, ciphertext FROM secret_store";
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        rd.GetString(0).Should().Be("registry:ghcr.io");
        rd.GetString(1).Should().StartWith("v1.").And.NotContain(Password);
        File.ReadAllBytes(h.Factory.DatabaseFile).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(Password)).Should().Be(-1, "the password is nowhere in the database file");

        // and in no audit row
        var audit = await h.Admin.GetAsync("/audit?limit=200");
        audit.Body.Should().NotContain(Password);
        audit.Json.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("action").GetString()).Should().Contain("registry.credentials.put");
    }

    [Fact]
    public async Task The_credential_store_round_trips_through_the_secret_box()
    {
        var h = await HarnessAsync();
        await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "ghcr.io", username = "bot", password = Password });
        var store = h.Factory.Services.GetRequiredService<IRegistryCredentialStore>();
        (await store.GetAsync("ghcr.io", default)).Should().Be(new RegistryCredential("bot", Password));
        await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "ghcr.io", username = "bot2", password = "second" });
        (await store.GetAsync("GHCR.IO", default)).Should().Be(new RegistryCredential("bot2", "second"));
        (await store.ListAsync(default)).Should().ContainSingle();
        (await store.GetAsync("quay.io", default)).Should().BeNull();
    }

    [Fact]
    public async Task All_credential_endpoints_are_admin_only_including_the_read()
    {
        var h = await HarnessAsync();
        (await h.Viewer.GetAsync("/registries/credentials")).Status.Should().Be(403);
        (await h.Viewer.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "a.io", username = "u", password = "p" })).Status.Should().Be(403);
        (await h.Viewer.SendAsync(HttpMethod.Delete, "/registries/credentials/a.io")).Status.Should().Be(403);
        (await h.Factory.NewClient().GetAsync("/registries/credentials")).Status.Should().Be(401);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"host\":\"a.io\",\"username\":\"u\"}")]
    [InlineData("{\"host\":\"a b.io\",\"username\":\"u\",\"password\":\"p\"}")]
    [InlineData("{\"host\":\"a.io/path\",\"username\":\"u\",\"password\":\"p\"}")]
    [InlineData("{\"host\":\"\",\"username\":\"u\",\"password\":\"p\"}")]
    [InlineData("{\"host\":\"a.io\",\"username\":\"\",\"password\":\"p\"}")]
    [InlineData("{\"host\":\"a.io\",\"username\":\"u\",\"password\":\"\"}")]
    [InlineData("{\"host\":\"a.io\",\"username\":\"u\",\"password\":\"p\",\"extra\":1}")]
    public async Task Invalid_bodies_are_rejected_with_a_validation_error(string body)
    {
        var h = await HarnessAsync();
        var r = await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", rawBody: body);
        r.Status.Should().Be(400);
        r.ErrorCode.Should().Be("validation");
        (await h.Admin.GetAsync("/registries/credentials")).Json.GetProperty("credentials").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Delete_is_host_scoped_and_removes_the_secret_too()
    {
        var h = await HarnessAsync();
        await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "ghcr.io", username = "a", password = "x" });
        await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "quay.io", username = "b", password = "y" });
        (await h.Admin.SendAsync(HttpMethod.Delete, "/registries/credentials/nope.io")).Status.Should().Be(404);

        var del = await h.Admin.SendAsync(HttpMethod.Delete, "/registries/credentials/GHCR.io");
        del.Status.Should().Be(200);
        del.Json.GetProperty("ok").GetBoolean().Should().BeTrue();
        (await h.Admin.GetAsync("/registries/credentials")).Json.GetProperty("credentials").EnumerateArray().Select(c => c.GetProperty("host").GetString()).Should().Equal("quay.io");
        var store = h.Factory.Services.GetRequiredService<IRegistryCredentialStore>();
        (await store.GetAsync("ghcr.io", default)).Should().BeNull();
        (await store.GetAsync("quay.io", default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Credentials_can_be_managed_in_read_only_mode_because_they_touch_no_container()
    {
        var h = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        (await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "ghcr.io", username = "a", password = "x" })).Status.Should().Be(200);
        h.Docker.Mutations.Should().BeEmpty();
    }

    [Fact]
    public async Task Changing_credentials_invalidates_the_cached_registry_answers()
    {
        var h = await HarnessAsync();
        h.Registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NginxDigest;
        await h.Viewer.GetAsync("/updates");
        await h.Viewer.GetAsync("/updates");
        h.Registry.Heads.Count(k => k == "docker.io/library/nginx:1.27").Should().Be(1);
        await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "ghcr.io", username = "a", password = "x" });
        await h.Viewer.GetAsync("/updates");
        h.Registry.Heads.Count(k => k == "docker.io/library/nginx:1.27").Should().Be(2);
    }
}

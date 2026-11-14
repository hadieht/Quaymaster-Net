using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Updates;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class ImagePullApiTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null)
    {
        _h = await DockerHarness.CreateAsync(settings);
        T3Support.RoomyDisk(_h.Docker);
        return _h;
    }

    [Fact]
    public async Task A_pull_is_202_with_an_operation_id_and_pulls_by_digest_into_a_staged_tag_without_moving_the_users_tag()
    {
        var h = await HarnessAsync();
        h.Registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;

        var r = await h.Admin.PostAsync("/images/pull", new { @ref = "nginx:1.27" });

        r.Status.Should().Be(202, r.Body);
        DockerHarness.Keys(r.Json).Should().BeEquivalentTo("operationId");
        var op = await T3Support.OperationAsync(h.Admin, r.Json.GetProperty("operationId").GetString()!);
        op.GetProperty("status").GetString().Should().Be("succeeded", op.ToString());
        op.GetProperty("kind").GetString().Should().Be("image.pull");
        op.GetProperty("target").GetString().Should().Be("nginx:1.27");
        op.GetProperty("output").GetString().Should().Contain(T3Support.NewDigest).And.Contain("staged as quaymaster-staged/").And.Contain("NOT moved");

        var staged = ImageReferences.StagedTag(ImageReferences.Parse("nginx:1.27")!, T3Support.NewDigest);
        h.Docker.Mutations.Should().Equal($"pull nginx@{T3Support.NewDigest}", $"tag {new string('f', 12)} {staged}");
        h.Docker.ImageList.Single(i => i.Id == "sha256:" + new string('f', 64)).Tags.Should().Equal(staged);
        h.Docker.ImageList.Single(i => i.Id == FakeDocker.NginxId).Tags.Should().Equal("nginx:1.27");

        var list = await h.Viewer.GetAsync("/images/staged");
        list.Status.Should().Be(200);
        DockerHarness.Keys(list.Json).Should().BeEquivalentTo("staged", "rollbackPoints");
        var s = list.Json.GetProperty("staged").EnumerateArray().Single();
        s.GetProperty("tag").GetString().Should().Be(staged);
        s.GetProperty("ref").GetString().Should().Be("docker.io/library/nginx:1.27");
        s.GetProperty("digest").GetString().Should().Be(T3Support.NewDigest);
        s.GetProperty("imageId").GetString().Should().Be("sha256:" + new string('f', 64));
        s.GetProperty("createdAt").GetString().Should().EndWith("Z");
        DockerHarness.Keys(s).Should().BeEquivalentTo("tag", "ref", "digest", "imageId", "createdAt");

        var audit = (await h.Admin.GetAsync("/audit?action=image.pull")).Json.GetProperty("entries");
        audit[0].GetProperty("outcome").GetString().Should().Be("success");
    }

    [Fact]
    public async Task A_tag_that_does_not_exist_locally_is_added_because_nothing_is_moved()
    {
        var h = await HarnessAsync();
        h.Registry.Digests["docker.io/library/redis:7"] = T3Support.NewDigest;
        var r = await h.Admin.PostAsync("/images/pull", new { @ref = "redis:7" });
        var op = await T3Support.OperationAsync(h.Admin, r.Json.GetProperty("operationId").GetString()!);
        op.GetProperty("status").GetString().Should().Be("succeeded");
        h.Docker.ImageList.Single(i => i.Id == "sha256:" + new string('f', 64)).Tags.Should().Contain("redis:7").And.HaveCount(2);
        op.GetProperty("output").GetString().Should().Contain("did not exist locally; added it");
    }

    [Fact]
    public async Task Staged_listing_also_shows_rollback_points()
    {
        var h = await HarnessAsync();
        var now = h.Factory.Time.GetUtcNow().ToUnixTimeMilliseconds();
        await h.Factory.Services.GetRequiredService<IRollbackStore>().PutAsync(
            new RollbackPoint("quaymaster-rollback/abc:run1", "run1", "shop", "web", "shop-web-1", "nginx:1.26", "sha256:" + new string('1', 64), now, now + 86_400_000, false), default);
        var rb = (await h.Viewer.GetAsync("/images/staged")).Json.GetProperty("rollbackPoints").EnumerateArray().Single();
        DockerHarness.Keys(rb).Should().BeEquivalentTo("tag", "runId", "project", "service", "ref", "imageId", "createdAt", "expiresAt", "released");
        rb.GetProperty("project").GetString().Should().Be("shop");
        rb.GetProperty("released").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Stored_credentials_are_used_for_the_pull_and_never_for_another_registry()
    {
        var h = await HarnessAsync();
        await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "registry-1.docker.io", username = "hubuser", password = "hubpw" });
        await h.Admin.SendAsync(HttpMethod.Put, "/registries/credentials", new { host = "ghcr.io", username = "ghuser", password = "ghpw" });
        h.Registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;
        h.Registry.Digests["quay.io/o/app:1"] = T3Support.NewDigest;
        h.Registry.Digests["ghcr.io/o/app:1"] = T3Support.NewDigest;

        foreach (var reference in new[] { "nginx:1.27", "ghcr.io/o/app:1", "quay.io/o/app:1" })
        {
            var r = await h.Admin.PostAsync("/images/pull", new { @ref = reference });
            r.Status.Should().Be(202, r.Body);
            await T3Support.OperationAsync(h.Admin, r.Json.GetProperty("operationId").GetString()!);
        }

        h.Docker.PullAuths.Should().BeEquivalentTo(
            new[]
            {
                new FakeDocker.RegistryAuthSeen("nginx", "hubuser", "https://index.docker.io/v1/"),
                new FakeDocker.RegistryAuthSeen("ghcr.io/o/app", "ghuser", "ghcr.io"),
            });
        h.Docker.Mutations.Count(m => m.StartsWith("pull quay.io/o/app@", StringComparison.Ordinal)).Should().Be(1, "pulled anonymously");
    }

    [Fact]
    public async Task Only_tag_references_are_accepted()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/images/pull", new { @ref = "nginx@" + T3Support.NewDigest })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/images/pull", new { @ref = "Not A Ref!" })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/images/pull", new { })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/images/pull", new { @ref = "" })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/images/pull", new { @ref = "nginx", extra = true })).Status.Should().Be(400);
        h.Docker.Mutations.Should().BeEmpty();
        h.Registry.Heads.Should().BeEmpty();
    }

    [Fact]
    public async Task Pull_is_admin_only_and_refused_in_read_only_mode_with_the_read_only_code()
    {
        var h = await HarnessAsync();
        (await h.Viewer.PostAsync("/images/pull", new { @ref = "nginx" })).Status.Should().Be(403);
        (await h.Factory.NewClient().PostAsync("/images/pull", new { @ref = "nginx" })).Status.Should().Be(401);
        h.Dispose();

        var ro = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var r = await ro.Admin.PostAsync("/images/pull", new { @ref = "nginx:1.27" });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("read_only");
        ro.Docker.Mutations.Should().BeEmpty();
        (await ro.Viewer.GetAsync("/images/staged")).Status.Should().Be(200);
    }

    [Fact]
    public async Task With_a_managed_scope_only_references_used_by_an_in_scope_container_may_be_pulled()
    {
        var h = await HarnessAsync(new() { ["QM_MANAGED_SCOPE"] = "web*,shop" });
        h.Registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;
        var denied = await h.Admin.PostAsync("/images/pull", new { @ref = "redis:7" });
        denied.Status.Should().Be(403);
        denied.ErrorCode.Should().Be("out_of_scope");
        h.Docker.Mutations.Should().BeEmpty();
        var ok = await h.Admin.PostAsync("/images/pull", new { @ref = "nginx:1.27" });
        ok.Status.Should().Be(202);
        await T3Support.OperationAsync(h.Admin, ok.Json.GetProperty("operationId").GetString()!);
    }

    [Fact]
    public async Task A_pull_that_does_not_fit_fails_the_operation_before_touching_the_daemon()
    {
        var h = await HarnessAsync();
        h.Docker.FreeBytes = 100;
        h.Docker.TotalBytes = 1000;
        h.Registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;
        var r = await h.Admin.PostAsync("/images/pull", new { @ref = "nginx:1.27" });
        var op = await T3Support.OperationAsync(h.Admin, r.Json.GetProperty("operationId").GetString()!);
        op.GetProperty("status").GetString().Should().Be("failed");
        op.GetProperty("error").GetString().Should().Contain("Not enough free disk space");
        h.Docker.Mutations.Should().BeEmpty();
        (await h.Admin.GetAsync("/audit?action=image.pull")).Json.GetProperty("entries")[0].GetProperty("outcome").GetString().Should().Be("failure");
    }

    [Fact]
    public async Task Registry_and_daemon_failures_fail_the_operation_with_their_message()
    {
        var h = await HarnessAsync();
        var missing = await h.Admin.PostAsync("/images/pull", new { @ref = "ghost:1" });
        var op1 = await T3Support.OperationAsync(h.Admin, missing.Json.GetProperty("operationId").GetString()!);
        op1.GetProperty("status").GetString().Should().Be("failed");
        op1.GetProperty("error").GetString().Should().Contain("manifest not found");

        h.Registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;
        h.Docker.PullFailure = "toomanyrequests: rate limit";
        var r = await h.Admin.PostAsync("/images/pull", new { @ref = "nginx:1.27" });
        var op2 = await T3Support.OperationAsync(h.Admin, r.Json.GetProperty("operationId").GetString()!);
        op2.GetProperty("status").GetString().Should().Be("failed");
        op2.GetProperty("error").GetString().Should().Contain("rate limit");
        h.Docker.ImageList.Should().HaveCount(1, "nothing was staged");
        (await h.Viewer.GetAsync("/images/staged")).Json.GetProperty("staged").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Staged_listing_requires_a_session()
    {
        var h = await HarnessAsync();
        (await h.Factory.NewClient().GetAsync("/images/staged")).Status.Should().Be(401);
    }

    [Fact]
    public async Task The_literal_staged_route_wins_over_the_image_reference_route()
    {
        var h = await HarnessAsync();
        var r = await h.Viewer.GetAsync("/images/staged");
        r.Status.Should().Be(200);
        r.Json.TryGetProperty("staged", out _).Should().BeTrue();
        h.Docker.ImageRefsAsked.Should().NotContain("staged");
    }
}

using System.Text.Json;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class ComposeTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null) =>
        _h = await DockerHarness.CreateAsync(settings);

    private static async Task<ApiResponse> WaitOperationAsync(ApiClient client, string id)
    {
        for (var i = 0; i < 100; i++)
        {
            var r = await client.GetAsync("/operations/" + id);
            var status = r.Json.GetProperty("status").GetString();
            if (status is "succeeded" or "failed")
            {
                return r;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("operation did not finish");
    }

    [Fact]
    public async Task Projects_Are_Discovered_From_Container_Labels()
    {
        var h = await HarnessAsync();
        var r = await h.Viewer.GetAsync("/compose/projects");
        r.Status.Should().Be(200, r.Body);
        var shop = r.Json.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "shop");
        shop.GetProperty("workingDir").GetString().Should().Be("/srv/shop");
        shop.GetProperty("services").GetArrayLength().Should().BeGreaterThan(0);
        DockerHarness.Keys(shop).Should().Contain(["name", "level", "fileAccess", "workingDir", "configFiles", "services", "status"]);
        r.Json.EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().NotContain("solo");
    }

    [Fact]
    public async Task Project_Detail_Graph_Config_And_Validate()
    {
        var h = await HarnessAsync();
        var detail = await h.Viewer.GetAsync("/compose/projects/shop");
        detail.Status.Should().Be(200, detail.Body);
        detail.Json.GetProperty("summary").GetProperty("name").GetString().Should().Be("shop");
        DockerHarness.Keys(detail.Json).Should().Contain(["summary", "graph", "services", "namespaceProviders"]);

        var graph = await h.Viewer.GetAsync("/compose/projects/shop/graph");
        graph.Status.Should().Be(200, graph.Body);

        var config = await h.Viewer.GetAsync("/compose/projects/shop/config");
        config.Status.Should().BeOneOf(200, 409);
        config.Body.Should().NotContain(FakeDocker.Secret);

        var validate = await h.Viewer.GetAsync("/compose/projects/shop/validate");
        validate.Status.Should().Be(200, validate.Body);
        DockerHarness.Keys(validate.Json).Should().Contain(["valid", "errors", "warnings"]);
    }

    [Fact]
    public async Task Unknown_And_Invalid_Project_Names()
    {
        var h = await HarnessAsync();
        (await h.Viewer.GetAsync("/compose/projects/ghost")).Status.Should().Be(404);
        var bad = await h.Viewer.GetAsync("/compose/projects/-bad");
        bad.Status.Should().Be(400);
        (await h.Viewer.GetAsync("/compose/projects/..%2Fetc/graph")).Status.Should().BeOneOf(400, 404);
    }

    [Fact]
    public async Task Config_Is_Redacted()
    {
        var h = await HarnessAsync();
        h.Docker.ComposeHandler = argv => argv.Contains("config")
            ? new(0, "{\"name\":\"shop\",\"services\":{\"web\":{\"image\":\"nginx\",\"environment\":{\"API_TOKEN\":\"" + FakeDocker.Secret + "\"}}}}", string.Empty, false)
            : new(0, "[]", string.Empty, false);
        var config = await h.Viewer.GetAsync("/compose/projects/shop/config");
        config.Body.Should().NotContain(FakeDocker.Secret);
    }

    [Fact]
    public async Task Viewers_Cannot_Run_Actions_And_Unknown_Actions_Are_400()
    {
        var h = await HarnessAsync();
        (await h.Viewer.PostAsync("/compose/projects/shop/actions/up", new { })).Status.Should().Be(403);
        var pull = await h.Admin.PostAsync("/compose/projects/shop/actions/pull", new { });
        pull.Status.Should().Be(400);
        (await h.Admin.PostAsync("/compose/projects/shop/actions/restart", new { bogus = 1 })).Status.Should().Be(400);
        h.Docker.ComposeRuns.Where(r => r.Contains("up") || r.Contains("pull")).Should().BeEmpty();
    }

    [Fact]
    public async Task Up_Returns_202_And_The_Operation_Completes_With_Output()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/compose/projects/shop/actions/up", new { });
        r.Status.Should().Be(202, r.Body);
        var id = r.Json.GetProperty("operationId").GetString()!;
        r.Json.EnumerateObject().Should().ContainSingle();

        var op = await WaitOperationAsync(h.Admin, id);
        op.Json.GetProperty("id").GetString().Should().Be(id);
        op.Json.GetProperty("status").GetString().Should().Be("succeeded", op.Body);
        DockerHarness.Keys(op.Json).Should().Contain(["id", "kind", "target", "status", "startedAt", "finishedAt", "actor", "composeVersion", "output", "error"]);

        var up = h.Docker.ComposeRuns.Last(a => a.Contains("up"));
        up.Should().Contain("--detach");
        up.Should().Contain("--project-name=shop");
        up.Should().NotContain("--force-recreate").And.NotContain("-v");

        var list = await h.Admin.GetAsync("/operations");
        list.Json.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("id").GetString()).Should().Contain(id);
    }

    [Fact]
    public async Task Down_Needs_Confirmation_And_Never_Removes_Volumes()
    {
        var h = await HarnessAsync();
        var refused = await h.Admin.PostAsync("/compose/projects/shop/actions/down", new { });
        refused.Status.Should().Be(409);
        refused.ErrorCode.Should().Be("confirmation_required");
        h.Docker.ComposeRuns.Where(a => a.Contains("down")).Should().BeEmpty();

        var ok = await h.Admin.PostAsync("/compose/projects/shop/actions/down", new { confirm = "shop" });
        ok.Status.Should().Be(202, ok.Body);
        await WaitOperationAsync(h.Admin, ok.Json.GetProperty("operationId").GetString()!);
        var down = h.Docker.ComposeRuns.Last(a => a.Contains("down"));
        down.Should().NotContain("-v").And.NotContain("--volumes");
    }

    [Fact]
    public async Task Redeploy_With_Force_Recreate_Needs_Confirmation()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/compose/projects/shop/actions/redeploy", new { forceRecreate = true });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("confirmation_required");
    }

    [Fact]
    public async Task Drifted_Configuration_Blocks_Redeploy_Until_ApplyDrift()
    {
        var h = await HarnessAsync();
        h.Docker.ComposeHandler = argv => argv.Contains("--hash") || argv.Any(a => a.StartsWith("--hash", StringComparison.Ordinal))
            ? new(0, "web 0123456789abcdef0123\ndb 0123456789abcdef0123\n", string.Empty, false)
            : new(0, argv.Contains("ls") ? "[]" : argv.Contains("config") ? FakeDocker.DefaultConfigJson : string.Empty, string.Empty, false);

        var blocked = await h.Admin.PostAsync("/compose/projects/shop/actions/redeploy", new { });
        blocked.Status.Should().Be(409, blocked.Body);
        blocked.ErrorCode.Should().Be("blocked_drift");
        var details = blocked.Json.GetProperty("error").GetProperty("details");
        details.GetProperty("field").GetString().Should().Be("applyDrift");
        details.GetProperty("services").GetArrayLength().Should().BeGreaterThan(0);
        details.TryGetProperty("redactedConfig", out _).Should().BeTrue();
        h.Docker.ComposeRuns.Where(a => a.Contains("up")).Should().BeEmpty();

        var applied = await h.Admin.PostAsync("/compose/projects/shop/actions/redeploy", new { applyDrift = true });
        applied.Status.Should().Be(202, applied.Body);
    }

    [Fact]
    public async Task Read_Only_Refuses_Compose_Actions()
    {
        var h = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var r = await h.Admin.PostAsync("/compose/projects/shop/actions/restart", new { });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("read_only");
        h.Docker.ComposeRuns.Where(a => a.Contains("restart")).Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_Operation_Is_404()
    {
        var h = await HarnessAsync();
        (await h.Viewer.GetAsync("/operations/nope")).Status.Should().Be(404);
    }
}

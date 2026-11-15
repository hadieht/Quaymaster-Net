using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class SystemAndContainerReadTests : IAsyncLifetime
{
    private DockerHarness _h = null!;

    public async Task InitializeAsync() => _h = await DockerHarness.CreateAsync();

    public Task DisposeAsync()
    {
        _h.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Docker_Endpoints_Require_A_Session()
    {
        var anon = _h.Factory.NewClient();
        foreach (var path in new[] { "/system/mode", "/system/health", "/system/info", "/system/host", "/system/df", "/containers", "/containers/web",
                     "/containers/web/stats", "/compose/projects", "/images", "/volumes", "/networks", "/events", "/operations", "/monitors/settings" })
        {
            (await anon.GetAsync(path, auth: false)).Status.Should().Be(401, path);
        }
    }

    [Fact]
    public async Task System_Mode_Reports_Posture()
    {
        var r = await _h.Viewer.GetAsync("/system/mode");
        r.Status.Should().Be(200);
        r.Json.GetProperty("readOnly").GetBoolean().Should().BeFalse();
        r.Json.GetProperty("managedScope").EnumerateArray().Select(x => x.GetString()).Should().Equal("*");
    }

    [Fact]
    public async Task System_Health_Info_And_Host()
    {
        var health = await _h.Viewer.GetAsync("/system/health");
        health.Status.Should().Be(200);
        health.Json.GetProperty("docker").GetString().Should().Be("ok");
        health.Json.GetProperty("status").GetString().Should().Be("ok");

        var info = await _h.Viewer.GetAsync("/system/info");
        info.Status.Should().Be(200);
        info.Json.GetProperty("ServerVersion").GetString().Should().Be("29.1.0");

        var host = await _h.Viewer.GetAsync("/system/host");
        host.Status.Should().Be(200);
        host.Json.GetProperty("memTotal").GetInt64().Should().Be(8L * 1024 * 1024 * 1024);
        host.Json.GetProperty("load").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task System_Df_Is_Cached_And_Refresh_Re_Reads()
    {
        var first = await _h.Viewer.GetAsync("/system/df?refresh=1");
        first.Status.Should().Be(200);
        var before = _h.Docker.DfCalls;
        before.Should().BeGreaterThan(0);

        var cached = await _h.Viewer.GetAsync("/system/df");
        cached.Status.Should().Be(200);
        _h.Docker.DfCalls.Should().Be(before, "a second plain read is served from the cache");

        (await _h.Viewer.GetAsync("/system/df?refresh=1")).Status.Should().Be(200);
        _h.Docker.DfCalls.Should().Be(before, "refresh inside QM_DF_MIN_INTERVAL_SECONDS is throttled");
    }

    [Fact]
    public async Task Containers_List_Has_Capability_And_Mutability()
    {
        var r = await _h.Viewer.GetAsync("/containers");
        r.Status.Should().Be(200);
        var items = r.Json.EnumerateArray().ToList();
        items.Select(c => c.GetProperty("name").GetString()).Should().BeEquivalentTo("web", "db", "solo");
        var web = items.Single(c => c.GetProperty("name").GetString() == "web");
        web.GetProperty("shortId").GetString().Should().Be(FakeDocker.WebId[..12]);
        web.GetProperty("composeProject").GetString().Should().Be("shop");
        web.GetProperty("composeService").GetString().Should().Be("web");
        web.GetProperty("mutable").GetBoolean().Should().BeTrue();
        web.GetProperty("state").GetString().Should().Be("running");
        var solo = items.Single(c => c.GetProperty("name").GetString() == "solo");
        solo.GetProperty("state").GetString().Should().Be("exited");
        DockerHarness.Keys(web).Should().Contain(["id", "shortId", "name", "image", "imageId", "state", "status", "createdAt", "ports", "labels", "mutable"]);
    }

    [Fact]
    public async Task Container_Inspect_Resolves_Name_And_Short_Id_And_Redacts_Secrets()
    {
        var byName = await _h.Viewer.GetAsync("/containers/web");
        byName.Status.Should().Be(200);
        var byId = await _h.Viewer.GetAsync("/containers/" + FakeDocker.WebId[..12]);
        byId.Status.Should().Be(200);
        byId.Json.GetProperty("inspect").GetProperty("Id").GetString().Should().Be(FakeDocker.WebId);
        byName.Body.Should().NotContain(FakeDocker.Secret);
        byName.Body.Should().Contain("DB_PASSWORD=");
        byName.Body.Should().Contain("PATH=/usr/bin");
    }

    [Fact]
    public async Task Learned_Secret_Echoed_In_Cmd_And_Healthcheck_Never_Reaches_A_Viewer()
    {
        // F-01: the env value is learned; Cmd / Healthcheck.Test carry it too and key-based redaction cannot see that.
        var r = await _h.Viewer.GetAsync("/containers/web");
        r.Status.Should().Be(200);
        r.Body.Should().NotContain(FakeDocker.Secret);
        r.Body.Should().Contain("echo starting with");
    }

    [Fact]
    public async Task Container_Ref_Validation_And_Unknown()
    {
        (await _h.Viewer.GetAsync("/containers/nope")).Status.Should().Be(404);
        var bad = await _h.Viewer.GetAsync("/containers/-rf");
        bad.Status.Should().Be(400);
        bad.ErrorCode.Should().Be("validation");
    }

    [Fact]
    public async Task Logs_Need_The_Logs_Permission()
    {
        var denied = await _h.Viewer.GetAsync("/containers/web/logs");
        denied.Status.Should().Be(403);
        denied.ErrorCode.Should().Be("forbidden");

        var ok = await _h.LogsViewer.GetAsync("/containers/web/logs?tail=10&timestamps=false");
        ok.Status.Should().Be(200);
        var lines = ok.Json.GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        (await _h.Admin.GetAsync("/containers/web/logs")).Status.Should().Be(200);
        (await _h.LogsViewer.GetAsync("/containers/web/logs?tail=0")).Status.Should().Be(400);
        (await _h.LogsViewer.GetAsync("/containers/web/logs?timestamps=maybe")).Status.Should().Be(400);
    }

    [Fact]
    public async Task Stats_Ring_Buffer_And_History()
    {
        var stats = await _h.Viewer.GetAsync("/containers/web/stats");
        stats.Status.Should().Be(200);
        stats.Json.GetProperty("points").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);

        var history = await _h.Viewer.GetAsync("/containers/web/stats/history?minutes=30");
        history.Status.Should().Be(200);
        history.Json.GetProperty("rollups").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        (await _h.Viewer.GetAsync("/containers/web/stats/history?minutes=0")).Status.Should().Be(400);
        (await _h.Viewer.GetAsync("/containers/web/stats/history?minutes=abc")).Status.Should().Be(400);
        (await _h.Viewer.GetAsync("/containers/web/stats/history?minutes=43200&bucket=15")).Status.Should().Be(200);
        (await _h.Viewer.GetAsync("/containers/web/stats/history?bucket=0")).Status.Should().Be(400);
        (await _h.Viewer.GetAsync("/containers/web/stats/history?since=100&until=50")).Status.Should().Be(400);
        (await _h.Viewer.GetAsync("/containers/web/stats/history?since=100&until=160&bucket=5")).Status.Should().Be(200);
    }

    [Fact]
    public async Task Daemon_Down_Answers_503_Docker_Unavailable()
    {
        _h.Docker.DaemonDown = true;
        var r = await _h.Viewer.GetAsync("/containers/web");
        r.Status.Should().Be(503);
        r.ErrorCode.Should().Be("docker_unavailable");
    }

    [Fact]
    public async Task Inventory_Reads()
    {
        var images = await _h.Viewer.GetAsync("/images");
        images.Status.Should().Be(200);
        images.Json.GetArrayLength().Should().Be(1);

        var image = await _h.Viewer.GetAsync("/images/nginx:1.27");
        image.Status.Should().Be(200);
        image.Body.Should().NotContain(FakeDocker.Secret);
        (await _h.Viewer.GetAsync("/images/missing:1")).Status.Should().Be(404);

        var volumes = await _h.Viewer.GetAsync("/volumes");
        volumes.Status.Should().Be(200);
        volumes.Json.GetArrayLength().Should().Be(1);
        (await _h.Viewer.GetAsync("/volumes/shop_data")).Status.Should().Be(200);
        (await _h.Viewer.GetAsync("/volumes/missing")).Status.Should().Be(404);

        var networks = await _h.Viewer.GetAsync("/networks");
        networks.Status.Should().Be(200);
        networks.Json.GetArrayLength().Should().Be(1);
        (await _h.Viewer.GetAsync("/networks/shop_default")).Status.Should().Be(200);
        (await _h.Viewer.GetAsync("/networks/missing")).Status.Should().Be(404);
    }

    [Fact]
    public async Task Slash_Encoded_Image_References_Route_To_The_Full_Reference()
    {
        var r = await _h.Viewer.GetAsync("/images/reg.io%2Fteam%2Fapp:tag");
        r.Status.Should().Be(200, r.Body);
        _h.Docker.ImageRefsAsked.Should().Contain("reg.io/team/app:tag");
    }
}

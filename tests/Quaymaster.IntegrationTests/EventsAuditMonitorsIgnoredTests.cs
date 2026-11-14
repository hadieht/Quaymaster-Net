using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Events;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class EventsAuditMonitorsIgnoredTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null) =>
        _h = await DockerHarness.CreateAsync(settings);

    private static JsonObject RawEvent(string action, string name, string id, string? project = null, long time = 1_700_000_000)
    {
        var attrs = new JsonObject { ["name"] = name, ["image"] = "nginx:1.27", ["exitCode"] = "1", ["DB_PASSWORD"] = FakeDocker.Secret };
        if (project is not null)
        {
            attrs["com.docker.compose.project"] = project;
            attrs["com.docker.compose.service"] = name;
        }

        return new JsonObject
        {
            ["Type"] = "container",
            ["Action"] = action,
            ["Actor"] = new JsonObject { ["ID"] = id, ["Attributes"] = attrs },
            ["time"] = time,
            ["timeNano"] = time * 1_000_000_000L,
        };
    }

    private static async Task FeedAsync(DockerHarness h, params JsonObject[] events)
    {
        var watcher = h.Factory.Services.GetRequiredService<EventWatcher>();
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => watcher.RunAsync(cts.Token));
        foreach (var e in events)
        {
            h.Docker.PushEvent(e);
        }

        await h.Docker.WaitEventsHandledAsync();
        await cts.CancelAsync();
        // the flusher sleeps on the fake clock; keep ticking it until shutdown has flushed the batch
        while (await Task.WhenAny(run, Task.Delay(20)) != run)
        {
            h.Factory.Time.Advance(TimeSpan.FromMilliseconds(250));
        }

        await run;
    }

    [Fact]
    public async Task Events_Are_Persisted_Filtered_Paged_And_Sanitised()
    {
        var h = await HarnessAsync();
        await FeedAsync(
            h,
            RawEvent("die", "web", FakeDocker.WebId, "shop", 1_700_000_001),
            RawEvent("start", "web", FakeDocker.WebId, "shop", 1_700_000_002),
            RawEvent("die", "solo", FakeDocker.SoloId, null, 1_700_000_003),
            RawEvent("exec_create: sh -c secret", "web", FakeDocker.WebId, "shop", 1_700_000_004));

        var all = await h.Viewer.GetAsync("/events");
        all.Status.Should().Be(200, all.Body);
        var events = all.Json.GetProperty("events").EnumerateArray().ToList();
        events.Should().HaveCount(3, "exec_* is not an allow-listed action");
        events[0].GetProperty("id").GetInt64().Should().BeGreaterThan(events[1].GetProperty("id").GetInt64(), "newest first");
        DockerHarness.Keys(events[0]).Should().Contain(["id", "time", "type", "action", "actorId", "name", "image", "project", "service", "attributes", "synthetic"]);
        all.Body.Should().NotContain(FakeDocker.Secret, "attribute values outside the allow-list are dropped");

        var byProject = await h.Viewer.GetAsync("/events?project=shop");
        byProject.Json.GetProperty("events").GetArrayLength().Should().Be(2);
        var byContainer = await h.Viewer.GetAsync("/events?container=" + FakeDocker.SoloId);
        byContainer.Json.GetProperty("events").GetArrayLength().Should().Be(1);

        var limited = await h.Viewer.GetAsync("/events?limit=1");
        limited.Json.GetProperty("events").GetArrayLength().Should().Be(1);
        var lastId = limited.Json.GetProperty("events")[0].GetProperty("id").GetInt64();
        var older = await h.Viewer.GetAsync($"/events?beforeId={lastId}&limit=10");
        older.Json.GetProperty("events").EnumerateArray().Should().OnlyContain(e => e.GetProperty("id").GetInt64() < lastId);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=501")]
    [InlineData("limit=abc")]
    [InlineData("beforeId=0")]
    [InlineData("container=not-a-full-id")]
    public async Task Events_Query_Validation(string query)
    {
        var h = await HarnessAsync();
        var r = await h.Viewer.GetAsync("/events?" + query);
        r.Status.Should().Be(400);
        r.ErrorCode.Should().Be("validation");
    }

    [Fact]
    public async Task Audit_Is_Admin_Only_And_Filterable()
    {
        var h = await HarnessAsync();
        (await h.Viewer.GetAsync("/audit")).Status.Should().Be(403);
        (await h.Admin.PostAsync("/containers/web/actions/restart", new { })).Status.Should().Be(200);
        await h.Admin.PostAsync("/containers/solo/actions/remove", new { });

        var all = await h.Admin.GetAsync("/audit");
        all.Status.Should().Be(200);
        var entries = all.Json.GetProperty("entries").EnumerateArray().ToList();
        entries.Count.Should().BeGreaterThan(2);
        DockerHarness.Keys(entries[0]).Should().Equal("id", "time", "actor", "ip", "action", "target", "params", "outcome", "durationMs");

        var byActor = await h.Admin.GetAsync("/audit?actor=admin&limit=2");
        byActor.Json.GetProperty("entries").GetArrayLength().Should().Be(2);
        var firstId = byActor.Json.GetProperty("entries")[1].GetProperty("id").GetInt64();
        (await h.Admin.GetAsync($"/audit?beforeId={firstId}")).Json.GetProperty("entries").EnumerateArray()
            .Should().OnlyContain(e => e.GetProperty("id").GetInt64() < firstId);
        (await h.Admin.GetAsync("/audit?limit=0")).Status.Should().Be(400);
        (await h.Admin.GetAsync("/audit?action=" + entries[0].GetProperty("action").GetString())).Json.GetProperty("entries").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Monitor_Settings_Round_Trip_And_Validation()
    {
        var h = await HarnessAsync();
        var current = await h.Viewer.GetAsync("/monitors/settings");
        current.Status.Should().Be(200);
        DockerHarness.Keys(current.Json).Should().Equal(
            "enabled", "diskFreePercentBelow", "diskFreeBytesBelow", "cpuPercentAbove", "memoryPercentAbove", "sustainSeconds",
            "restartLoopCount", "restartLoopWindowSeconds", "upcomingMaintenanceMinutes");

        (await h.Viewer.SendAsync(HttpMethod.Put, "/monitors/settings", rawBody: current.Body)).Status.Should().Be(403);

        var node = JsonNode.Parse(current.Body)!.AsObject();
        node["cpuPercentAbove"] = 75;
        node["enabled"] = false;
        var put = await h.Admin.SendAsync(HttpMethod.Put, "/monitors/settings", rawBody: node.ToJsonString());
        put.Status.Should().Be(200, put.Body);
        put.Json.GetProperty("cpuPercentAbove").GetDouble().Should().Be(75);
        (await h.Viewer.GetAsync("/monitors/settings")).Json.GetProperty("enabled").GetBoolean().Should().BeFalse();

        node["memoryPercentAbove"] = 101;
        var bad = await h.Admin.SendAsync(HttpMethod.Put, "/monitors/settings", rawBody: node.ToJsonString());
        bad.Status.Should().Be(400);
        bad.Details.Should().Contain(d => d.Path == "memoryPercentAbove");
        (await h.Admin.SendAsync(HttpMethod.Put, "/monitors/settings", rawBody: "{}")).Status.Should().Be(400);
    }

    [Fact]
    public async Task Ignored_List_Is_Admin_Only_And_Needs_Name_Confirmation()
    {
        var h = await HarnessAsync();
        (await h.Viewer.GetAsync("/ignored")).Status.Should().Be(403);
        (await h.Viewer.PostAsync("/ignored", new { kind = "container", name = "solo", confirm = "solo" })).Status.Should().Be(403);
        (await h.Admin.GetAsync("/ignored")).Json.GetProperty("entries").GetArrayLength().Should().Be(0);

        var mismatch = await h.Admin.PostAsync("/ignored", new { kind = "container", name = "solo", confirm = "nope" });
        mismatch.Status.Should().Be(409);
        mismatch.ErrorCode.Should().Be("confirmation_required");
        (await h.Admin.PostAsync("/ignored", new { kind = "pod", name = "x", confirm = "x" })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/ignored", new { kind = "container", name = "solo" })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/ignored", new { kind = "container", name = "ghost", confirm = "ghost" })).Status.Should().Be(404);
        var member = await h.Admin.PostAsync("/ignored", new { kind = "container", name = "web", confirm = "web" });
        member.Status.Should().Be(409);
        member.ErrorCode.Should().Be("unsupported");
    }

    [Fact]
    public async Task Ignoring_A_Container_Hides_It_Without_Touching_Docker_And_Restore_Brings_It_Back()
    {
        var h = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        var created = await h.Admin.PostAsync("/ignored", new { kind = "container", name = "solo", confirm = "solo" });
        created.Status.Should().Be(201, created.Body);
        DockerHarness.Keys(created.Json).Should().Equal("id", "kind", "name", "workingDir", "ignoredAt", "ignoredBy");
        created.Json.GetProperty("ignoredBy").GetString().Should().Be("admin");
        var id = created.Json.GetProperty("id").GetInt64();

        h.Docker.Commands.Should().BeEmpty();
        h.Docker.ComposeRuns.Should().BeEmpty();
        (await h.Admin.GetAsync("/containers")).Json.EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().NotContain("solo");
        (await h.Admin.GetAsync("/ignored")).Json.GetProperty("entries").GetArrayLength().Should().Be(1);
        var ignoredInspect = await h.Admin.GetAsync("/containers/solo");
        ignoredInspect.Status.Should().Be(409);
        ignoredInspect.ErrorCode.Should().Be("ignored");

        (await h.Admin.SendAsync(HttpMethod.Delete, "/ignored/abc")).Status.Should().Be(400);
        (await h.Admin.SendAsync(HttpMethod.Delete, "/ignored/9999")).Status.Should().Be(404);
        var restored = await h.Admin.SendAsync(HttpMethod.Delete, $"/ignored/{id}");
        restored.Status.Should().Be(200);
        restored.Json.GetProperty("name").GetString().Should().Be("solo");
        (await h.Admin.GetAsync("/containers")).Json.EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().Contain("solo");

        var audit = (await h.Admin.GetAsync("/audit?limit=50")).Json.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        audit.Should().Contain(["monitoring.stop", "monitoring.restore"]);
    }

    [Fact]
    public async Task Ignoring_A_Project_Hides_Its_Containers_And_The_Project()
    {
        var h = await HarnessAsync();
        var created = await h.Admin.PostAsync("/ignored", new { kind = "project", name = "shop", confirm = "shop" });
        created.Status.Should().Be(201, created.Body);
        created.Json.GetProperty("workingDir").GetString().Should().Be("/srv/shop");
        (await h.Admin.GetAsync("/containers")).Json.EnumerateArray().Select(c => c.GetProperty("name").GetString()).Should().Equal("solo");
        (await h.Admin.GetAsync("/compose/projects")).Json.EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().NotContain("shop");
    }

    [Fact]
    public async Task Self_Project_Cannot_Be_Ignored()
    {
        var h = await HarnessAsync(new() { ["QM_PROTECTED"] = "shop" });
        var r = await h.Admin.PostAsync("/ignored", new { kind = "project", name = "shop", confirm = "shop" });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("self_protected");
    }

    [Fact]
    public async Task Operations_List_Shape()
    {
        var h = await HarnessAsync();
        var r = await h.Viewer.GetAsync("/operations");
        r.Status.Should().Be(200);
        r.Json.GetProperty("operations").ValueKind.Should().Be(JsonValueKind.Array);
    }
}

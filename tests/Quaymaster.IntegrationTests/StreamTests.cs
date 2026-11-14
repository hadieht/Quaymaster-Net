using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Events;
using Quaymaster.Application.Stats;
using Quaymaster.Application.Streaming;
using Quaymaster.Domain.Stats;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

public sealed class StreamTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null) =>
        _h = await DockerHarness.CreateAsync(settings);

    [Fact]
    public async Task Streams_Require_A_Session()
    {
        var h = await HarnessAsync();
        var anon = h.Factory.NewClient();
        foreach (var path in new[] { "/stream/events", "/stream/stats", "/stream/logs/web", "/stream/operations/x" })
        {
            (await anon.GetAsync(path, auth: false)).Status.Should().Be(401, path);
        }
    }

    [Fact]
    public async Task Events_Stream_Delivers_Docker_Events()
    {
        var h = await HarnessAsync();
        await using var sse = await SseSession.OpenAsync(h.Factory, h.Viewer, "/stream/events");
        sse.Status.Should().Be(200);
        sse.ContentType.Should().Be("text/event-stream");

        var record = new DockerEventRecord(7, "2026-11-14T10:00:00.000Z", "container", "die", FakeDocker.WebId, "web", "nginx:1.27", "shop", "web",
            new Dictionary<string, string> { ["exitCode"] = "1" }, false);
        h.Factory.Services.GetRequiredService<Broadcaster<DockerEventRecord>>().Publish(record);

        var frame = await sse.NextOfAsync("docker-event");
        frame.Json.GetProperty("id").GetInt64().Should().Be(7);
        frame.Json.GetProperty("action").GetString().Should().Be("die");
        frame.Json.GetProperty("project").GetString().Should().Be("shop");
    }

    [Fact]
    public async Task Stats_Stream_Sends_A_Snapshot_Then_Stats()
    {
        var h = await HarnessAsync();
        await using var sse = await SseSession.OpenAsync(h.Factory, h.Viewer, "/stream/stats");
        var snapshot = await sse.NextOfAsync("snapshot");
        snapshot.Data.Should().NotBeEmpty();

        h.Factory.Services.GetRequiredService<Broadcaster<StatsSampleEvent>>()
            .Publish(new StatsSampleEvent(FakeDocker.WebId, "web", new StatsPoint(1_700_000_000_000, 12.5, 100, 200, 1, 2, 3, 4)));
        var stats = await sse.NextOfAsync("stats");
        stats.Json.GetProperty("id").GetString().Should().Be(FakeDocker.WebId);
        stats.Json.GetProperty("name").GetString().Should().Be("web");
        stats.Json.GetProperty("point").GetProperty("cpu").GetDouble().Should().Be(12.5);
    }

    [Fact]
    public async Task Log_Stream_Needs_Logs_Permission_And_Streams_Lines()
    {
        var h = await HarnessAsync();
        var denied = await h.Viewer.GetAsync("/stream/logs/web");
        denied.Status.Should().Be(403);
        denied.ErrorCode.Should().Be("forbidden");
        (await h.LogsViewer.GetAsync("/stream/logs/ghost")).Status.Should().Be(404);
        (await h.LogsViewer.GetAsync("/stream/logs/web?tail=5000")).Status.Should().Be(400);

        await using var sse = await SseSession.OpenAsync(h.Factory, h.LogsViewer, "/stream/logs/web?tail=10");
        sse.Status.Should().Be(200);
        var first = await sse.NextOfAsync("log");
        first.Json.GetProperty("text").GetString().Should().Contain("first line");
        DockerHarness.Keys(first.Json).Should().Contain(["text"]);
        (await sse.NextOfAsync("log")).Json.GetProperty("text").GetString().Should().Contain("second line");
    }

    [Fact]
    public async Task Operation_Stream_Replays_Output_And_Ends_With_Status()
    {
        var h = await HarnessAsync();
        (await h.Viewer.GetAsync("/stream/operations/nope")).Status.Should().Be(404);
        var started = await h.Admin.PostAsync("/compose/projects/shop/actions/up", new { });
        started.Status.Should().Be(202);
        var id = started.Json.GetProperty("operationId").GetString()!;

        await using var sse = await SseSession.OpenAsync(h.Factory, h.Viewer, "/stream/operations/" + id);
        sse.Status.Should().Be(200);
        var status = await sse.NextOfAsync("status");
        status.Json.GetProperty("status").GetString().Should().BeOneOf("running", "succeeded");
        var frames = new List<SseMessage> { status };
        while (frames[^1].Event != "end" && await sse.NextAsync() is { } f)
        {
            frames.Add(f);
        }

        frames.Select(f => f.Event).Should().Contain("end");
        frames.Last(f => f.Event == "end").Json.TryGetProperty("reason", out _).Should().BeTrue();
        (await h.Admin.GetAsync("/operations/" + id)).Json.GetProperty("status").GetString().Should().Be("succeeded");
    }

    [Fact]
    public async Task Per_Session_Cap_Rejects_The_Extra_Stream_With_429()
    {
        var h = await HarnessAsync(new() { ["QM_SSE_MAX_PER_SESSION"] = "1" });
        await using var first = await SseSession.OpenAsync(h.Factory, h.Viewer, "/stream/events");
        first.Status.Should().Be(200);
        var second = await h.Viewer.GetAsync("/stream/stats");
        second.Status.Should().Be(429);
        second.ErrorCode.Should().Be("rate_limited");
        // another session is unaffected
        await using var other = await SseSession.OpenAsync(h.Factory, h.LogsViewer, "/stream/events");
        other.Status.Should().Be(200);
    }

    [Fact]
    public async Task Total_Cap_Rejects_Streams_From_Any_Session()
    {
        var h = await HarnessAsync(new() { ["QM_SSE_MAX_TOTAL"] = "1" });
        await using var first = await SseSession.OpenAsync(h.Factory, h.Viewer, "/stream/events");
        first.Status.Should().Be(200);
        (await h.LogsViewer.GetAsync("/stream/events")).Status.Should().Be(429);
    }

    [Fact]
    public async Task Logout_Closes_The_Stream_With_An_End_Event()
    {
        var h = await HarnessAsync();
        await using var sse = await SseSession.OpenAsync(h.Factory, h.Viewer, "/stream/events");
        sse.Status.Should().Be(200);

        (await h.Viewer.PostAsync("/auth/logout")).Status.Should().Be(200);
        var end = await sse.NextOfAsync("end");
        end.Json.GetProperty("reason").GetString().Should().NotBeNullOrEmpty();
        (await sse.NextAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Password_Reset_Closes_That_Users_Streams()
    {
        var h = await HarnessAsync();
        await using var sse = await SseSession.OpenAsync(h.Factory, h.LogsViewer, "/stream/events");
        var users = await h.Admin.GetAsync("/users");
        var id = users.Json.EnumerateArray().Single(u => u.GetProperty("username").GetString() == "reader").GetProperty("id").GetInt64();
        (await h.Admin.PostAsync($"/users/{id}/password", new { password = "a brand new password" })).Status.Should().Be(200);
        (await sse.NextOfAsync("end")).Json.GetProperty("reason").GetString().Should().NotBeNullOrEmpty();
    }
}

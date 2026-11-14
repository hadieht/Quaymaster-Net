using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Streaming;
using Quaymaster.Application.Updates;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

/// <summary>T4c over HTTP: the standalone endpoint, the maintenance streams and the monitor loop, on the fake Docker world.</summary>
public sealed class StandaloneAndStreamTests : IDisposable
{
    private static readonly string AlpineId = "sha256:" + new string('4', 64);

    private DockerHarness? _h;
    private ScriptedImages _images = null!;

    public void Dispose() => _h?.Dispose();

    /// <summary>`solo` (no Compose labels, exited) runs alpine:3 whose registry digest moved on: an update candidate.</summary>
    private async Task<DockerHarness> HarnessAsync(bool soloUpdatable = true, Dictionary<string, string>? settings = null)
    {
        var docker = new FakeDocker();
        T3Support.RoomyDisk(docker);
        var registry = new FakeRegistry();
        registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;
        if (soloUpdatable)
        {
            docker.ImageList.Add(new FakeDocker.Img { Id = AlpineId, Tags = ["alpine:3"], RepoDigests = ["alpine@sha256:" + new string('c', 64)], Size = 1 });
            docker.Containers[2]!["ImageID"] = AlpineId;
            registry.Digests["docker.io/library/alpine:3"] = T3Support.NewDigest;
        }

        _images = new ScriptedImages();
        _images.Tags["alpine:3"] = AlpineId;
        var images = _images;
        _h = await DockerHarness.CreateAsync(settings, docker, registry, services => services.AddSingleton<IImageOps>(images));
        return _h;
    }

    private static async Task<JsonElement> WaitFinalAsync(DockerHarness h, string id)
    {
        for (var i = 0; i < 500; i++)
        {
            var r = await h.Admin.GetAsync($"/maintenance/runs/{id}");
            if (r.Json.GetProperty("status").GetString() is not ("approved" or "running"))
            {
                return r.Json.Clone();
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("run did not finish");
    }

    // ---- POST /maintenance/standalone -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Standalone_Endpoint_Contract_Auth_Validation_And_Error_Codes()
    {
        var h = await HarnessAsync();
        (await h.Factory.NewClient().PostAsync("/maintenance/standalone", new { container = "solo", confirm = "solo" }, auth: false)).Status.Should().Be(401);
        (await h.Viewer.PostAsync("/maintenance/standalone", new { container = "solo", confirm = "solo" })).Status.Should().Be(403);

        var empty = await h.Admin.PostAsync("/maintenance/standalone", new { });
        (empty.Status, empty.ErrorCode).Should().Be((400, "validation"));
        var unknown = await h.Admin.PostAsync("/maintenance/standalone", new { container = "solo", confirm = "solo", force = true });
        (unknown.Status, unknown.ErrorCode).Should().Be((400, "validation"));

        (await h.Admin.PostAsync("/maintenance/standalone", new { container = "ghost", confirm = "ghost" })).Status.Should().Be(404);

        var noConfirm = await h.Admin.PostAsync("/maintenance/standalone", new { container = "solo" });
        (noConfirm.Status, noConfirm.ErrorCode).Should().Be((409, "confirmation_required"));
        noConfirm.Json.GetProperty("error").GetProperty("details").GetProperty("expected").GetString().Should().Be("solo");

        var compose = await h.Admin.PostAsync("/maintenance/standalone", new { container = "web", confirm = "web" });
        (compose.Status, compose.ErrorCode).Should().Be((409, "unsupported"));
        h.Docker.Mutations.Should().BeEmpty("every refusal happens before anything is touched");
    }

    [Fact]
    public async Task Standalone_Refuses_When_There_Is_Nothing_To_Update_And_In_Read_Only_Mode()
    {
        var h = await HarnessAsync(soloUpdatable: false);
        var none = await h.Admin.PostAsync("/maintenance/standalone", new { container = "solo", confirm = "solo" });
        (none.Status, none.ErrorCode).Should().Be((409, "conflict"));
        h.Docker.Mutations.Should().BeEmpty();
        h.Dispose();

        var ro = await HarnessAsync(settings: new() { ["QM_READ_ONLY"] = "true" });
        var refused = await ro.Admin.PostAsync("/maintenance/standalone", new { container = "solo", confirm = "solo" });
        (refused.Status, refused.ErrorCode).Should().Be((409, "read_only"));
    }

    [Fact]
    public async Task Standalone_Accepts_Runs_The_Clone_Sequence_And_Records_The_Run()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/maintenance/standalone", new { container = "solo", confirm = "solo" });
        r.Status.Should().Be(202, r.Body);
        var runId = r.Json.GetProperty("runId").GetString()!;
        r.Json.GetProperty("id").GetString().Should().Be(runId);
        r.Json.GetProperty("target").GetString().Should().Be("solo");
        r.Json.GetProperty("trigger").GetString().Should().Be("manual");

        var done = await WaitFinalAsync(h, runId);
        done.GetProperty("status").GetString().Should().Be("completed", done.ToString());
        done.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetString()).Should().Equal("preflight", "recovery_point", "pull", "create", "swap");
        done.GetProperty("result").GetProperty("services")[0].GetProperty("outcome").GetString().Should().Be("updated");

        h.Docker.Mutations.Should().Contain(m => m.StartsWith("create solo-qm-new-", StringComparison.Ordinal));
        h.Docker.Mutations.Should().Contain(m => m.EndsWith(" solo", StringComparison.Ordinal) && m.StartsWith("rename ", StringComparison.Ordinal));
        h.Docker.Mutations.Should().Contain(m => m.Contains("-qm-old-", StringComparison.Ordinal));
        _images.Tags["alpine:3"].Should().Be(ScriptedImages.NewId, "the tag moved to the pulled digest");

        // the live output is the operation with the same id
        (await h.Admin.GetAsync($"/operations/{runId}")).Json.GetProperty("status").GetString().Should().Be("succeeded");
        // a standalone run has no compose recovery point: restore is by hand and says so
        var restore = await h.Admin.PostAsync($"/maintenance/runs/{runId}/restore", new { confirm = "solo" });
        (restore.Status, restore.ErrorCode).Should().Be((409, "conflict"));
        restore.Body.Should().Contain("by hand");
    }

    // ---- /stream/maintenance --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Maintenance_Streams_Require_A_Session()
    {
        var h = await HarnessAsync();
        var anon = h.Factory.NewClient();
        (await anon.GetAsync("/stream/maintenance", auth: false)).Status.Should().Be(401);
        (await anon.GetAsync("/stream/maintenance/runs/x", auth: false)).Status.Should().Be(401);
        (await h.Viewer.GetAsync("/stream/maintenance/runs/nope")).Status.Should().Be(404);
    }

    [Fact]
    public async Task Maintenance_Stream_Lifecycle_Snapshot_Run_Event_Heartbeat_And_End()
    {
        var h = await HarnessAsync();
        var first = await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" });
        first.Status.Should().Be(200, first.Body);
        var firstId = first.Json.GetProperty("id").GetString()!;

        await using var sse = await SseSession.OpenAsync(h.Factory, h.Viewer, "/stream/maintenance");
        sse.Status.Should().Be(200);
        sse.ContentType.Should().Be("text/event-stream");
        var snapshot = await sse.NextOfAsync("snapshot");
        snapshot.Json.GetProperty("id").GetString().Should().Be(firstId);
        snapshot.Json.GetProperty("status").GetString().Should().Be("planned");
        DockerHarness.Keys(snapshot.Json).Should().Contain(["id", "kind", "target", "status", "createdAt"]);

        // a new run appears -> `run` event on the next 2 s poll
        var second = await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" });
        var secondId = second.Json.GetProperty("id").GetString()!;
        h.Factory.Time.Advance(MaintenanceStreamFeed.PollInterval);
        var run = await sse.NextOfAsync("run");
        run.Json.GetProperty("id").GetString().Should().Be(secondId);

        // heartbeat comment every 15 s
        h.Factory.Time.Advance(TimeSpan.FromSeconds(15));
        (await sse.NextCommentAsync("ping")).Should().NotBeNull();

        // session end closes the stream with `end {reason}`
        (await h.Viewer.PostAsync("/auth/logout")).Status.Should().Be(200);
        (await sse.NextOfAsync("end")).Json.GetProperty("reason").GetString().Should().NotBeNullOrEmpty();
        (await sse.NextAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Run_Output_Stream_Replays_The_Operation_Of_A_Run_And_Ends()
    {
        var h = await HarnessAsync();
        var started = await h.Admin.PostAsync("/maintenance/standalone", new { container = "solo", confirm = "solo" });
        var runId = started.Json.GetProperty("runId").GetString()!;
        await WaitFinalAsync(h, runId);

        await using var sse = await SseSession.OpenAsync(h.Factory, h.Viewer, $"/stream/maintenance/runs/{runId}");
        sse.Status.Should().Be(200);
        var texts = new List<string>();
        SseMessage? last = null;
        while (await sse.NextAsync() is { } f)
        {
            last = f;
            if (f.Event == "output")
            {
                texts.Add(f.Json.GetProperty("text").GetString()!);
            }

            if (f.Event == "end")
            {
                break;
            }
        }

        string.Concat(texts).Should().Contain($"run {runId} finished: completed");
        last!.Event.Should().Be("end");
    }

    // ---- monitor loop ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Monitor_Loop_Announces_Updates_And_Reports_Event_Types_Through_The_Sink()
    {
        var h = await HarnessAsync();
        var sink = new CapturingSink();
        // the real host: swap only the sink, then run one tick of the real evaluator
        h.Dispose();
        var docker = new FakeDocker();
        T3Support.RoomyDisk(docker);
        var registry = new FakeRegistry();
        registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;
        _h = await DockerHarness.CreateAsync(null, docker, registry, services => services.AddSingleton<INotificationSink>(sink));

        await _h.Factory.Services.GetRequiredService<IMonitorEvaluator>().TickAsync(default);
        sink.Events.Should().Contain(e => e.Type == "updates.available");
        var ev = sink.Events.First(e => e.Type == "updates.available");
        ev.Severity.Should().Be("info");
        ev.DedupeKey.Should().StartWith("updates:");

        // a plan that cannot be approved in time: created awaiting approval -> maintenance.awaiting_approval, expiry -> run.skipped
        var created = await _h.Admin.PostAsync("/maintenance/runs", new { project = "shop" });
        created.Status.Should().Be(201, created.Body);
        var runId = created.Json.GetProperty("id").GetString()!;
        sink.Events.Should().Contain(e => e.Type == "maintenance.awaiting_approval" && (string?)e.Data!["run"] == runId);

        _h.Factory.Time.Advance(TimeSpan.FromHours(73));
        await _h.Factory.Services.GetRequiredService<IMonitorEvaluator>().TickAsync(default);
        sink.Events.Should().Contain(e => e.Type == "run.skipped" && e.DedupeKey == $"expired:{runId}");
        (await _h.Factory.Services.GetRequiredService<IRunStore>().GetAsync(runId, default))!.Info.Status.Should().Be("skipped"); // read through the store: the admin session itself expired with the clock
    }

    private sealed class CapturingSink : INotificationSink
    {
        private readonly List<NotificationEvent> _events = [];

        public IReadOnlyList<NotificationEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Notify(NotificationEvent notification)
        {
            lock (_events)
            {
                _events.Add(notification);
            }
        }
    }
}

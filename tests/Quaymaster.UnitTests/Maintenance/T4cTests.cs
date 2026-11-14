using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Safety;
using Quaymaster.Application.Storage;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Safety;
using Xunit;

namespace Quaymaster.UnitTests.Maintenance;

internal sealed class NoConfigFiles : IFileReader
{
    public string ReadAllText(string path) => throw new FileNotFoundException(path);
}

internal static class T4cConfig
{
    public static QmConfig Load(Dictionary<string, string?>? extra = null)
    {
        var env = new Dictionary<string, string?> { ["QM_SECRET_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray()) };
        foreach (var (k, v) in extra ?? [])
        {
            env[k] = v;
        }

        return QmConfigLoader.Load(env, new NoConfigFiles());
    }
}

public class CloneSpecBuilderTests
{
    private static JsonObject Container(Action<JsonObject>? tweak = null)
    {
        var c = JsonNode.Parse("""
        {
          "Id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Name": "/solo",
          "Config": { "Hostname": "aaaaaaaaaaaa", "Env": ["PATH=/usr/bin", "APP_MODE=prod"], "Cmd": ["nginx"], "Labels": { "team": "x", "img": "same" } },
          "HostConfig": { "NetworkMode": "appnet", "Binds": ["/srv/data:/data"], "RestartPolicy": { "Name": "always", "MaximumRetryCount": 0 } },
          "Mounts": [
            { "Type": "bind", "Source": "/srv/data", "Destination": "/data", "RW": true },
            { "Type": "volume", "Name": "anon123", "Destination": "/var/lib/x", "RW": true } ],
          "NetworkSettings": { "Networks": {
            "appnet": { "Aliases": ["aaaaaaaaaaaa", "solo", "web"], "IPAMConfig": { "IPv4Address": "10.1.0.5" } },
            "backnet": { "Aliases": ["solo"] } } }
        }
        """)!.AsObject();
        tweak?.Invoke(c);
        return c;
    }

    private static readonly JsonObject OldImage = JsonNode.Parse("""{ "Config": { "Env": ["PATH=/usr/bin"], "Cmd": ["nginx"], "Labels": { "img": "same" } } }""")!.AsObject();

    [Fact]
    public void Keeps_only_what_differs_from_the_old_image_and_reattaches_every_mount()
    {
        var spec = CloneSpecBuilder.Build(Container(), OldImage, "nginx:1.27");
        spec.Body["Image"]!.GetValue<string>().Should().Be("nginx:1.27");
        spec.Body["Env"]!.AsArray().Select(e => e!.GetValue<string>()).Should().Equal("APP_MODE=prod");
        spec.Body["Cmd"].Should().BeNull("the command equals the old image's default");
        spec.Body["Labels"]!.AsObject().Select(p => p.Key).Should().Equal("team");
        spec.Body["Hostname"].Should().BeNull("the hostname is the auto-generated short id");
        var mounts = spec.Body["HostConfig"]!["Mounts"]!.AsArray();
        mounts.Should().ContainSingle("the bind is already a Bind, only the anonymous volume is added");
        mounts[0]!["Source"]!.GetValue<string>().Should().Be("anon123");
        spec.Notes.Should().Contain(n => n.Contains("anon123", StringComparison.Ordinal));
    }

    [Fact]
    public void First_user_network_goes_into_create_and_the_rest_is_connected_before_start()
    {
        var spec = CloneSpecBuilder.Build(Container(), OldImage, "nginx:1.27");
        var primary = spec.Body["NetworkingConfig"]!["EndpointsConfig"]!["appnet"]!;
        primary["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()).Should().Equal("web"); // the container's own name and short id are not aliases to carry over
        primary["IPAMConfig"]!["IPv4Address"]!.GetValue<string>().Should().Be("10.1.0.5");
        spec.ExtraNetworks.Select(n => n.Network).Should().Equal("backnet");
        spec.ExtraNetworks[0].ToEndpointConfig()["Aliases"]!.AsArray().Should().BeEmpty();
    }

    [Theory]
    [InlineData("container:other")]
    public void Refuses_a_shared_network_namespace(string mode)
    {
        var act = () => CloneSpecBuilder.Build(Container(c => c["HostConfig"]!["NetworkMode"] = mode), OldImage, "x:1");
        act.Should().Throw<NotCloneableException>().WithMessage("*network namespace*");
    }

    [Fact]
    public void Refuses_auto_remove_and_compose_managed_containers()
    {
        var rm = () => CloneSpecBuilder.Build(Container(c => c["HostConfig"]!["AutoRemove"] = true), OldImage, "x:1");
        rm.Should().Throw<NotCloneableException>().WithMessage("*--rm*");
        var compose = () => CloneSpecBuilder.Build(Container(c => c["Config"]!["Labels"]!["com.docker.compose.project"] = "shop"), OldImage, "x:1");
        compose.Should().Throw<NotCloneableException>().WithMessage("*Compose*");
    }
}

public class StandaloneExecutorTests
{
    private const string OldId = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string CloneId = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string OldImg = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewImg = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Digest = "sha256:d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1";

    private sealed class World
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

        public List<string> Calls { get; } = [];

        public FakeRunStore Runs { get; } = new();

        public FakeRollbackStore Rollback { get; } = new();

        public FakeImages Images { get; } = new();

        public CollectingSink Notes { get; } = new();

        public IMutationGateway Gateway { get; } = Substitute.For<IMutationGateway>();

        public IContainerService Containers { get; } = Substitute.For<IContainerService>();

        public IUpdateService Updates { get; } = Substitute.For<IUpdateService>();

        public IPolicyResolver Policy { get; } = Substitute.For<IPolicyResolver>();

        public IOperationManager Operations { get; } = Substitute.For<IOperationManager>();

        public IRunEnvironment Env { get; } = Substitute.For<IRunEnvironment>();

        public bool CloneHealthy { get; set; } = true;

        public StandaloneExecutor Executor { get; }

        public StandalonePrepared Prepared { get; } = new(OldId, "solo", "nginx:1.27", Digest, OldImg);

        public World(bool running = true, string restart = "always")
        {
            Images.Tags["nginx:1.27"] = OldImg;
            Images.Tags[OldImg] = OldImg;
            Images.PullResults[Digest] = NewImg;
            var raw = JsonNode.Parse($$"""
            {
              "Id": "{{OldId}}", "Name": "/solo", "Image": "{{OldImg}}",
              "State": { "Running": {{(running ? "true" : "false")}} },
              "Config": { "Hostname": "111111111111" },
              "HostConfig": { "NetworkMode": "default", "RestartPolicy": { "Name": "{{restart}}", "MaximumRetryCount": 0 } },
              "Mounts": [],
              "NetworkSettings": { "Networks": { "bridge": { "Aliases": null }, "appnet": { "Aliases": ["solo"] } } }
            }
            """)!.AsObject();
            Containers.ResolveAsync(default!, default).ReturnsForAnyArgs(Task.FromResult(new ResolvedContainer(OldId, "solo", null, null, raw)));
            Policy.ResolveAsync(default, default, default, default).ReturnsForAnyArgs(Task.FromResult(
                new EffectivePolicy("manual", false, true, null, false, new HealthPolicy(5, 0, null), 1, null, new Dictionary<string, string>())));
            Gateway.Policy.Returns(new SafetyPolicy(new SafetyOptions(false, [], [], [])));
            Gateway.CreateCloneAsync(default!, default!, default!, default).ReturnsForAnyArgs(_ =>
            {
                Calls.Add("create-clone");
                return Task.FromResult(CloneId);
            });
            Gateway.ConnectCloneNetworkAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(c =>
            {
                Calls.Add("connect:" + c.ArgAt<string>(2));
                return Task.CompletedTask;
            });
            Gateway.RenameContainerAsync(default!, default!, default).ReturnsForAnyArgs(c =>
            {
                Calls.Add($"rename:{(c.ArgAt<ContainerRef>(0).Id == OldId ? "old" : "clone")}->{c.ArgAt<string>(1)}");
                return Task.CompletedTask;
            });
            Gateway.SetRestartPolicyAsync(default!, default!, default, default).ReturnsForAnyArgs(c =>
            {
                Calls.Add($"restart-policy:{c.ArgAt<string>(1)}");
                return Task.CompletedTask;
            });
            Gateway.ExecuteContainerAsync(default!, default!, default).ReturnsForAnyArgs(c =>
            {
                Calls.Add($"old:{c.ArgAt<ContainerCommand>(1).Action}".ToLowerInvariant());
                return Task.FromResult(true);
            });
            Gateway.ExecuteOnCloneAsync(default!, default!, default).ReturnsForAnyArgs(c =>
            {
                Calls.Add($"clone:{c.ArgAt<ContainerCommand>(1).Action}".ToLowerInvariant());
                return Task.FromResult(true);
            });
            Env.InspectAsync(default!, default).ReturnsForAnyArgs(_ => Task.FromResult<InspectLite?>(
                CloneHealthy ? new InspectLite(NewImg, 0, "running", true, false, 0, "2026-11-14T12:00:00Z", null) : new InspectLite(NewImg, 0, "exited", false, false, 1, "2026-11-14T12:00:00Z", null)));
            var health = new HealthGate(Env, Substitute.For<IHttpProbe>(), new HealthTimings { PollMs = 1000, Sleep = (ms, _) => { Clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; } }, Clock);
            Executor = new StandaloneExecutor(Containers, Images, Updates, Runs, Rollback, Substitute.For<IStagedStore>(), Policy, Gateway, health, Operations, Notes, T4cConfig.Load(), Clock);
        }

        public async Task<(string Status, RunDetail Detail)> RunAsync()
        {
            var id = Guid.NewGuid().ToString();
            await Runs.InsertAsync(new NewRun(id, "update", "manual", "solo", RunStatuses.Approved, null, null, "admin", [], Clock.GetUtcNow().ToUnixTimeMilliseconds()), default);
            var status = await Executor.ExecuteAsync(id, Prepared, "admin", new FakeOperationContext(), default);
            return (status, (await Runs.GetAsync(id, default))!);
        }
    }

    [Fact]
    public async Task Clone_flow_runs_in_order_and_finishes_completed()
    {
        var w = new World();
        var (status, detail) = await w.RunAsync();

        status.Should().Be(RunStatuses.Completed);
        w.Calls.Should().Equal(
            "create-clone",
            "connect:appnet",
            "old:stop",
            "restart-policy:no",
            $"rename:old->solo-qm-old-{w.Clock.GetUtcNow().ToUnixTimeSeconds()}",
            "rename:clone->solo",
            "clone:start");
        await w.Env.Received().InspectAsync(CloneId, Arg.Any<CancellationToken>()); // the probe looked at the CLONE
        detail.Steps.Select(s => s.Step).Should().Equal("preflight", "recovery_point", "pull", "create", "swap", "health");
        w.Rollback.Points.Values.Should().ContainSingle(p => p.ImageId == OldImg && p.Project == "standalone");
        w.Images.Tags["nginx:1.27"].Should().Be(NewImg, "the user tag moves just before the clone is created");
        w.Notes.Types.Should().Equal("run.succeeded");
        detail.Info.Result!.Services.Should().ContainSingle(s => s.Outcome == "updated");
        detail.Info.Reason.Should().Contain("docker update --restart=always");
    }

    [Fact]
    public async Task A_stopped_container_is_swapped_but_not_started_or_probed()
    {
        var w = new World(running: false, restart: "no");
        var (status, _) = await w.RunAsync();
        status.Should().Be(RunStatuses.Completed);
        w.Calls.Should().NotContain("clone:start").And.NotContain(c => c.StartsWith("restart-policy", StringComparison.Ordinal));
        await w.Env.DidNotReceiveWithAnyArgs().InspectAsync(default!, default);
    }

    [Fact]
    public async Task Failed_health_removes_the_clone_restores_the_old_container_and_the_tag()
    {
        var w = new World { CloneHealthy = false };
        var (status, detail) = await w.RunAsync();

        status.Should().Be(RunStatuses.RolledBack);
        var stamp = w.Clock.GetUtcNow().ToUnixTimeSeconds();
        w.Calls.Skip(7).Should().Equal(
            "clone:stop",
            $"rename:clone->solo-qm-new-{stamp}",
            "clone:remove",
            "rename:old->solo",
            "restart-policy:always",
            "old:start");
        w.Images.Tags["nginx:1.27"].Should().Be(OldImg);
        w.Notes.Types.Should().Equal("run.rolled_back");
        detail.Info.Reason.Should().Contain("previous container was restored");
    }

    [Fact]
    public async Task Pull_failure_touches_nothing()
    {
        var w = new World();
        w.Images.PullFailure = "denied";
        var (status, _) = await w.RunAsync();
        status.Should().Be(RunStatuses.Failed);
        w.Calls.Should().BeEmpty("nothing was created, stopped or renamed");
        w.Images.Tags["nginx:1.27"].Should().Be(OldImg);
        w.Notes.Types.Should().Equal("run.failed");
    }

    [Fact]
    public async Task Prepare_requires_the_name_as_confirmation_and_refuses_compose_containers()
    {
        var w = new World();
        var noConfirm = () => w.Executor.PrepareAsync(new StandaloneRequest("solo", "other"), default);
        (await noConfirm.Should().ThrowAsync<AppException>()).Which.Code.Should().Be(ErrorCode.ConfirmationRequired);

        w.Containers.ResolveAsync(default!, default).ReturnsForAnyArgs(Task.FromResult(new ResolvedContainer(OldId, "web", "shop", "web", new JsonObject())));
        var compose = () => w.Executor.PrepareAsync(new StandaloneRequest("web", "web"), default);
        (await compose.Should().ThrowAsync<AppException>()).Which.Code.Should().Be(ErrorCode.Unsupported);
    }

    [Fact]
    public async Task Prepare_needs_an_available_update()
    {
        var w = new World();
        w.Updates.StatusesAsync(true, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ImageUpdateStatusDto>>(
            [new ImageUpdateStatusDto("nginx:1.27", "up_to_date", null, Digest, Digest, null, "registry", false, [new ImageUserDto(OldId, "solo", null, null, OldImg)])]));
        var act = () => w.Executor.PrepareAsync(new StandaloneRequest("solo", "solo"), default);
        (await act.Should().ThrowAsync<AppException>()).Which.Message.Should().Contain("nothing to update");

        w.Updates.StatusesAsync(true, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ImageUpdateStatusDto>>(
            [new ImageUpdateStatusDto("nginx:1.27", "update_available", null, OldImg, Digest, null, "registry", false, [new ImageUserDto(OldId, "solo", null, null, OldImg)])]));
        var prepared = await w.Executor.PrepareAsync(new StandaloneRequest("solo", "solo"), default);
        prepared.Should().Be(new StandalonePrepared(OldId, "solo", "nginx:1.27", Digest, OldImg));
    }
}

public class ScheduleAndLoopRuleTests
{
    private const long Min = 60_000;

    [Fact]
    public void Missed_occurrences_are_skipped_but_the_latest_inside_an_open_window_still_fires()
    {
        long now = 100 * Min;
        long[] due = [now - (30 * Min), now - (10 * Min), now - (1 * Min)];
        ScheduleTick.Decide(due, now, isUpdateJob: true, hasWindow: true, inWindow: true)
            .Should().Equal(OccurrenceAction.SkipMissed, OccurrenceAction.SkipMissed, OccurrenceAction.Fire);
        ScheduleTick.Decide([now - (10 * Min)], now, isUpdateJob: true, hasWindow: false, inWindow: true)
            .Should().Equal(OccurrenceAction.SkipMissed); // a late update without a window is never run unattended
        ScheduleTick.Decide([now - (10 * Min)], now, isUpdateJob: false, hasWindow: false, inWindow: true)
            .Should().Equal(OccurrenceAction.Fire); // a late image check is harmless
    }

    [Fact]
    public void Update_jobs_outside_their_window_are_skipped()
    {
        long now = 100 * Min;
        ScheduleTick.Decide([now - Min], now, true, true, false).Should().Equal(OccurrenceAction.SkipWindow);
        ScheduleTick.Decide([now - Min], now, false, true, false).Should().Equal(OccurrenceAction.Fire); // windows gate update jobs only
    }

    [Fact]
    public void Restart_loop_counts_dies_inside_the_window_only()
    {
        var t0 = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var window = TimeSpan.FromSeconds(300);
        var (w1, loop1) = RestartLoop.Record([], t0, window, 3);
        var (w2, loop2) = RestartLoop.Record(w1, t0.AddSeconds(60), window, 3);
        var (w3, loop3) = RestartLoop.Record(w2, t0.AddSeconds(120), window, 3);
        (loop1, loop2, loop3).Should().Be((false, false, true));
        var (w4, loop4) = RestartLoop.Record(w3, t0.AddSeconds(400), window, 3);
        w4.Should().HaveCount(2, "the two earliest dies fell out of the 300 s window");
        loop4.Should().BeFalse();
    }
}

public class MaintenanceMonitorTests
{
    private sealed class World
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

        public IScheduleStore Schedules { get; } = Substitute.For<IScheduleStore>();

        public IWindowStore Windows { get; } = Substitute.For<IWindowStore>();

        public IRunStore RunStore { get; } = Substitute.For<IRunStore>();

        public IRunService Runs { get; } = Substitute.For<IRunService>();

        public IUpdateService Updates { get; } = Substitute.For<IUpdateService>();

        public IImageCleanupService Cleanup { get; } = Substitute.For<IImageCleanupService>();

        public IMonitorSettingsService Settings { get; } = Substitute.For<IMonitorSettingsService>();

        public ISystemService System { get; } = Substitute.For<ISystemService>();

        public IKvStore Kv { get; } = Substitute.For<IKvStore>();

        public CollectingSink Sink { get; } = new();

        public MaintenanceMonitor Monitor { get; }

        public long Now => Clock.GetUtcNow().ToUnixTimeMilliseconds();

        public World(long diskFloor = 1_000_000_000)
        {
            Settings.GetAsync(default).ReturnsForAnyArgs(Task.FromResult(Monitors.Defaults with { UpcomingMaintenanceMinutes = 60 }));
            Schedules.ListAsync(default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ScheduleRecord>>([]));
            Updates.StatusesAsync(default, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ImageUpdateStatusDto>>([]));
            System.HostAsync(default).ReturnsForAnyArgs(Task.FromResult(new HostMetricsDto(1, [0.1], 8_000_000_000, 4_000_000_000, 100, new HostRootDto("/var/lib/docker", 100_000_000_000, 50_000_000_000), null)));
            Kv.GetAsync(default!, default).ReturnsForAnyArgs(Task.FromResult<string?>(null));
            Runs.ExpireApprovalsAsync(default).ReturnsForAnyArgs(Task.FromResult(0));
            var config = T4cConfig.Load(new Dictionary<string, string?> { ["QM_DISK_FLOOR_BYTES"] = diskFloor.ToString(global::System.Globalization.CultureInfo.InvariantCulture) });
            Monitor = new MaintenanceMonitor(Schedules, Windows, RunStore, Runs, Updates, Cleanup, Settings, System, Sink, Kv, config, Clock, NullLogger<MaintenanceMonitor>.Instance);
        }

        public ScheduleRecord Schedule(string cron, string job = "update", string mode = "auto", string? windowId = null, long? cursor = null) =>
            new("sched-1", "nightly", cron, "UTC", job, new JsonObject { ["project"] = "shop" }, mode, windowId, true, cursor ?? Now - 30_000, Now - 86_400_000);

        public void Has(params ScheduleRecord[] s) => Schedules.ListAsync(default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ScheduleRecord>>(s));
    }

    [Fact]
    public async Task A_due_auto_schedule_creates_a_scheduled_run_and_moves_the_cursor_first()
    {
        var w = new World();
        w.Has(w.Schedule("* * * * *"));
        await w.Monitor.TickAsync(default);

        await w.Runs.Received(1).CreateAsync(
            Arg.Is<CreateRunRequest>(r => r.Project == "shop"),
            Arg.Is<Actor>(a => a.Name == "schedule:nightly"),
            Arg.Is<ScheduledRun>(s => s.Mode == "auto" && s.ScheduleId == "sched-1"),
            Arg.Any<CancellationToken>());
        await w.Schedules.Received().SetCursorAsync("sched-1", w.Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Window_gating_update_jobs_outside_the_window_do_not_start_a_run()
    {
        var w = new World();
        // window Mon-Sun 03:00-04:00 UTC; the clock says 12:00 -> closed
        w.Windows.GetAsync("win-1", default).ReturnsForAnyArgs(Task.FromResult<WindowRecord?>(new WindowRecord("win-1", "night", [0, 1, 2, 3, 4, 5, 6], "03:00", "04:00", "UTC", true, 0)));
        w.Has(w.Schedule("* * * * *", windowId: "win-1"));
        await w.Monitor.TickAsync(default);
        await w.Runs.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default, default);

        // inside the window the same schedule fires
        w.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 15, 3, 30, 0, TimeSpan.Zero));
        w.Has(w.Schedule("* * * * *", windowId: "win-1"));
        await w.Monitor.TickAsync(default);
        await w.Runs.ReceivedWithAnyArgs(1).CreateAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_closed_window_at_approval_time_is_reported_as_run_skipped()
    {
        var w = new World();
        w.Has(w.Schedule("* * * * *"));
        w.Runs.CreateAsync(default!, default!, default, default).ThrowsAsyncForAnyArgs(new AppException(409, ErrorCode.WindowClosed, "The maintenance window is closed"));
        await w.Monitor.TickAsync(default);
        w.Sink.Events.Should().ContainSingle(e => e.Type == "run.skipped").Which.Data!["reason"].Should().Be("window_closed");
    }

    [Fact]
    public async Task Upcoming_maintenance_is_announced_with_a_stable_dedupe_key()
    {
        var w = new World();
        w.Has(w.Schedule("0 13 * * *", mode: "manual", cursor: 0));
        w.Schedules.ListAsync(default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ScheduleRecord>>([w.Schedule("0 13 * * *", mode: "manual", cursor: w.Now)]));
        await w.Monitor.TickAsync(default);
        w.Clock.Advance(TimeSpan.FromMinutes(1));
        await w.Monitor.TickAsync(default);

        var upcoming = w.Sink.Events.Where(e => e.Type == "maintenance.upcoming").ToList();
        upcoming.Should().HaveCount(2, "the monitor re-announces every tick; the sink owns the cool-down");
        upcoming.Select(e => e.DedupeKey).Distinct().Should().ContainSingle().Which.Should().StartWith("upcoming:sched-1:");
        upcoming[0].CooldownMs.Should().Be(24 * 3_600_000L);
        upcoming[0].Data!["target"].Should().Be("shop");
    }

    [Fact]
    public async Task Available_updates_are_announced_once_per_day()
    {
        var w = new World();
        w.Updates.StatusesAsync(false, default).ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ImageUpdateStatusDto>>(
            [new ImageUpdateStatusDto("nginx:1.27", "update_available", null, "sha256:a", "sha256:b", null, "registry", false, [new ImageUserDto("c1", "web", null, null, "sha256:a")])]));
        await w.Monitor.TickAsync(default);
        w.Sink.Types.Should().Contain("updates.available");
        var ev = w.Sink.Events.Single(e => e.Type == "updates.available");
        ev.Data!["count"].Should().Be(1);
        ev.CooldownMs.Should().Be(24 * 3_600_000L);
        await w.Kv.Received().SetAsync(MaintenanceMonitor.UpdateCheckKey, Arg.Any<string>(), Arg.Any<CancellationToken>());

        w.Sink.Events.Clear();
        w.Kv.GetAsync(MaintenanceMonitor.UpdateCheckKey, default).ReturnsForAnyArgs(Task.FromResult<string?>(w.Now.ToString(global::System.Globalization.CultureInfo.InvariantCulture)));
        await w.Monitor.TickAsync(default);
        w.Sink.Types.Should().NotContain("updates.available", "the daily marker is fresh");
    }

    [Fact]
    public async Task Disk_below_the_floor_raises_host_disk_low_and_a_healthy_disk_stays_quiet()
    {
        var quiet = new World(diskFloor: 10_000_000_000);
        await quiet.Monitor.TickAsync(default);
        quiet.Sink.Types.Should().NotContain("host.disk_low");

        var low = new World(diskFloor: 80_000_000_000);
        await low.Monitor.TickAsync(default);
        low.Sink.Events.Should().ContainSingle(e => e.Type == "host.disk_low").Which.Severity.Should().Be("critical");
    }

    [Fact]
    public async Task A_disabled_monitor_still_fires_schedules_but_announces_nothing()
    {
        var w = new World();
        w.Settings.GetAsync(default).ReturnsForAnyArgs(Task.FromResult(Monitors.Defaults with { Enabled = false }));
        w.Has(w.Schedule("* * * * *"));
        await w.Monitor.TickAsync(default);
        await w.Runs.ReceivedWithAnyArgs(1).CreateAsync(default!, default!, default, default);
        w.Sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cleanup_report_schedule_emits_cleanup_report_without_deleting_anything()
    {
        var w = new World();
        w.Cleanup.PreviewAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromResult(
            new CleanupPreviewDto("p1", "2026-11-14T12:10:00.000Z", [], 24, [new CleanupCandidateDto("sha256:x", "dangling", "old:1", 5_242_880, null, 48, "dangling")], [], new ReportOnlyDto([], []), 5_242_880)));
        w.Has(w.Schedule("* * * * *", job: "cleanup_report"));
        await w.Monitor.TickAsync(default);
        var ev = w.Sink.Events.Single(e => e.Type == "cleanup.report");
        ev.Data!["candidates"].Should().Be(1);
        await w.Cleanup.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default!, default);
        await w.RunStore.Received().InsertAsync(Arg.Is<NewRun>(r => r.Kind == "cleanup_report"), Arg.Any<CancellationToken>());
    }
}

public class SinkDedupeTests
{
    [Fact]
    public async Task The_outbound_sink_collapses_repeats_inside_the_cool_down_and_lets_them_through_after_it()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var audit = Substitute.For<IAuditLog>();
        var sink = new AuditNotificationSink(audit, clock);
        NotificationEvent Ev() => new("updates.available", "info", "t", "m", "k1", 60_000);

        sink.Notify(Ev());
        sink.Notify(Ev());
        clock.Advance(TimeSpan.FromSeconds(59));
        sink.Notify(Ev());
        clock.Advance(TimeSpan.FromSeconds(2));
        sink.Notify(Ev());
        sink.Notify(new NotificationEvent("updates.available", "info", "t", "m", "other-key", 60_000));

        for (var i = 0; i < 100 && audit.ReceivedCalls().Count() < 3; i++)
        {
            await Task.Delay(20);
        }

        audit.ReceivedCalls().Should().HaveCount(3, "k1 at 0 s, k1 at 61 s, other-key");
    }
}

public class ApprovalExpiryIntegrationTests
{
    [Fact]
    public async Task Overdue_approvals_become_skipped_and_announce_run_skipped_once()
    {
        var w = new RunWorld(new RunWorld.SvcSpec("web", "cid-web"));
        var runs = w.Runs;
        var id = Guid.NewGuid().ToString();
        var now = w.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await runs.InsertAsync(new NewRun(id, "update", "schedule", "shop", RunStatuses.AwaitingApproval, null, w.CurrentPlan, "schedule:nightly", [], now, ApprovalExpiresAt: now + 1_000), default);
        var service = new RunService(runs, w.Planner, w.Executor, Substitute.For<IOperationManager>(), w.Windows, w.Rollback, w.Notes, w.Gateway, w.Config, w.Clock);

        (await service.ExpireApprovalsAsync(default)).Should().Be(0);
        w.Clock.Advance(TimeSpan.FromSeconds(5));
        (await service.ExpireApprovalsAsync(default)).Should().Be(1);
        (await service.ExpireApprovalsAsync(default)).Should().Be(0, "already skipped");

        w.Notes.Events.Should().ContainSingle(e => e.Type == "run.skipped").Which.DedupeKey.Should().Be($"expired:{id}");
        (await runs.GetAsync(id, default))!.Info.Status.Should().Be(RunStatuses.Skipped);
    }
}

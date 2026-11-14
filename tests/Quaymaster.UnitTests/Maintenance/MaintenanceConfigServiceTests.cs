using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Compose;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Safety;
using Xunit;

namespace Quaymaster.UnitTests.Maintenance;

/// <summary>Runs the audited body and remembers what was audited; everything that would touch Docker is unreachable in these tests.</summary>
internal sealed class RecordingGateway(SafetyPolicy policy) : IMutationGateway
{
    public List<string> Audited { get; } = [];

    public SafetyPolicy Policy => policy;

    public async Task<T> AuditedAsync<T>(AuditScope scope, Func<Task<T>> body, CancellationToken ct)
    {
        Audited.Add(scope.Action);
        return await body();
    }

    public Task RecordAsync(AuditScope scope, AuditOutcome outcome, object? parameters, long? durationMs, CancellationToken ct)
    {
        Audited.Add(scope.Action);
        return Task.CompletedTask;
    }

    public Task AssertContainerAsync(ContainerRef container, CancellationToken ct) => throw new NotSupportedException();

    public Task AssertProjectAsync(string project, ProjectRefs? refs, bool perService, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> ExecuteContainerAsync(ContainerRef container, ContainerCommand command, CancellationToken ct) => throw new NotSupportedException();

    public Task PullImageAsync(ImagePull request, IProgress<PullProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

    public Task TagImageAsync(string imageId, string repo, string tag, CancellationToken ct) => throw new NotSupportedException();

    public Task RemoveImageAsync(string nameOrId, CancellationToken ct) => throw new NotSupportedException();

    public Task RemoveVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();

    public Task RemoveNetworkAsync(string id, string name, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> CreateCloneAsync(ContainerRef source, string cloneName, JsonObject body, CancellationToken ct) => throw new NotSupportedException();
    public Task ConnectCloneNetworkAsync(ContainerRef source, string cloneId, string network, JsonObject endpoint, CancellationToken ct) => throw new NotSupportedException();
    public Task RenameContainerAsync(ContainerRef container, string newName, CancellationToken ct) => throw new NotSupportedException();
    public Task SetRestartPolicyAsync(ContainerRef container, string policy, int maximumRetryCount, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> ExecuteOnCloneAsync(ContainerRef source, ContainerCommand command, CancellationToken ct) => throw new NotSupportedException();

    public Task<ComposeRunResult> RunComposeAsync(IReadOnlyList<string> argv, ComposeRunOptions? options, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class MemPolicyStore : IPolicyStore
{
    public Dictionary<(string, string), PolicyRecord> Rows { get; } = [];

    public Task<IReadOnlyList<PolicyRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PolicyRecord>>([.. Rows.Values]);

    public Task<PolicyRecord?> GetAsync(string kind, string key, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault((kind, key)));

    public Task PutAsync(string kind, string key, PolicyInput policy, long now, CancellationToken ct)
    {
        Rows[(kind, key)] = new PolicyRecord(kind, key, policy, now);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string kind, string key, CancellationToken ct) => Task.FromResult(Rows.Remove((kind, key)));
}

internal sealed class MemWindowStore : IWindowStore
{
    public List<WindowRecord> Rows { get; } = [];

    public Func<string, bool> UsedBySchedule { get; set; } = _ => false;

    public Task<IReadOnlyList<WindowRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WindowRecord>>([.. Rows]);

    public Task<WindowRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));

    public Task<WindowRecord?> FindByNameAsync(string name, CancellationToken ct) => Task.FromResult(Rows.FirstOrDefault(r => r.Name == name));

    public Task<WindowRecord> CreateAsync(WindowWrite w, long now, CancellationToken ct)
    {
        var r = new WindowRecord(Guid.NewGuid().ToString(), w.Name, w.Days, w.Start, w.End, w.Timezone, w.Enabled, now);
        Rows.Add(r);
        return Task.FromResult(r);
    }

    public Task<WindowRecord?> UpdateAsync(string id, WindowWrite w, CancellationToken ct)
    {
        var i = Rows.FindIndex(r => r.Id == id);
        if (i < 0)
        {
            return Task.FromResult<WindowRecord?>(null);
        }

        Rows[i] = new WindowRecord(id, w.Name, w.Days, w.Start, w.End, w.Timezone, w.Enabled, Rows[i].CreatedAt);
        return Task.FromResult<WindowRecord?>(Rows[i]);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct) => Task.FromResult(Rows.RemoveAll(r => r.Id == id) > 0);

    public Task<bool> InUseAsync(string id, CancellationToken ct) => Task.FromResult(UsedBySchedule(id));
}

internal sealed class MemScheduleStore : IScheduleStore
{
    public List<ScheduleRecord> Rows { get; } = [];

    public Task<IReadOnlyList<ScheduleRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ScheduleRecord>>([.. Rows]);

    public Task<ScheduleRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));

    public Task<ScheduleRecord?> FindByNameAsync(string name, CancellationToken ct) => Task.FromResult(Rows.FirstOrDefault(r => r.Name == name));

    public Task<ScheduleRecord> CreateAsync(ScheduleWrite w, long cursorUtc, long now, CancellationToken ct)
    {
        var r = new ScheduleRecord(Guid.NewGuid().ToString(), w.Name, w.Cron, w.Timezone, w.JobType, w.Selector, w.Mode, w.WindowId, w.Enabled, cursorUtc, now);
        Rows.Add(r);
        return Task.FromResult(r);
    }

    public Task<ScheduleRecord?> UpdateAsync(string id, ScheduleWrite w, CancellationToken ct)
    {
        var i = Rows.FindIndex(r => r.Id == id);
        if (i < 0)
        {
            return Task.FromResult<ScheduleRecord?>(null);
        }

        Rows[i] = Rows[i] with { Name = w.Name, Cron = w.Cron, Timezone = w.Timezone, JobType = w.JobType, Selector = w.Selector, Mode = w.Mode, WindowId = w.WindowId, Enabled = w.Enabled };
        return Task.FromResult<ScheduleRecord?>(Rows[i]);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct) => Task.FromResult(Rows.RemoveAll(r => r.Id == id) > 0);

    public Task SetCursorAsync(string id, long cursorUtc, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class OkProbes : IProbeVerifier
{
    public List<(string Url, int Status)> Verified { get; } = [];

    public Task VerifyAsync(string url, int expectStatus, CancellationToken ct)
    {
        Verified.Add((url, expectStatus));
        return Task.CompletedTask;
    }
}

public class MaintenanceConfigServiceTests
{
    private static readonly Actor Admin = new("admin", "127.0.0.1");

    private static SafetyPolicy Safety(params string[] scope) => new(new SafetyOptions(false, scope, [], []));

    private static JsonNode Json(string s) => JsonNode.Parse(s)!;

    // ---- policy merge ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Effective_policy_merges_stored_levels_least_to_most_specific_and_names_the_sources()
    {
        var store = new MemPolicyStore();
        var gw = new RecordingGateway(Safety());
        var svc = new PolicyService(store, gw, new OkProbes(), TimeProvider.System);
        await svc.PutAsync("global", "whatever", Json("""{"updateMode":"auto","requireApproval":false,"health":{"timeoutSeconds":120}}"""), Admin, default);
        await svc.PutAsync("project", "shop", Json("""{"critical":true}"""), Admin, default);
        await svc.PutAsync("service", "shop/web", Json("""{"updateMode":"pull_only","health":{"stabilizationSeconds":5}}"""), Admin, default);
        await svc.PutAsync("container", "solo", Json("""{"updateMode":"notify"}"""), Admin, default);

        store.Rows.Keys.Should().Contain(("global", "*"), "a global policy is always keyed *");
        var web = await svc.ResolveAsync("shop", "web", null, default);
        web.UpdateMode.Should().Be("pull_only");
        web.Critical.Should().BeTrue();
        web.RequireApproval.Should().BeFalse();
        web.Health.TimeoutSeconds.Should().Be(120);
        web.Health.StabilizationSeconds.Should().Be(5);
        web.Sources["updateMode"].Should().Be("service:shop/web");

        var db = await svc.ResolveAsync("shop", "db", null, default);
        db.UpdateMode.Should().Be("auto");
        db.Sources["updateMode"].Should().Be("global:*");

        // a container-level policy only applies to a standalone container (no project)
        (await svc.ResolveAsync(null, null, "solo", default)).UpdateMode.Should().Be("notify");
        (await svc.ResolveAsync("shop", null, "solo", default)).UpdateMode.Should().Be("auto");
        gw.Audited.Should().OnlyContain(a => a == "maintenance.policy.put");
    }

    [Fact]
    public async Task Policy_keys_scope_and_probe_rules()
    {
        var probes = new OkProbes();
        var svc = new PolicyService(new MemPolicyStore(), new RecordingGateway(Safety("shop")), probes, TimeProvider.System);

        (await FluentActions.Awaiting(() => svc.PutAsync("planet", "x", Json("{}"), Admin, default)).Should().ThrowAsync<AppException>()).Which.Status.Should().Be(400);
        (await FluentActions.Awaiting(() => svc.PutAsync("service", "web", Json("{}"), Admin, default)).Should().ThrowAsync<AppException>()).Which.Status.Should().Be(400);
        (await FluentActions.Awaiting(() => svc.PutAsync("project", "Shop", Json("{}"), Admin, default)).Should().ThrowAsync<AppException>()).Which.Status.Should().Be(400);
        (await FluentActions.Awaiting(() => svc.PutAsync("project", "shop", Json("""{"updateMode":"nope"}"""), Admin, default)).Should().ThrowAsync<AppException>()).Which.Status.Should().Be(400);

        var scope = await FluentActions.Awaiting(() => svc.PutAsync("service", "other/web", Json("{}"), Admin, default)).Should().ThrowAsync<AppException>();
        scope.Which.Status.Should().Be(409);
        scope.Which.Code.Should().Be(ErrorCode.OutOfScope);

        // %2F-encoded keys are decoded like decodeURIComponent
        (await svc.PutAsync("service", "shop%2Fweb", Json("""{"health":{"probe":{"url":"http://x/health","expectStatus":200}}}"""), Admin, default)).ScopeKey.Should().Be("shop/web");
        probes.Verified.Should().Equal(("http://x/health", 200));

        (await FluentActions.Awaiting(() => svc.DeleteAsync("project", "shop", Admin, default)).Should().ThrowAsync<AppException>()).Which.Status.Should().Be(404);
    }

    // ---- windows --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Window_open_now_and_next_start_follow_the_zoned_wall_clock_across_the_dst_change()
    {
        // Europe/Berlin springs forward 2027-03-28 02:00 -> 03:00. Window: every day 01:30-03:30 local.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2027-03-27T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var svc = new WindowService(new MemWindowStore(), new RecordingGateway(Safety()), clock);
        var w = await svc.CreateAsync(new WindowBody("night", [0, 1, 2, 3, 4, 5, 6], "01:30", "03:30", "Europe/Berlin", null), Admin, default);

        w.Enabled.Should().BeTrue();
        w.OpenNow.Should().BeFalse();
        w.NextStart.Should().Be("2027-03-28T00:30:00.000Z", "01:30 CET = 00:30Z on the day before the gap");

        clock.SetUtcNow(DateTimeOffset.Parse("2027-03-28T01:10:00Z", System.Globalization.CultureInfo.InvariantCulture)); // 03:10 CEST
        (await svc.ListAsync(default)).Windows.Single().OpenNow.Should().BeTrue();

        clock.SetUtcNow(DateTimeOffset.Parse("2027-03-28T02:31:00Z", System.Globalization.CultureInfo.InvariantCulture)); // 04:31 CEST: the window is 01:30 CET + 2h = 04:30 CEST
        var after = (await svc.ListAsync(default)).Windows.Single();
        after.OpenNow.Should().BeFalse();
        after.NextStart.Should().Be("2027-03-28T23:30:00.000Z", "next 01:30 CEST = 23:30Z");
    }

    [Fact]
    public async Task Window_rules_dedupe_days_reject_equal_bounds_and_protect_windows_in_use()
    {
        var store = new MemWindowStore();
        var svc = new WindowService(store, new RecordingGateway(Safety()), TimeProvider.System);
        var w = await svc.CreateAsync(new WindowBody("a", [3, 1, 3], "22:00", "02:00", "UTC", false), Admin, default);
        w.Days.Should().Equal(1, 3);
        w.Enabled.Should().BeFalse();
        w.NextStart.Should().BeNull();

        (await FluentActions.Awaiting(() => svc.CreateAsync(new WindowBody("b", [1], "02:00", "02:00", "UTC", null), Admin, default)).Should().ThrowAsync<AppException>()).Which.Status.Should().Be(400);
        (await FluentActions.Awaiting(() => svc.CreateAsync(new WindowBody("a", [1], "01:00", "02:00", "UTC", null), Admin, default)).Should().ThrowAsync<AppException>()).Which.Status.Should().Be(409);
        (await FluentActions.Awaiting(() => svc.UpdateAsync("ghost", new WindowBody("a", [1], "01:00", "02:00", "UTC", null), Admin, default)).Should().ThrowAsync<AppException>()).Which.Status.Should().Be(404);

        store.UsedBySchedule = _ => true;
        (await FluentActions.Awaiting(() => svc.DeleteAsync(w.Id, Admin, default)).Should().ThrowAsync<AppException>()).Which.Status.Should().Be(409);
        store.UsedBySchedule = _ => false;
        (await svc.DeleteAsync(w.Id, Admin, default)).Ok.Should().BeTrue();
    }

    // ---- schedules ------------------------------------------------------------------------------------------------------------

    private static ScheduleBody Sched(string cron = "30 2 * * *", string tz = "Europe/Berlin", string job = "update", SelectorBody? sel = null, string mode = "manual", string? window = null) =>
        new("nightly", cron, tz, job, sel ?? new SelectorBody("shop", null, null), mode, window, null);

    [Fact]
    public async Task Schedule_next_runs_are_dst_safe_the_skipped_local_time_fires_once_at_the_first_valid_instant()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2027-03-26T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new MemScheduleStore();
        var svc = new ScheduleService(store, new MemWindowStore(), new RecordingGateway(Safety()), clock);
        var s = await svc.CreateAsync(Sched(), Admin, default);

        // 02:30 does not exist on 2027-03-28 in Berlin: it runs once, at 03:00 CEST (= 01:00Z); other days at 02:30 local
        s.NextRuns.Should().Equal("2027-03-27T01:30:00.000Z", "2027-03-28T01:00:00.000Z", "2027-03-29T00:30:00.000Z");
        store.Rows.Single().CursorUtc.Should().Be(clock.GetUtcNow().ToUnixTimeMilliseconds(), "a new schedule never fires for the past");
    }

    [Fact]
    public async Task Schedule_validation_and_selector_rules()
    {
        var svc = new ScheduleService(new MemScheduleStore(), new MemWindowStore(), new RecordingGateway(Safety()), TimeProvider.System);
        async Task<int> StatusOf(ScheduleBody b) => (await FluentActions.Awaiting(() => svc.CreateAsync(b, Admin, default)).Should().ThrowAsync<AppException>()).Which.Status;

        (await StatusOf(Sched(cron: "99 * * * *"))).Should().Be(400);
        (await StatusOf(Sched(tz: "Mars/Olympus"))).Should().Be(400);
        (await StatusOf(Sched(window: "11111111-1111-1111-1111-111111111111"))).Should().Be(400);
        (await StatusOf(Sched(sel: new SelectorBody(null, null, null)))).Should().Be(400);
        (await StatusOf(Sched(sel: new SelectorBody(null, "solo", null), mode: "auto"))).Should().Be(400);

        (await svc.CreateAsync(Sched("0 4 * * 1", "UTC", "cleanup_report", new SelectorBody(null, null, null), "auto") with { Name = "c" }, Admin, default)).Selector.Should().BeEmpty();
        var withServices = await svc.CreateAsync(Sched(sel: new SelectorBody("shop", null, ["web", "db"])) with { Name = "d" }, Admin, default);
        withServices.Selector["services"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("web", "db");
        (await StatusOf(Sched() with { Name = "c" })).Should().Be(409, "the name is taken");
    }
}

public class PlannerInputsTests
{
    [Fact]
    public void Update_order_follows_the_dependency_graph_of_a_resolved_compose_config()
    {
        var config = JsonNode.Parse("""
        {"services":{
          "web":{"image":"nginx","depends_on":{"api":{"condition":"service_healthy"}}},
          "api":{"image":"api","depends_on":["db","cache"]},
          "db":{"image":"postgres"},
          "cache":{"image":"redis"},
          "sidecar":{"image":"x","network_mode":"service:db"}
        }}
        """)!.AsObject();
        var graph = DependencyGraphBuilder.Build(config);
        graph.Cycle.Should().BeNull();

        var (order, cycle) = ServiceOrdering.Order(
            [new OrderNode("web", null), new OrderNode("api", null), new OrderNode("db", null), new OrderNode("cache", null), new OrderNode("sidecar", null)],
            graph.Edges);
        cycle.Should().BeNull();
        order.ToList().IndexOf("db").Should().BeLessThan(order.ToList().IndexOf("api"));
        order.ToList().IndexOf("cache").Should().BeLessThan(order.ToList().IndexOf("api"));
        order.ToList().IndexOf("api").Should().BeLessThan(order.ToList().IndexOf("web"));
        order.ToList().IndexOf("db").Should().BeLessThan(order.ToList().IndexOf("sidecar"), "namespace edges order the same way");
        order.Should().Equal("cache", "db", "api", "sidecar", "web"); // ties: updateOrder hint, then name
    }

    [Fact]
    public void A_dependency_cycle_in_the_compose_config_is_reported()
    {
        var config = JsonNode.Parse("""{"services":{"a":{"image":"x","depends_on":["b"]},"b":{"image":"x","depends_on":["a"]}}}""")!.AsObject();
        var graph = DependencyGraphBuilder.Build(config);
        graph.Cycle.Should().NotBeNull();
        ServiceOrdering.Order([new OrderNode("a", null), new OrderNode("b", null)], graph.Edges).Cycle.Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public void Drift_is_detected_per_service_from_the_running_config_hash_label()
    {
        ContainerInfo C(string svc, string? hash) => new(
            svc + "1", svc, "img", "sha256:1", "running", "Up", 0, [],
            new Dictionary<string, string> { [Quaymaster.Domain.Docker.ComposeLabels.Service] = svc, [Quaymaster.Domain.Docker.ComposeLabels.ConfigHash] = hash ?? string.Empty },
            [], new Dictionary<string, string?>());
        var wanted = new Dictionary<string, string> { ["web"] = "aaa", ["db"] = "bbb", ["new"] = "ccc" };
        var running = new[] { C("web", "aaa"), C("db", "old"), C("db", "bbb") };

        var drift = ComposeService.ComputeDrift(["web", "db", "new"], wanted, running);

        drift.Should().ContainSingle();
        drift[0].Service.Should().Be("db");
        drift[0].Running.Should().BeEquivalentTo("old", "bbb");
        drift[0].Wanted.Should().Be("bbb");
    }

    [Fact]
    public void An_approved_plan_goes_stale_when_digest_or_drift_changes_and_the_diff_says_why()
    {
        static Plan P(string toDigest, string? configHash) => new()
        {
            Target = "shop",
            CreatedAt = "2027-01-01T00:00:00.000Z",
            Order = ["web"],
            Services =
            [
                new PlanService
                {
                    Service = "web", Role = "update", Containers = [new PlanContainer("1", "web")], Ref = "nginx:1.27", FromImageId = "sha256:a", ToDigest = toDigest,
                    ConfigHash = configHash, WasRunning = true, OneShot = false, AutoRollback = true, AutoRollbackReason = "r", Health = new PlanHealth("stability", 60, 15, null),
                    Downtime = "d", DataRisk = new DataRisk(false, [], "n"), Critical = false, RequireApproval = true, UpdateMode = "notify",
                    Unattended = new Unattended(false, []), Quarantined = false,
                },
            ],
            Excluded = [],
            Preflight = new PlanPreflight(true, []),
            Validation = new PlanValidation(true, [], []),
            Drift = [],
            Impact = new PlanImpact([], []),
            Disk = new PlanDisk(null, null, 0, true),
            Warnings = [],
            PlanHash = string.Empty,
        };

        var approved = P("sha256:1", "h1");
        var same = approved with { CreatedAt = "2027-02-02T00:00:00.000Z", Disk = new PlanDisk(1, 2, 3, false) };
        PlanHasher.Compute(same).Should().Be(PlanHasher.Compute(approved), "timestamps and disk numbers are volatile");
        PlanHasher.Compute(P("sha256:2", "h1")).Should().NotBe(PlanHasher.Compute(approved));
        PlanHasher.Compute(P("sha256:1", "h2")).Should().NotBe(PlanHasher.Compute(approved));
        PlanHasher.DescribeDiff(approved, P("sha256:2", "h1")).Should().Contain("web");
    }
}

using FluentAssertions;
using Xunit;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Streaming;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Redaction;
using Quaymaster.Domain.Safety;
using Quaymaster.Domain.Stats;

namespace Quaymaster.UnitTests.Docker;

public class RedactionTests
{
    [Fact]
    public void EnvEntries_with_secret_names_or_secret_looking_values_are_masked()
    {
        Redactor.RedactEnvEntry("DB_PASSWORD=hunter2").Should().Be($"DB_PASSWORD={Redactor.Mask}");
        Redactor.RedactEnvEntry("TZ=Europe/Berlin").Should().Be("TZ=Europe/Berlin");
        Redactor.RedactEnvEntry("TZ=Europe/Berlin", new RedactOptions(RedactAllEnv: true)).Should().Be($"TZ={Redactor.Mask}");
        Redactor.RedactEnvEntry("X=ghp_abcdefghijklmnopqrstuvwxyz0123").Should().Be($"X={Redactor.Mask}");
    }

    [Fact]
    public void RedactDeep_masks_secrets_at_every_depth_and_keeps_bookkeeping_labels()
    {
        var raw = JsonNode.Parse("""
            {"Config":{"Env":["API_TOKEN=abc","PATH=/usr/bin"],"Labels":{"com.docker.compose.config-hash":"deadbeefdeadbeef","db.password":"s3cret-value"}},
             "State":{"Pid":42}}
            """)!;
        var red = Redactor.RedactDeep(raw)!;
        red["Config"]!["Env"]![0]!.GetValue<string>().Should().Be($"API_TOKEN={Redactor.Mask}");
        red["Config"]!["Env"]![1]!.GetValue<string>().Should().Be("PATH=/usr/bin");
        red["Config"]!["Labels"]!["com.docker.compose.config-hash"]!.GetValue<string>().Should().Be("deadbeefdeadbeef");
        red["Config"]!["Labels"]!["db.password"]!.GetValue<string>().Should().Be(Redactor.Mask);
        red["State"]!["Pid"]!.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public void Urls_lose_userinfo_and_secret_query_parameters()
    {
        Redactor.RedactUrl("https://user:pw@example.org/path?token=abc&x=1")
            .Should().Be($"https://{Redactor.Mask}@example.org/path?token={Redactor.Mask}&x=1");
    }

    [Fact]
    public void Scrubber_removes_learned_values_in_all_encodings_but_ignores_short_ones()
    {
        var s = new Scrubber();
        s.Add("correct-horse-battery");
        s.Add("short");
        s.Scrub("a correct-horse-battery b short").Should().Be($"a {Redactor.Mask} b short");
    }
}

public class SafetyPolicyTests
{
    private static SafetyPolicy Policy(bool ro = false, string[]? scope = null, string[]? prot = null, SelfInfo? self = null)
    {
        var p = new SafetyPolicy(new SafetyOptions(ro, scope ?? [], [], prot ?? []));
        if (self is not null)
        {
            p.Self = self;
        }

        return p;
    }

    private static readonly string Id1 = new('a', 64);
    private static readonly string Id2 = new('b', 64);

    [Theory]
    [InlineData(false, "", "", "web", null, null)]
    [InlineData(true, "", "", "web", RefusalCode.ReadOnly, null)]
    [InlineData(false, "app-*", "", "db", RefusalCode.OutOfScope, null)]
    [InlineData(false, "app-*", "", "app-web", null, null)]
    [InlineData(false, "", "db", "db", RefusalCode.SelfProtected, null)]
    [InlineData(true, "app-*", "db", "db", RefusalCode.ReadOnly, null)] // read-only wins over everything
    public void Container_decision_table(bool ro, string scope, string prot, string name, RefusalCode? expected, string? project)
    {
        var p = Policy(ro, scope.Length > 0 ? [scope] : [], prot.Length > 0 ? [prot] : []);
        p.ContainerRefusal(new ContainerRef(Id1, name, project))?.Code.Should().Be(expected);
    }

    [Fact]
    public void Own_container_is_protected_and_unidentified_self_makes_everything_read_only()
    {
        var self = SelfInfo.None with { InContainer = true, ContainerId = Id1, ContainerName = "quaymaster" };
        Policy(self: self).ContainerRefusal(new ContainerRef(Id1, "quaymaster", null))!.Code.Should().Be(RefusalCode.SelfProtected);
        Policy(self: self).ContainerRefusal(new ContainerRef(Id2, "other", null)).Should().BeNull();
        var lost = SelfInfo.None with { InContainer = true, Failure = "no source" };
        Policy(self: lost).ContainerRefusal(new ContainerRef(Id2, "other", null))!.Code.Should().Be(RefusalCode.ReadOnly);
    }

    [Fact]
    public void Project_refusals_cover_scope_references_and_own_project()
    {
        var self = SelfInfo.None with { InContainer = true, ContainerId = Id1, Project = "quaymaster", Volumes = ["qm_data"] };
        var p = Policy(scope: ["media*", "quay*", "qm*"], self: self);
        p.ProjectRefusal("quaymaster")!.Code.Should().Be(RefusalCode.SelfProtected);
        p.ProjectRefusal("quaymaster", perService: true).Should().BeNull();
        p.ProjectRefusal("other")!.Code.Should().Be(RefusalCode.OutOfScope);
        p.ProjectRefusal("media", new ProjectRefs(["external_vol"], [], []))!.Code.Should().Be(RefusalCode.OutOfScope);
        p.ProjectRefusal("media", new ProjectRefs(["qm_data"], [], []))!.Code.Should().Be(RefusalCode.SelfProtected);
    }
}

public class ComposeArgvTests
{
    private static readonly ComposeTarget Target = new("shop", "/srv/shop", ["/srv/shop/compose.yaml"]);

    [Fact]
    public void Down_never_contains_volume_or_orphan_flags_and_services_follow_a_separator()
    {
        var down = ComposeArgv.Build(Target, new ComposeCommand.Down());
        down.Should().NotContain(["-v", "--volumes", "--remove-orphans", "--rmi"]);
        var up = ComposeArgv.Build(Target, new ComposeCommand.Up(["web"], true, false));
        up.Should().ContainInOrder("--detach", "--no-build", "--pull=never", "--no-deps", "--", "web");
        up.Should().Contain("--project-name=shop");
    }

    [Theory]
    [InlineData("-rf")]
    [InlineData("web;rm")]
    [InlineData("")]
    public void Service_names_are_validated(string service) =>
        FluentActions.Invoking(() => ComposeArgv.Build(Target, new ComposeCommand.StopServices([service])))
            .Should().Throw<AppException>().Where(e => e.Code == ErrorCode.Validation);

    [Fact]
    public void Forbidden_flags_and_subcommands_are_rejected_last()
    {
        FluentActions.Invoking(() => ComposeArgv.AssertSafe(["compose", "up", "-v"])).Should().Throw<AppException>();
        FluentActions.Invoking(() => ComposeArgv.AssertSafe(["compose", "pull"])).Should().Throw<AppException>();
        FluentActions.Invoking(() => ComposeArgv.AssertSafe(["compose", "up", "--force-recreate"])).Should().Throw<AppException>();
        ComposeArgv.AssertSafe(["compose", "up", "--force-recreate"], allowForceRecreate: true);
    }

    [Fact]
    public void Relative_paths_are_refused() =>
        FluentActions.Invoking(() => ComposeArgv.Build(new ComposeTarget("shop", "srv", ["compose.yaml"]), new ComposeCommand.Down()))
            .Should().Throw<AppException>();
}

public class DriftAndGraphTests
{
    private static ContainerInfo C(string service, string hash) => new(
        new string('c', 64), "c", "img", "sha256:x", "running", "Up", 0, [],
        new Dictionary<string, string> { [ComposeLabels.Service] = service, [ComposeLabels.Project] = "p", [ComposeLabels.ConfigHash] = hash },
        [], new Dictionary<string, string?>());

    [Fact]
    public void Drift_is_reported_only_when_a_deployed_container_hash_differs()
    {
        var wanted = ComposeService.ParseHashes("web  aaaaaaaaaaaaaaaa\ndb bbbbbbbbbbbbbbbb\n");
        wanted.Should().HaveCount(2);
        var running = new[] { C("web", "aaaaaaaaaaaaaaaa"), C("db", "ffffffffffffffff") };
        var drift = ComposeService.ComputeDrift(["web", "db", "cache"], wanted, running);
        drift.Should().ContainSingle().Which.Service.Should().Be("db");
        ComposeService.ComputeDrift(["web"], wanted, running).Should().BeEmpty();
    }

    [Fact]
    public void Graph_orders_dependencies_first_and_finds_namespace_providers()
    {
        var cfg = JsonNode.Parse("""{"services":{"app":{"depends_on":["db"]},"db":{},"sidecar":{"network_mode":"service:app"}}}""")!.AsObject();
        var g = Quaymaster.Domain.Compose.DependencyGraphBuilder.Build(cfg);
        g.Order.Should().ContainInOrder("db", "app");
        Quaymaster.Domain.Compose.DependencyGraphBuilder.NamespaceProviders(g)["app"].Should().Equal("sidecar");
        var cyc = JsonNode.Parse("""{"services":{"a":{"depends_on":["b"]},"b":{"depends_on":["a"]}}}""")!.AsObject();
        Quaymaster.Domain.Compose.DependencyGraphBuilder.Build(cyc).Cycle.Should().NotBeNull();
    }
}

public class RefAndVersionTests
{
    [Theory]
    [InlineData("web", RefKind.Container, true)]
    [InlineData("../etc", RefKind.Container, false)]
    [InlineData("a/b", RefKind.Container, false)]
    [InlineData("nginx:1.27", RefKind.Image, true)]
    [InlineData("reg.io/team/app@sha256:abc", RefKind.Image, true)]
    [InlineData("a/../b", RefKind.Image, false)]
    [InlineData("-rf", RefKind.Volume, false)]
    public void Safe_references(string value, RefKind kind, bool ok) => DockerNames.IsSafeRef(value, kind).Should().Be(ok);

    [Fact]
    public void Api_version_negotiation_picks_min_and_refuses_extremes()
    {
        ApiVersioning.Negotiate("1.54", "1.24").Should().Be("1.54");
        ApiVersioning.Negotiate("1.60", "1.24").Should().Be("1.55");
        FluentActions.Invoking(() => ApiVersioning.Negotiate("1.30", null)).Should().Throw<AppException>();
        FluentActions.Invoking(() => ApiVersioning.Negotiate("1.99", "1.70")).Should().Throw<AppException>();
    }
}

public class StatsMathTests
{
    private static RawStats Raw(long ms, long cpuNs, long mem = 1000, long inactive = 200) =>
        new(DateTimeOffset.FromUnixTimeMilliseconds(ms), cpuNs, mem, 4000, inactive, 10, 20, 30, 40);

    [Fact]
    public void Cpu_percent_is_delta_cpu_over_delta_wall_and_memory_excludes_inactive_file()
    {
        var first = StatsMath.ToSample(Raw(0, 0), null);
        first.Point.Cpu.Should().Be(0);
        var second = StatsMath.ToSample(Raw(2000, 1_000_000_000), first); // 1 s of CPU in 2 s = 50 %
        second.Point.Cpu.Should().Be(50);
        second.Point.MemUsage.Should().Be(800);
    }

    [Fact]
    public void Minute_rollup_averages_and_keeps_maxima_and_last_counters()
    {
        var a = StatsMath.ToSample(Raw(60_000, 0), null);
        var acc = new MinuteAccumulator(1, "web", a);
        acc.Add(new Sample(a.Point with { Cpu = 10, MemUsage = 100 }, 0));
        acc.Add(new Sample(a.Point with { Cpu = 30, MemUsage = 300, NetRx = 99 }, 0));
        var row = acc.ToRow("id");
        row.Samples.Should().Be(2);
        row.CpuAvg.Should().Be(20);
        row.CpuMax.Should().Be(30);
        row.MemAvg.Should().Be(200);
        row.MemMax.Should().Be(300);
        row.NetRx.Should().Be(99);
    }
}

public class SseBackpressureTests
{
    [Fact]
    public void A_slow_client_is_closed_with_an_end_event_instead_of_buffering_without_bound()
    {
        using var conn = new SseConnection(new SseLimits(200, TimeSpan.FromSeconds(15)), TimeProvider.System);
        conn.Send("log", new { text = new string('x', 100) }).Should().BeTrue();
        conn.Send("log", new { text = new string('x', 500) }).Should().BeFalse();
        conn.IsClosed.Should().BeTrue();
        conn.Send("log", new { text = "late" }).Should().BeFalse();
        var frames = new List<SseFrame>();
        while (conn.Frames.TryRead(out var f))
        {
            frames.Add(f);
        }

        frames.Last().Event.Should().Be("end");
        frames.Last().Data.Should().Contain("client too slow");
    }

    [Fact]
    public void Heartbeats_are_comment_frames_and_close_runs_callbacks_once()
    {
        var time = new FakeTimeProvider();
        using var conn = new SseConnection(SseLimits.Default, time);
        var closed = 0;
        conn.OnClose(() => closed++);
        time.Advance(TimeSpan.FromSeconds(16));
        conn.Frames.TryRead(out var beat).Should().BeTrue();
        beat!.ToWire().Should().Be(": ping\n\n");
        conn.Close("done");
        conn.Close("again");
        closed.Should().Be(1);
        conn.Closed.IsCancellationRequested.Should().BeTrue();
    }
}

public class EventSanitizerTests
{
    [Fact]
    public void Exec_events_and_unknown_actions_are_dropped_and_labels_never_leak()
    {
        var scrubber = new Scrubber();
        var exec = JsonNode.Parse("""{"Type":"container","Action":"exec_start: sh -c 'echo secret'","Actor":{"ID":"x","Attributes":{}}}""")!.AsObject();
        Quaymaster.Application.Events.EventSanitizer.Sanitize(exec, scrubber, 0).Should().BeNull();
        var die = JsonNode.Parse("""{"Type":"container","Action":"die","Actor":{"ID":"abc","Attributes":{"name":"web","exitCode":"1","com.docker.compose.project":"shop","MY_SECRET":"zzz"}},"timeNano":1700000000000000000}""")!.AsObject();
        var ev = Quaymaster.Application.Events.EventSanitizer.Sanitize(die, scrubber, 0)!;
        ev.Action.Should().Be("die");
        ev.Project.Should().Be("shop");
        ev.Attributes.Should().ContainKey("exitCode").And.NotContainKey("MY_SECRET");
        ev.Ts.Should().Be(1_700_000_000_000);
    }
}

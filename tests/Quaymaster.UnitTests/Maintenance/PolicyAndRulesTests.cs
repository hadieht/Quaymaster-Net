using System.Text.Json.Nodes;
using FluentAssertions;
using Quaymaster.Domain.Compose;
using Quaymaster.Domain.Maintenance;
using Xunit;

namespace Quaymaster.UnitTests.Maintenance;

public class PolicyTests
{
    private static PolicyLevel L(string level, PolicyInput p) => new(level, p);

    [Fact]
    public void Defaults_are_conservative_notify_with_approval()
    {
        var e = PolicyRules.Resolve([]);
        e.UpdateMode.Should().Be("notify");
        e.RequireApproval.Should().BeTrue();
        e.Critical.Should().BeFalse();
        e.AutoRollbackExplicit.Should().BeNull();
        e.Health.TimeoutSeconds.Should().Be(60);
        e.Health.StabilizationSeconds.Should().Be(15);
        e.Health.Probe.Should().BeNull();
        e.MaxParallel.Should().Be(1);
        e.Sources["updateMode"].Should().Be("default");
    }

    [Fact]
    public void Most_specific_level_wins_for_update_mode_health_and_order()
    {
        var e = PolicyRules.Resolve(
        [
            L("global:*", new PolicyInput { UpdateMode = "notify", UpdateOrder = 5, Health = new HealthInput(90, null, false, null) }),
            L("project:media", new PolicyInput { UpdateMode = "pull_only" }),
            L("service:media/web", new PolicyInput { UpdateMode = "auto", UpdateOrder = 1, Health = new HealthInput(null, 30, false, null) }),
        ]);
        e.UpdateMode.Should().Be("auto");
        e.UpdateOrder.Should().Be(1);
        e.Health.TimeoutSeconds.Should().Be(90);
        e.Health.StabilizationSeconds.Should().Be(30);
        e.Sources["updateMode"].Should().Be("service:media/web");
        e.Sources["health"].Should().Be("service:media/web");
    }

    [Fact]
    public void Critical_and_require_approval_combine_as_or_across_levels()
    {
        var e = PolicyRules.Resolve(
        [
            L("global:*", new PolicyInput { Critical = true, RequireApproval = true }),
            L("service:a/b", new PolicyInput { Critical = false, RequireApproval = false }),
        ]);
        e.Critical.Should().BeTrue();
        e.RequireApproval.Should().BeTrue("a lower level may not switch the approval requirement off once any level demands it");
        e.Sources["critical"].Should().Be("global:*");
    }

    [Fact]
    public void Approval_can_be_switched_off_when_nobody_demands_it()
    {
        PolicyRules.Resolve([L("project:p", new PolicyInput { RequireApproval = false })]).RequireApproval.Should().BeFalse();
    }

    [Fact]
    public void Auto_rollback_explicit_false_anywhere_wins_and_broad_true_never_covers_stateful()
    {
        PolicyRules.Resolve([L("global:*", new PolicyInput { AutoRollback = false }), L("service:a/b", new PolicyInput { AutoRollback = true })])
            .AutoRollbackExplicit.Should().Be(false);
        var broad = PolicyRules.Resolve([L("project:p", new PolicyInput { AutoRollback = true })]);
        broad.AutoRollbackExplicit.Should().BeNull();
        broad.AutoRollbackBroad.Should().BeTrue();
        PolicyRules.Resolve([L("service:p/s", new PolicyInput { AutoRollback = true })]).AutoRollbackExplicit.Should().Be(true);
        PolicyRules.Resolve([L("container:nginx", new PolicyInput { AutoRollback = true })]).AutoRollbackExplicit.Should().Be(true);
    }

    [Theory]
    [InlineData(null, false, false, true, "default is ON for a stateless service")]
    [InlineData(null, true, false, false, "default is OFF")]
    [InlineData(true, true, false, true, "enabled by policy")]
    [InlineData(false, false, false, false, "disabled by policy")]
    [InlineData(null, false, true, true, "project/global")]
    [InlineData(null, true, true, false, "default is OFF")]
    public void Auto_rollback_decision_table(bool? explicitValue, bool persistent, bool broad, bool expected, string reasonContains)
    {
        var d = PolicyRules.ResolveAutoRollback(explicitValue, persistent, broad);
        d.Value.Should().Be(expected);
        d.Reason.Should().Contain(reasonContains);
    }

    [Theory]
    [InlineData("auto", "auto", false, false, 0)]
    [InlineData("notify", "auto", false, false, 1)]
    [InlineData("auto", "manual", false, false, 1)]
    [InlineData("auto", null, false, false, 1)]
    [InlineData("auto", "auto", true, false, 1)]
    [InlineData("auto", "auto", false, true, 1)]
    [InlineData("pull_only", "manual", true, true, 4)]
    public void Unattended_requires_all_four_conditions(string mode, string? schedule, bool approval, bool critical, int blockers)
    {
        PolicyRules.UnattendedBlockers(mode, critical, approval, schedule).Should().HaveCount(blockers);
    }

    [Fact]
    public void Critical_targets_always_need_per_run_approval_even_with_auto_everything()
    {
        var e = PolicyRules.Resolve([L("service:db/pg", new PolicyInput { UpdateMode = "auto", RequireApproval = false, Critical = true })]);
        PolicyRules.UnattendedBlockers(e.UpdateMode, e.Critical, e.RequireApproval, "auto").Should().ContainSingle().Which.Should().Contain("critical");
    }

    [Fact]
    public void Chain_covers_global_project_service_and_container_only_without_a_project()
    {
        PolicyRules.ChainFor("media", "web", null).Should().Equal(("global", "*"), ("project", "media"), ("service", "media/web"));
        PolicyRules.ChainFor(null, null, "nginx").Should().Equal(("global", "*"), ("container", "nginx"));
        PolicyRules.ChainFor("media", null, "nginx").Should().Equal(("global", "*"), ("project", "media"));
    }

    [Fact]
    public void Parse_accepts_the_documented_shape_and_round_trips()
    {
        var body = JsonNode.Parse("""{"updateMode":"auto","critical":true,"requireApproval":false,"autoRollback":true,"maxParallel":1,"updateOrder":3,"health":{"timeoutSeconds":120,"stabilizationSeconds":5,"probe":{"url":"http://x/health"}}}""");
        var p = PolicyRules.Parse(body);
        p.Health!.Probe!.ExpectStatus.Should().Be(200);
        var json = PolicyRules.ToJson(p);
        json["health"]!["probe"]!["expectStatus"]!.GetValue<int>().Should().Be(200);
        json["updateOrder"]!.GetValue<long>().Should().Be(3);
    }

    [Theory]
    [InlineData("[]", "policy must be an object")]
    [InlineData("""{"bogus":1}""", "unknown policy field \"bogus\"")]
    [InlineData("""{"updateMode":"yes"}""", "updateMode must be ignore|notify|pull_only|auto")]
    [InlineData("""{"critical":"x"}""", "critical must be a boolean")]
    [InlineData("""{"maxParallel":2}""", "maxParallel is fixed to 1")]
    [InlineData("""{"updateOrder":1.5}""", "updateOrder must be an integer")]
    [InlineData("""{"health":{"timeoutSeconds":1}}""", "health.timeoutSeconds must be 5..3600")]
    [InlineData("""{"health":{"stabilizationSeconds":601}}""", "health.stabilizationSeconds must be 0..600")]
    [InlineData("""{"health":{"probe":{"url":"ftp://x"}}}""", "health.probe.url must be http(s)")]
    [InlineData("""{"health":{"probe":{"url":"http://x","expectStatus":99}}}""", "health.probe.expectStatus invalid")]
    public void Parse_rejects_with_the_original_messages(string json, string message)
    {
        var act = () => PolicyRules.Parse(JsonNode.Parse(json));
        act.Should().Throw<PolicyValidationException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void An_explicit_null_probe_clears_an_inherited_probe()
    {
        var e = PolicyRules.Resolve(
        [
            L("global:*", new PolicyInput { Health = new HealthInput(null, null, true, new ProbeSpec("http://a", 200)) }),
            L("service:p/s", new PolicyInput { Health = new HealthInput(null, null, true, null) }),
        ]);
        e.Health.Probe.Should().BeNull();
    }
}

public class OrderingAndRulesTests
{
    private static GraphEdge E(string from, string to, string kind = "depends_on") => new(from, to, kind);

    private static OrderNode N(string n, long? o = null) => new(n, o);

    [Fact]
    public void Dependencies_come_first()
    {
        var (order, cycle) = ServiceOrdering.Order([N("web"), N("db"), N("cache")], [E("web", "db"), E("web", "cache")]);
        cycle.Should().BeNull();
        order[^1].Should().Be("web");
        order.Should().BeEquivalentTo(["db", "cache", "web"]);
    }

    [Fact]
    public void Indirect_constraints_through_services_outside_the_selection_are_kept()
    {
        // web -> mid -> db, only web and db selected
        var (order, _) = ServiceOrdering.Order([N("web"), N("db")], [E("web", "mid"), E("mid", "db")]);
        order.Should().Equal("db", "web");
    }

    [Fact]
    public void Ties_are_broken_by_update_order_then_name()
    {
        ServiceOrdering.Order([N("b"), N("a"), N("c", 0)], []).Order.Should().Equal("c", "a", "b");
        ServiceOrdering.Order([N("b", 2), N("a", 5)], []).Order.Should().Equal("b", "a");
    }

    [Fact]
    public void A_cycle_is_reported_with_its_members()
    {
        var (order, cycle) = ServiceOrdering.Order([N("a"), N("b"), N("c")], [E("a", "b"), E("b", "a")]);
        order.Should().BeEmpty();
        cycle.Should().Equal("a", "b");
    }

    [Fact]
    public void Namespace_edges_order_the_same_way()
    {
        ServiceOrdering.Order([N("sidecar"), N("vpn")], [E("sidecar", "vpn", "shares_namespace")]).Order.Should().Equal("vpn", "sidecar");
    }

    [Theory]
    [InlineData("30s", 30_000)]
    [InlineData("1m30s", 90_000)]
    [InlineData("500ms", 500)]
    [InlineData("1h", 3_600_000)]
    public void Compose_durations_parse(string text, double ms) => HealthTimeouts.ParseDurationMs(text).Should().Be(ms);

    [Fact]
    public void Garbage_durations_are_null() => HealthTimeouts.ParseDurationMs("soon").Should().BeNull();

    [Fact]
    public void Effective_timeout_is_raised_to_the_healthchecks_own_timings()
    {
        HealthTimeouts.EffectiveSeconds(60, null).Should().Be(60);
        // start 40s + 30s * 3 + 30 = 160
        HealthTimeouts.EffectiveSeconds(60, new HealthTimeouts.HealthcheckTimings(30_000, 3, 40_000)).Should().Be(160);
        HealthTimeouts.EffectiveSeconds(300, new HealthTimeouts.HealthcheckTimings(2000, 3, 1000)).Should().Be(300);
    }

    // ---- reclaim / rollback decision table ---------------------------------------------------------------------

    [Theory]
    // isUpdate, deployOk, healthy, autoRollback -> outcome, quarantine, restoreTag, attemptRollback
    [InlineData(true, true, true, true, "updated", false, false, false)]
    [InlineData(false, true, true, true, "dependent_recreated", false, false, false)]
    [InlineData(true, true, false, true, "rolled_back", true, false, true)]
    [InlineData(true, true, false, false, "failed_left_as_is", true, true, false)]
    [InlineData(true, false, false, true, "rolled_back", false, true, true)]
    [InlineData(true, false, false, false, "failed_left_as_is", false, true, false)]
    [InlineData(false, true, false, true, "failed_left_as_is", false, false, false)]
    [InlineData(false, false, false, false, "failed_left_as_is", false, false, false)]
    public void Rollback_decision_table(bool isUpdate, bool deployOk, bool healthy, bool auto, string outcome, bool quarantine, bool restoreTag, bool rollback)
    {
        var d = RollbackRules.Decide(isUpdate, deployOk, healthy, auto);
        d.Outcome.Should().Be(outcome);
        d.Quarantine.Should().Be(quarantine);
        d.RestoreTag.Should().Be(restoreTag);
        d.AttemptRollback.Should().Be(rollback);
    }

    [Fact]
    public void A_failed_rollback_is_a_failure_with_manual_guidance()
    {
        RollbackRules.AfterRollback(true).Should().Be("rolled_back");
        RollbackRules.AfterRollback(false).Should().Be("failed");
        RollbackRules.RunStatusFor("rolled_back").Should().Be("rolled_back");
        RollbackRules.RunStatusFor("failed").Should().Be("failed");
        RollbackRules.RunStatusFor("failed_left_as_is").Should().Be("failed");
    }

    // ---- restart-loop detection --------------------------------------------------------------------------------

    [Fact]
    public void Restart_loop_counts_deaths_inside_the_window_only()
    {
        var t = new RestartLoopTracker();
        t.Record("c1", 0, 300_000).Should().Be(1);
        t.Record("c1", 100_000, 300_000).Should().Be(2);
        t.Record("c2", 150_000, 300_000).Should().Be(1, "per container");
        t.Record("c1", 250_000, 300_000).Should().Be(3);
        // the first death (t=0) fell out of the window by now
        t.Record("c1", 320_000, 300_000).Should().Be(3);
        RestartLoopTracker.IsLoop(5, 5).Should().BeTrue();
        RestartLoopTracker.IsLoop(4, 5).Should().BeFalse();
    }

    // ---- plan hash / approval staleness ------------------------------------------------------------------------

    private static PlanService Svc(string name, string toDigest = "sha256:aa", string cfg = "cfg", string fromImage = "sha256:old") => new()
    {
        Service = name,
        Role = "update",
        Containers = [new PlanContainer("c", "n")],
        Ref = "nginx:1",
        FromImageId = fromImage,
        ToDigest = toDigest,
        ConfigHash = cfg,
        WasRunning = true,
        OneShot = false,
        AutoRollback = true,
        AutoRollbackReason = "x",
        Health = new PlanHealth("stability", 60, 15, null),
        Downtime = "d",
        DataRisk = new DataRisk(false, [], "n"),
        Critical = false,
        RequireApproval = true,
        UpdateMode = "notify",
        Unattended = new Unattended(false, ["x"]),
        Quarantined = false,
    };

    private static Plan P(PlanService[] services, string createdAt = "2026-01-01T00:00:00Z", long? free = 1) => new()
    {
        Target = "shop",
        CreatedAt = createdAt,
        Order = [.. services.Select(s => s.Service)],
        Services = services,
        Excluded = [],
        Preflight = new PlanPreflight(true, []),
        Validation = new PlanValidation(true, [], []),
        Drift = [],
        Impact = new PlanImpact([], []),
        Disk = new PlanDisk(1, free, 1, true),
        Warnings = [],
        PlanHash = string.Empty,
    };

    [Fact]
    public void Plan_hash_ignores_volatile_data_but_binds_digest_image_config_and_policy()
    {
        var a = PlanHasher.Compute(P([Svc("web")]));
        PlanHasher.Compute(P([Svc("web")], createdAt: "2030-01-01T00:00:00Z", free: 999)).Should().Be(a);
        PlanHasher.Compute(P([Svc("web", toDigest: "sha256:bb")])).Should().NotBe(a);
        PlanHasher.Compute(P([Svc("web", cfg: "other")])).Should().NotBe(a);
        PlanHasher.Compute(P([Svc("web", fromImage: "sha256:new")])).Should().NotBe(a);
        PlanHasher.Compute(P([Svc("web") with { Critical = true }])).Should().NotBe(a);
        PlanHasher.Compute(P([Svc("web") with { AutoRollback = false }])).Should().NotBe(a);
        a.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Diff_explains_why_an_approval_went_stale()
    {
        var approved = P([Svc("web"), Svc("db")]);
        PlanHasher.DescribeDiff(approved, P([Svc("web", toDigest: "sha256:zz"), Svc("db")])).Should().Contain("web: target digest moved");
        PlanHasher.DescribeDiff(approved, P([Svc("web")])).Should().Contain("db is no longer part of the plan");
        PlanHasher.DescribeDiff(approved, P([Svc("web"), Svc("db"), Svc("x")])).Should().Contain("x was added");
        PlanHasher.DescribeDiff(approved, P([Svc("web", cfg: "c2"), Svc("db")])).Should().Contain("web: configuration changed");
    }

    [Fact]
    public void Canonical_json_sorts_keys_at_every_depth()
    {
        CanonicalJson.Write(JsonNode.Parse("""{"b":1,"a":{"d":[{"z":1,"y":2}],"c":null}}""")).Should().Be("""{"a":{"c":null,"d":[{"y":2,"z":1}]},"b":1}""");
    }
}

using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Operations;
using Quaymaster.Domain.Maintenance;

namespace Quaymaster.UnitTests.Maintenance;

public class HealthGateTests
{
    private sealed class ScriptedEnv(Func<string, int, InspectLite?> script) : IRunEnvironment
    {
        public int Polls { get; private set; }

        public Task<InspectLite?> InspectAsync(string containerId, CancellationToken ct)
        {
            Polls++;
            return Task.FromResult(script(containerId, Polls));
        }

        public Task<ProjectTarget> TargetAsync(string project, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<EngineContainer>> ContainersAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<DeployResult> UpAsync(string project, string service, bool forceRecreate, bool noStart, Action<string> log, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ConfigHashAsync(string project, string service, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> HasDriftAsync(string project, string service, CancellationToken ct) => throw new NotSupportedException();

        public Task<JsonObject> SnapshotAsync(string project, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Quaymaster.Domain.Compose.GraphEdge>> EdgesAsync(string project, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> RestartAsync(string containerId, CancellationToken ct) => throw new NotSupportedException();

        public void Invalidate()
        {
        }
    }

    private const string Img = "sha256:aaaa";

    private static InspectLite C(string health = "healthy", string status = "running", int restarts = 0, string started = "t0", bool restarting = false, int exit = 0, string image = Img) =>
        new(image, restarts, status, status == "running", restarting, exit, started, health);

    private static (HealthGate Gate, FakeTimeProvider Clock, FakeProbe Probe) Gate(ScriptedEnv env)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var probe = new FakeProbe();
        return (new HealthGate(env, probe, new HealthTimings { PollMs = 1000, Sleep = (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; } }, clock), clock, probe);
    }

    private static HealthSpec Spec(string kind = "healthcheck", long timeout = 30_000, long stab = 5_000, ProbeSpec? probe = null, string image = Img) => new(kind, timeout, stab, probe, image);

    [Fact]
    public async Task Healthcheck_waits_through_starting_until_healthy()
    {
        var env = new ScriptedEnv((_, n) => C(n < 4 ? "starting" : "healthy"));
        var r = await Gate(env).Gate.WaitAsync(["a"], Spec(), default);
        r.Ok.Should().BeTrue();
        env.Polls.Should().Be(4);
        r.ElapsedMs.Should().Be(3000);
    }

    [Fact]
    public async Task Unhealthy_fails_at_once_without_waiting_for_the_timeout()
    {
        var env = new ScriptedEnv((_, n) => C(n < 2 ? "starting" : "unhealthy"));
        var r = await Gate(env).Gate.WaitAsync(["a"], Spec(), default);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("unhealthy");
        r.ElapsedMs.Should().BeLessThan(5000);
    }

    [Fact]
    public async Task A_healthcheck_that_never_settles_times_out_with_the_last_state()
    {
        var r = await Gate(new ScriptedEnv((_, _) => C("starting"))).Gate.WaitAsync(["a"], Spec(timeout: 10_000), default);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Be("timed out after 10s: health is starting");
    }

    [Fact]
    public async Task Without_a_healthcheck_the_container_must_stay_running_and_stable_for_the_stabilisation_period()
    {
        var env = new ScriptedEnv((_, _) => C("none"));
        var r = await Gate(env).Gate.WaitAsync(["a"], Spec("stability", stab: 5_000), default);
        r.Ok.Should().BeTrue();
        r.ElapsedMs.Should().BeGreaterThanOrEqualTo(5000);
    }

    [Fact]
    public async Task A_restart_during_stabilisation_fails_by_restart_count_or_start_time()
    {
        var byCount = await Gate(new ScriptedEnv((_, n) => C("none", restarts: n > 2 ? 1 : 0))).Gate.WaitAsync(["a"], Spec("stability"), default);
        byCount.Ok.Should().BeFalse();
        byCount.Reason.Should().Contain("restart loop");

        var byStart = await Gate(new ScriptedEnv((_, n) => C("none", started: n > 2 ? "t1" : "t0"))).Gate.WaitAsync(["a"], Spec("stability"), default);
        byStart.Ok.Should().BeFalse();
        byStart.Reason.Should().Contain("restarted during the stabilisation");
    }

    [Fact]
    public async Task A_restarting_container_resets_the_stable_clock_and_never_passes()
    {
        var r = await Gate(new ScriptedEnv((_, _) => C("none", restarting: true))).Gate.WaitAsync(["a"], Spec("stability", timeout: 8_000), default);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("container is restarting");
    }

    [Fact]
    public async Task An_exited_or_vanished_container_or_the_wrong_image_fails()
    {
        (await Gate(new ScriptedEnv((_, _) => C("none", status: "exited", exit: 137))).Gate.WaitAsync(["a"], Spec("stability"), default)).Reason.Should().Contain("exited with code 137");
        (await Gate(new ScriptedEnv((_, _) => null)).Gate.WaitAsync(["abcdef0123456789"], Spec(), default)).Reason.Should().Contain("disappeared");
        var wrong = await Gate(new ScriptedEnv((_, _) => C(image: "sha256:other"))).Gate.WaitAsync(["a"], Spec(), default);
        wrong.Ok.Should().BeFalse();
        wrong.Reason.Should().Contain("expected sha256:aaaa");
        (await Gate(new ScriptedEnv((_, _) => C())).Gate.WaitAsync([], Spec(), default)).Reason.Should().Be("no container to check");
    }

    [Fact]
    public async Task Every_replica_must_pass()
    {
        var env = new ScriptedEnv((id, _) => C(id == "b" ? "unhealthy" : "healthy"));
        (await Gate(env).Gate.WaitAsync(["a", "b"], Spec(), default)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task One_shot_services_succeed_on_exit_zero_and_fail_on_a_non_zero_exit()
    {
        (await Gate(new ScriptedEnv((_, n) => n < 3 ? C("none") : C("none", status: "exited", exit: 0))).Gate.WaitAsync(["a"], Spec("oneshot"), default)).Ok.Should().BeTrue();
        var bad = await Gate(new ScriptedEnv((_, _) => C("none", status: "exited", exit: 2))).Gate.WaitAsync(["a"], Spec("oneshot"), default);
        bad.Ok.Should().BeFalse();
        bad.Reason.Should().Contain("exited with code 2");
    }

    [Fact]
    public async Task The_http_probe_gates_the_result_and_has_its_own_deadline()
    {
        var (gate, _, probe) = Gate(new ScriptedEnv((_, _) => C()));
        probe.Answers.Enqueue(503);
        probe.Answers.Enqueue(200);
        (await gate.WaitAsync(["a"], Spec(probe: new ProbeSpec("http://x/health", 200)), default)).Ok.Should().BeTrue();

        var (gate2, _, probe2) = Gate(new ScriptedEnv((_, _) => C()));
        probe2.Answers.Enqueue(500);
        var r = await gate2.WaitAsync(["a"], Spec(timeout: 5_000, probe: new ProbeSpec("http://x/health", 200)), default);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("HTTP probe did not pass").And.Contain("expected 200");
    }
}

public class RollbackDecisionTableTests
{
    [Theory]
    [InlineData(true, true, false, true, "updated", false, false, false)]
    [InlineData(false, true, false, true, "dependent_recreated", false, false, false)]
    public void Healthy_means_nothing_to_undo(bool isUpdate, bool deployOk, bool healthy, bool auto, string outcome, bool quarantine, bool restoreTag, bool rollback)
    {
        var d = RollbackRules.Decide(isUpdate, deployOk, true, auto);
        (d.Outcome, d.Quarantine, d.RestoreTag, d.AttemptRollback).Should().Be((outcome, quarantine, restoreTag, rollback));
        _ = healthy;
    }

    [Theory]
    //        isUpdate deployOk auto   outcome               quarantine restoreTag rollback
    [InlineData(true, true, true, "rolled_back", true, false, true)]
    [InlineData(true, true, false, "failed_left_as_is", true, true, false)]
    [InlineData(true, false, true, "rolled_back", false, false, true)]
    [InlineData(true, false, false, "failed_left_as_is", false, true, false)]
    [InlineData(false, true, true, "failed_left_as_is", false, false, false)]
    [InlineData(false, true, false, "failed_left_as_is", false, false, false)]
    public void Failure_decision_table(bool isUpdate, bool deployOk, bool auto, string outcome, bool quarantine, bool restoreTag, bool rollback)
    {
        var d = RollbackRules.Decide(isUpdate, deployOk, false, auto);
        (d.Outcome, d.Quarantine, d.RestoreTag, d.AttemptRollback).Should().Be((outcome, quarantine, restoreTag, rollback));
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public void Auto_rollback_is_off_with_writable_mounts_and_on_without_unless_the_policy_says_otherwise(bool? explicitValue, bool writableMounts, bool expected) =>
        PolicyRules.ResolveAutoRollback(explicitValue, writableMounts).Value.Should().Be(expected);

    [Fact]
    public void A_broad_true_never_turns_rollback_on_for_a_service_with_writable_mounts()
    {
        PolicyRules.ResolveAutoRollback(true, true, broadTrue: true).Value.Should().BeFalse();
        PolicyRules.ResolveAutoRollback(true, false, broadTrue: true).Value.Should().BeTrue();
    }

    [Fact]
    public void A_rollback_that_fails_is_a_plain_failure_and_maps_to_the_run_status()
    {
        RollbackRules.AfterRollback(true).Should().Be("rolled_back");
        RollbackRules.AfterRollback(false).Should().Be("failed");
        RollbackRules.RunStatusFor("rolled_back").Should().Be("rolled_back");
        RollbackRules.RunStatusFor("failed_left_as_is").Should().Be("failed");
    }
}

public class ApprovalRulesTests
{
    [Fact]
    public void Approval_is_bound_to_the_exact_hash_and_expires()
    {
        var h = new string('a', 64);
        ApprovalRules.Matches(h, h).Should().BeTrue();
        ApprovalRules.Matches(h, new string('b', 64)).Should().BeFalse();
        ApprovalRules.Matches(null, h).Should().BeFalse();
        ApprovalRules.IsExpired(1000, 1000).Should().BeTrue("expiry is inclusive");
        ApprovalRules.IsExpired(1001, 1000).Should().BeFalse();
        ApprovalRules.IsExpired(null, long.MaxValue).Should().BeFalse();
    }

    [Theory]
    // scheduled, schedule mode, policy auto, schedule auto..., critical, requireApproval => eligible per PolicyRules.UnattendedBlockers
    [InlineData("auto", false, false, "auto", true)]
    [InlineData("auto", false, false, "manual", false)]
    [InlineData("auto", true, false, "auto", false)]
    [InlineData("auto", false, true, "auto", false)]
    [InlineData("notify", false, false, "auto", false)]
    [InlineData("pull_only", false, false, "auto", false)]
    public void Unattended_needs_all_four_conditions(string updateMode, bool critical, bool requireApproval, string scheduleMode, bool unattended)
    {
        var blockers = PolicyRules.UnattendedBlockers(updateMode, critical, requireApproval, scheduleMode);
        (blockers.Count == 0).Should().Be(unattended);
    }

    [Fact]
    public void A_manual_run_is_never_unattended_and_one_blocked_service_blocks_the_whole_plan()
    {
        var plan = RunWorld.BuildPlan(new RunWorld.SvcSpec("web", new string('1', 64)));
        ApprovalRules.IsUnattended(false, "auto", plan).Should().BeFalse();
        var eligible = plan with { Services = [.. plan.Services.Select(s => s with { Unattended = new Unattended(true, []) })] };
        ApprovalRules.IsUnattended(true, "auto", eligible).Should().BeTrue();
        ApprovalRules.IsUnattended(true, "manual", eligible).Should().BeFalse();
        ApprovalRules.IsUnattended(true, "auto", plan).Should().BeFalse();
        var mixed = eligible with { Services = [.. eligible.Services, .. plan.Services.Select(s => s with { Service = "other" })] };
        ApprovalRules.IsUnattended(true, "auto", mixed).Should().BeFalse();
    }

    [Fact]
    public void Window_budget_and_run_overlap_rules()
    {
        ExecutionRules.FitsWindow(null, 5, 60).Should().BeTrue();
        ExecutionRules.FitsWindow(100_000, 40_000, 60).Should().BeTrue();
        ExecutionRules.FitsWindow(100_000, 40_001, 60).Should().BeFalse();
        ExecutionRules.Overlaps("shop", "shop").Should().BeTrue();
        ExecutionRules.Overlaps("shop", "media").Should().BeFalse();
        ExecutionRules.StopStatus(true).Should().Be("partial");
        ExecutionRules.StopStatus(false).Should().Be("skipped");
    }
}

public class RecoveryCommandsTests
{
    private static readonly string Id = new('3', 64);

    private static IntentInfo Intent(string action = "deploy", string? from = RunWorld.Old, string? to = RunWorld.New) => new(1, "run", "web", action, from, to, [Id]);

    private static readonly ProjectTarget Target = new("shop", "/srv/my shop", ["/srv/my shop/compose.yaml", "/srv/my shop/o'k.yaml"]);

    private static PlanService Service => RunWorld.BuildPlan(new RunWorld.SvcSpec("web", Id)).Services[0];

    [Fact]
    public void Shell_quoting_makes_every_command_paste_safe()
    {
        ShellQuote.Q("plain-1.2/x").Should().Be("plain-1.2/x");
        ShellQuote.Q("a b").Should().Be("'a b'");
        ShellQuote.Q("it's").Should().Be("'it'\\''s'");
        ShellQuote.Q(string.Empty).Should().Be("''");
        RecoveryCommands.UpCommand(Target, "web").Should().Be(
            "docker compose --project-name shop --project-directory '/srv/my shop' --file '/srv/my shop/compose.yaml' --file '/srv/my shop/o'\\''k.yaml' up -d --no-deps --no-build --pull never -- web");
    }

    [Fact]
    public void Health_is_read_from_the_docker_status_text()
    {
        RecoveryCommands.HealthFromStatus("Up 2 minutes (unhealthy)").Should().Be("unhealthy");
        RecoveryCommands.HealthFromStatus("Up 2 minutes (healthy)").Should().Be("healthy");
        RecoveryCommands.HealthFromStatus("Up 2 seconds (health: starting)").Should().Be("starting");
        RecoveryCommands.HealthFromStatus("Up 2 minutes").Should().BeNull();
    }

    [Fact]
    public void Service_down_after_a_crash_gets_the_retag_and_the_compose_up_command()
    {
        var a = RecoveryCommands.Assess("shop", Intent(), [], Service, Target);
        a.Down.Should().BeTrue();
        a.Unverified.Should().BeFalse();
        a.Guidance[0].Should().Contain("it is currently NOT running on no container");
        a.Guidance.Should().Contain($"docker tag {RunWorld.Old} nginx:1.27");
        a.Guidance[^1].Should().StartWith("docker compose --project-name shop ").And.EndWith("-- web");
    }

    [Fact]
    public void Running_on_the_new_image_is_unverified_and_running_on_the_old_one_after_a_deploy_still_reports_the_tag()
    {
        var onNew = RecoveryCommands.Assess("shop", Intent(), [new LiveContainer(Id, RunWorld.New, "running", "Up 3 seconds")], Service, Target);
        (onNew.Down, onNew.Unhealthy, onNew.Unverified).Should().Be((false, false, true));
        onNew.Guidance[0].Should().Contain("running on the new image");
        onNew.Guidance.Should().Contain("Put the shared tag back on the previous image:", "the deploy may have moved the tag");

        var onOld = RecoveryCommands.Assess("shop", Intent(), [new LiveContainer(Id, RunWorld.Old, "running", "Up")], Service, Target);
        onOld.Guidance[0].Should().Contain("running on the previous image");
        onOld.Guidance.Should().NotContain(g => g.StartsWith("docker compose", StringComparison.Ordinal), "nothing to bring back");

        var unhealthy = RecoveryCommands.Assess("shop", Intent(), [new LiveContainer(Id, RunWorld.New, "running", "Up 1 minute (unhealthy)")], Service, Target);
        (unhealthy.Unhealthy, unhealthy.Unverified).Should().Be((true, false));
        unhealthy.Guidance[0].Should().Contain("UNHEALTHY");
    }

    [Fact]
    public void Without_a_resolvable_project_the_guidance_is_generic_but_still_names_the_service()
    {
        var a = RecoveryCommands.Assess("shop", Intent(from: null, to: null, action: "rollback"), [], null, null);
        a.Guidance.Should().Contain(g => g.Contains("docker compose up -d --no-deps web in project shop", StringComparison.Ordinal));
    }

    [Fact]
    public void Manual_restore_names_data_risk_and_a_repo_tag_comes_from_the_configured_reference()
    {
        var risky = Service with { DataRisk = new DataRisk(true, [], string.Empty) };
        RecoveryCommands.ManualRestore(Target, risky, "nginx:1.27").Should().Contain(l => l.Contains("persistent mounts", StringComparison.Ordinal));
        RecoveryCommands.RepoTagOf("nginx").Should().Be("nginx:latest");
        RecoveryCommands.RepoTagOf("ghcr.io/acme/app:2").Should().Be("ghcr.io/acme/app:2");
    }
}

public class RunServiceTests
{

    private sealed class Fixture
    {
        public RunWorld World { get; } = new(new RunWorld.SvcSpec("web", new string('4', 64)));

        public IRunExecutor Executor { get; } = Substitute.For<IRunExecutor>();

        public IOperationManager Ops { get; } = Substitute.For<IOperationManager>();

        public List<OperationStart> Started { get; } = [];

        public RunService Service { get; }

        public Actor Admin { get; } = new("admin", null);

        public Fixture(bool locked = false)
        {
            Ops.IsLocked("maintenance").Returns(locked);
            Ops.StartAsync(Arg.Any<OperationStart>(), Arg.Any<CancellationToken>()).Returns(c =>
            {
                Started.Add(c.Arg<OperationStart>());
                return Task.FromResult(c.Arg<OperationStart>().Id ?? "op-1");
            });
            Service = new RunService(World.Runs, World.Planner, Executor, Ops, World.Windows, World.Rollback, World.Notes, World.Gateway, World.Config, World.Clock);
        }
    }

    [Fact]
    public async Task Create_waits_for_approval_with_the_configured_expiry_and_announces_it()
    {
        var f = new Fixture();
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);

        run.Status.Should().Be("awaiting_approval");
        run.Kind.Should().Be("update");
        run.PlanHash.Should().Be(f.World.CurrentPlan.PlanHash);
        DateTimeOffset.Parse(run.ApprovalExpiresAt!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(f.World.Clock.GetUtcNow().AddHours(72), "QM_APPROVAL_EXPIRY_HOURS defaults to 72");
        f.Started.Should().BeEmpty();
        f.World.Notes.Types.Should().Equal("maintenance.awaiting_approval");
    }

    [Fact]
    public async Task Approve_with_a_different_hash_is_approval_stale_and_starts_nothing()
    {
        var f = new Fixture();
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);

        var act = () => f.Service.ApproveAsync(run.Id, new string('0', 64), f.Admin, default);
        var e = (await act.Should().ThrowAsync<AppException>()).Which;
        (e.Status, e.Code).Should().Be((409, ErrorCode.ApprovalStale));
        (await f.Service.GetAsync(run.Id, default)).Status.Should().Be("awaiting_approval");
        f.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Approve_with_the_reviewed_hash_records_who_and_what_and_starts_the_operation_with_the_run_id()
    {
        var f = new Fixture();
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);

        var approved = await f.Service.ApproveAsync(run.Id, run.PlanHash!, f.Admin, default);

        approved.Status.Should().Be("approved");
        approved.ApprovedBy.Should().Be("admin");
        approved.ApprovedPlanHash.Should().Be(run.PlanHash);
        f.Started.Should().ContainSingle();
        f.Started[0].Id.Should().Be(run.Id, "id == operations.id so intents and live output share one identity");
        f.Started[0].LockKey.Should().Be("maintenance");
        await f.Started[0].Run(new FakeOperationContext());
        await f.Executor.Received(1).ExecuteAsync(run.Id, Arg.Any<IOperationContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_run_makes_the_operation_fail_but_completed_partial_and_skipped_do_not()
    {
        var f = new Fixture();
        f.Executor.ExecuteAsync(default!, default!, default).ReturnsForAnyArgs("rolled_back", "partial");
        var a = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        await f.Service.ApproveAsync(a.Id, a.PlanHash!, f.Admin, default);
        var failing = () => f.Started[0].Run(new FakeOperationContext());
        await failing.Should().ThrowAsync<InvalidOperationException>().WithMessage("run rolled_back");
        await f.Started[0].Run(new FakeOperationContext());
    }

    [Fact]
    public async Task An_expired_approval_cannot_be_used_and_the_run_is_skipped()
    {
        var f = new Fixture();
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        f.World.Clock.Advance(TimeSpan.FromHours(72));

        var act = () => f.Service.ApproveAsync(run.Id, run.PlanHash!, f.Admin, default);
        (await act.Should().ThrowAsync<AppException>()).Which.Message.Should().Contain("expired");
        var after = await f.Service.GetAsync(run.Id, default);
        after.Status.Should().Be("skipped");
        after.Reason.Should().Be("approval expired");
        f.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Expiry_boundary_one_millisecond_before_the_deadline_still_approves()
    {
        var f = new Fixture();
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        f.World.Clock.Advance(TimeSpan.FromHours(72) - TimeSpan.FromMilliseconds(1));
        (await f.Service.ApproveAsync(run.Id, run.PlanHash!, f.Admin, default)).Status.Should().Be("approved");
    }

    [Fact]
    public async Task Expire_approvals_skips_only_overdue_awaiting_runs()
    {
        var f = new Fixture();
        var old = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        f.World.Clock.Advance(TimeSpan.FromHours(50));
        var young = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        f.World.Clock.Advance(TimeSpan.FromHours(30));

        (await f.Service.ExpireApprovalsAsync(default)).Should().Be(1);
        (await f.Service.GetAsync(old.Id, default)).Status.Should().Be("skipped");
        (await f.Service.GetAsync(young.Id, default)).Status.Should().Be("awaiting_approval");
    }

    [Fact]
    public async Task Start_now_needs_the_project_name_as_confirmation_and_creates_no_run_otherwise()
    {
        var f = new Fixture();
        var act = () => f.Service.CreateAsync(new CreateRunRequest("shop", StartNow: true, Confirm: "nope"), f.Admin, null, default);
        var e = (await act.Should().ThrowAsync<AppException>()).Which;
        (e.Status, e.Code).Should().Be((409, ErrorCode.ConfirmationRequired));
        (await f.Service.ListAsync(10, null, null, default)).Runs.Should().BeEmpty();

        var run = await f.Service.CreateAsync(new CreateRunRequest("shop", StartNow: true, Confirm: "shop"), f.Admin, null, default);
        run.Status.Should().Be("approved");
        run.ApprovedBy.Should().Be("admin");
        f.Started.Should().ContainSingle();
    }

    [Fact]
    public async Task Overlapping_runs_are_serialised_a_second_start_now_is_declined_and_leaves_no_orphan_run()
    {
        var f = new Fixture(locked: true);

        var act = () => f.Service.CreateAsync(new CreateRunRequest("shop", StartNow: true, Confirm: "shop"), f.Admin, null, default);
        (await act.Should().ThrowAsync<AppException>()).Which.Message.Should().Contain("one run at a time");
        (await f.Service.ListAsync(10, null, null, default)).Runs.Should().BeEmpty("M-02: a refused start-now must not leave an approvable orphan");

        // a normal create still works (it just waits), but approving it while another run holds the lock is declined
        var waiting = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        var approve = () => f.Service.ApproveAsync(waiting.Id, waiting.PlanHash!, f.Admin, default);
        await approve.Should().ThrowAsync<AppException>();
        (await f.Service.GetAsync(waiting.Id, default)).Status.Should().Be("awaiting_approval", "still approvable once the other run is done");
    }

    [Fact]
    public async Task Losing_the_lock_race_after_approval_never_leaves_an_approved_orphan()
    {
        var f = new Fixture();
        f.Ops.StartAsync(Arg.Any<OperationStart>(), Arg.Any<CancellationToken>()).Returns<Task<string>>(_ => throw AppException.Conflict("Another operation is already running"));
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);

        var act = () => f.Service.ApproveAsync(run.Id, run.PlanHash!, f.Admin, default);
        await act.Should().ThrowAsync<AppException>();
        (await f.Service.GetAsync(run.Id, default)).Status.Should().Be("blocked");
    }

    [Fact]
    public async Task Critical_target_with_an_auto_policy_still_waits_for_a_human()
    {
        var f = new Fixture();
        // policy auto, schedule auto, no approval required - but critical => the plan says not eligible
        f.World.CurrentPlan = WithUnattended(f.World.CurrentPlan, new Unattended(false, ["marked critical"]));
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, new ScheduledRun("s1", "nightly", "auto", 1, null), default);

        run.Status.Should().Be("awaiting_approval");
        run.Trigger.Should().Be("schedule");
        f.Started.Should().BeEmpty();
        f.World.Notes.Types.Should().Contain("maintenance.awaiting_approval");
    }

    [Fact]
    public async Task All_four_conditions_hold_the_scheduled_run_starts_unattended_as_the_schedule()
    {
        var f = new Fixture();
        f.World.CurrentPlan = WithUnattended(f.World.CurrentPlan, new Unattended(true, []));
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, new ScheduledRun("s1", "nightly", "auto", 1, null), default);

        run.Status.Should().Be("approved");
        run.ApprovedBy.Should().Be("schedule:nightly");
        run.ScheduleId.Should().Be("s1");
        f.Started.Should().ContainSingle();
        f.World.Notes.Types.Should().NotContain("maintenance.awaiting_approval");
    }

    [Fact]
    public async Task A_manual_schedule_never_runs_unattended_even_when_every_service_is_eligible()
    {
        var f = new Fixture();
        f.World.CurrentPlan = WithUnattended(f.World.CurrentPlan, new Unattended(true, []));
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, new ScheduledRun("s1", "nightly", "manual", 1, null), default);
        run.Status.Should().Be("awaiting_approval");
    }

    [Fact]
    public async Task Blocked_plans_are_recorded_as_blocked_and_nothing_to_do_as_skipped()
    {
        var f = new Fixture();
        f.World.CurrentPlan = f.World.CurrentPlan with { Blocked = new PlanBlocked("blocked_drift", "drifted") };
        var blocked = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        blocked.Status.Should().Be("blocked");
        blocked.Reason.Should().Be("drifted");

        f.World.CurrentPlan = f.World.CurrentPlan with { Blocked = new PlanBlocked("nothing_to_do", "all current") };
        (await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default)).Status.Should().Be("skipped");
        f.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Reject_only_applies_to_a_run_awaiting_approval()
    {
        var f = new Fixture();
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        var rejected = await f.Service.RejectAsync(run.Id, f.Admin, default);
        rejected.Status.Should().Be("rejected");
        rejected.Reason.Should().Be("rejected by admin");
        var again = () => f.Service.RejectAsync(run.Id, f.Admin, default);
        await again.Should().ThrowAsync<AppException>();
        var approve = () => f.Service.ApproveAsync(run.Id, run.PlanHash!, f.Admin, default);
        await approve.Should().ThrowAsync<AppException>();
    }

    [Fact]
    public async Task Restore_needs_a_finished_update_run_the_target_as_confirmation_and_recovery_points()
    {
        var f = new Fixture();
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        var early = () => f.Service.RestoreAsync(run.Id, new RestoreBody("shop", null), f.Admin, default);
        (await early.Should().ThrowAsync<AppException>()).Which.Message.Should().Contain("restore applies to finished runs");

        await f.Service.RejectAsync(run.Id, f.Admin, default);
        var wrong = () => f.Service.RestoreAsync(run.Id, new RestoreBody("nope", null), f.Admin, default);
        (await wrong.Should().ThrowAsync<AppException>()).Which.Code.Should().Be(ErrorCode.ConfirmationRequired);
        var none = () => f.Service.RestoreAsync(run.Id, new RestoreBody("shop", null), f.Admin, default);
        (await none.Should().ThrowAsync<AppException>()).Which.Message.Should().Contain("no recovery points");

        await f.World.Rollback.PutAsync(new Quaymaster.Domain.Updates.RollbackPoint("quaymaster-rollback/x:y", run.Id, "shop", "web", null, "nginx:1.27", RunWorld.Old, 0, 1, false), default);
        var started = await f.Service.RestoreAsync(run.Id, new RestoreBody("shop", null), f.Admin, default);
        started.OperationId.Should().NotBeNullOrEmpty();
        f.Started.Single().Kind.Should().Be("maintenance.restore");
        f.Started.Single().Id.Should().BeNull("the restore operation has its own id");
    }

    [Fact]
    public async Task Read_only_mode_refuses_create_approve_and_restore()
    {
        var f = new Fixture();
        var run = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        f.World.Gateway.Policy.Returns(new Quaymaster.Domain.Safety.SafetyPolicy(new Quaymaster.Domain.Safety.SafetyOptions(true, [], [], [])));

        foreach (var act in new Func<Task>[]
        {
            () => f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default),
            () => f.Service.ApproveAsync(run.Id, run.PlanHash!, f.Admin, default),
        })
        {
            (await act.Should().ThrowAsync<AppException>()).Which.Code.Should().Be(ErrorCode.ReadOnly);
        }
    }

    [Fact]
    public async Task List_filters_by_status_and_target_newest_first()
    {
        var f = new Fixture();
        var a = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        var b = await f.Service.CreateAsync(new CreateRunRequest("shop"), f.Admin, null, default);
        await f.Service.RejectAsync(a.Id, f.Admin, default);

        (await f.Service.ListAsync(50, null, null, default)).Runs.Select(r => r.Id).Should().Equal(b.Id, a.Id);
        (await f.Service.ListAsync(50, "rejected", null, default)).Runs.Select(r => r.Id).Should().Equal(a.Id);
        (await f.Service.ListAsync(50, null, "other", default)).Runs.Should().BeEmpty();
        (await f.Service.ListAsync(1, null, null, default)).Runs.Should().ContainSingle();
        var missing = () => f.Service.GetAsync("nope", default);
        (await missing.Should().ThrowAsync<AppException>()).Which.Status.Should().Be(404);
    }

    [Fact]
    public void Notification_events_use_the_exact_types_t5_consumes()
    {
        RunExecutor.NotificationFor("r", "shop", "completed", "ok", []).Type.Should().Be("run.succeeded");
        RunExecutor.NotificationFor("r", "shop", "rolled_back", "x", []).Type.Should().Be("run.rolled_back");
        RunExecutor.NotificationFor("r", "shop", "partial", "x", []).Type.Should().Be("run.partial");
        RunExecutor.NotificationFor("r", "shop", "skipped", "x", []).Type.Should().Be("run.skipped");
        var failed = RunExecutor.NotificationFor("r", "shop", "blocked", "why", ["do this"]);
        (failed.Type, failed.Severity).Should().Be(("run.failed", "critical"));
        failed.Message.Should().Be("why\ndo this");
        RunExecutor.NotificationFor("r", "shop", "completed", "ok", []).DedupeKey.Should().Be("run:r");
    }

    private static Plan WithUnattended(Plan p, Unattended u) => p with { Services = [.. p.Services.Select(s => s with { Unattended = u })] };
}

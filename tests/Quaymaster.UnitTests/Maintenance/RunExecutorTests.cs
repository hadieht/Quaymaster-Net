using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Maintenance;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Updates;

namespace Quaymaster.UnitTests.Maintenance;

public class RunExecutorTests
{
    private static readonly string IdDb = new('1', 64);
    private static readonly string IdWeb = new('2', 64);

    private static RunWorld TwoServices(bool webAutoRollback = true, bool webWritable = false) =>
        new(new RunWorld.SvcSpec("db", IdDb, "postgres:17"), new RunWorld.SvcSpec("web", IdWeb, "nginx:1.27", webAutoRollback, webWritable));

    [Fact]
    public async Task Happy_path_runs_the_ten_steps_in_order_one_service_at_a_time_dependencies_first()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();

        (await w.RunAsync(id)).Should().Be("completed");

        var d = await w.DetailAsync(id);
        d.Info.Status.Should().Be("completed");
        d.Info.Result!.Services.Select(s => (s.Service, s.Outcome)).Should().Equal(("db", "updated"), ("web", "updated"));
        d.Steps.Select(s => (s.Step, s.Service)).Should().Equal(
            ("preflight", null), ("validate", null), ("check", null), ("recovery_point", null), ("pull", null), ("impact", null),
            ("deploy", "db"), ("health", "db"), ("deploy", "web"), ("health", "web"), ("record", null));
        d.Steps.Select(s => s.Seq).Should().BeInAscendingOrder();
        d.Steps.Should().OnlyContain(s => s.Status == "succeeded" && s.FinishedAt != null);
        w.Env.Ups.Should().Equal("db", "web");
        w.Env.Services.Values.Should().OnlyContain(s => s.Image == RunWorld.New);
        w.Runs.IntentsOpen.Should().Be(0, "every intent is completed once the outcome is final");
        w.Notes.Types.Should().Equal("run.succeeded");
        d.Info.StartedAt.Should().NotBeNull();
        d.Info.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_users_tag_does_not_move_before_deploy_and_the_pull_is_by_digest_into_a_staged_tag()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        await w.RunAsync(id);

        var log = w.Images.Log;
        var pull = log.FindIndex(l => l.StartsWith("pull:", StringComparison.Ordinal));
        var firstUserTag = log.FindIndex(l => l.StartsWith("tag:postgres:17=", StringComparison.Ordinal));
        log[pull].Should().Be($"pull:{RunWorld.Digest}");
        pull.Should().BeLessThan(firstUserTag, "the staged pull comes first; only the deploy step moves the user's tag");
        log.Where(l => l.StartsWith("tag:quaymaster-rollback/", StringComparison.Ordinal)).Should().HaveCount(2);
        log.Where(l => l.StartsWith("tag:quaymaster-rollback/", StringComparison.Ordinal)).Max(l => log.IndexOf(l)).Should().BeLessThan(pull, "recovery points are tagged before the pull");
        log.Should().Contain(l => l.StartsWith("rmi:quaymaster-staged/", StringComparison.Ordinal), "the staged alias goes once the user's tag holds the image");
    }

    [Fact]
    public async Task Recovery_points_are_rollback_tags_with_the_running_image_id_and_the_retention_expiry()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        await w.RunAsync(id);

        var points = w.Rollback.Points.Values.OrderBy(p => p.Service).ToList();
        points.Select(p => p.Service).Should().Equal("db", "web");
        points.Should().OnlyContain(p => p.Tag.StartsWith(ImageReferences.RollbackPrefix, StringComparison.Ordinal) && p.ImageId == RunWorld.Old && p.RunId == id && !p.Released);
        points[0].Tag.Should().Be(ImageReferences.RollbackTag("shop", "db", id));
        var created = w.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        points.Should().OnlyContain(p => p.ExpiresAt - p.CreatedAt == (long)w.Config.RollbackRetention.TotalMilliseconds);
        points.Should().OnlyContain(p => p.ExpiresAt > created, "retention is days, not seconds");
    }

    [Fact]
    public async Task Pull_failure_aborts_with_zero_impact_nothing_restarted_or_retagged()
    {
        var w = TwoServices();
        w.Images.PullFailure = "registry unreachable";
        var id = await w.ApprovedRunAsync();

        (await w.RunAsync(id)).Should().Be("failed");

        w.Env.Ups.Should().BeEmpty();
        w.Images.Log.Should().NotContain(l => l.StartsWith("tag:postgres", StringComparison.Ordinal) || l.StartsWith("tag:nginx", StringComparison.Ordinal));
        w.Env.Services.Values.Should().OnlyContain(s => s.Image == RunWorld.Old);
        var d = await w.DetailAsync(id);
        d.Info.Reason.Should().Contain("Pull failed").And.Contain("Nothing was restarted");
        d.Info.Guidance.Should().Contain(g => g.Contains("Nothing was changed", StringComparison.Ordinal));
        d.Steps.Select(s => s.Step).Should().NotContain("deploy");
        d.Steps.Single(s => s.Step == "pull").Status.Should().Be("failed");
        w.Notes.Types.Should().Equal("run.failed");
        w.Quarantine.ReceivedCalls().Should().BeEmpty("a pull failure says nothing about the image");
    }

    [Fact]
    public async Task Health_failure_rolls_back_retags_the_recovery_point_and_redeploys_and_rechecks()
    {
        var w = TwoServices();
        w.Env.BadImages.Add(RunWorld.New);
        var id = await w.ApprovedRunAsync();

        (await w.RunAsync(id)).Should().Be("rolled_back");

        var d = await w.DetailAsync(id);
        d.Info.Result!.Services.Select(s => (s.Service, s.Outcome)).Should().Equal(("db", "rolled_back"), ("web", "skipped"));
        d.Steps.Select(s => (s.Step, s.Service)).Should().ContainInOrder(("deploy", "db"), ("health", "db"), ("rollback", "db"));
        d.Steps.Single(s => s.Step == "rollback").Status.Should().Be("succeeded");
        w.Env.Services["db"].Image.Should().Be(RunWorld.Old, "the old image runs again");
        w.Images.Tags["postgres:17"].Should().Be(RunWorld.Old, "rollback = re-tag the recovery point");
        w.Env.Services["web"].Image.Should().Be(RunWorld.Old, "the rest of the rollout was skipped");
        w.Env.Ups.Should().Equal("db", "db");
        w.Rollback.Points.Should().ContainKey(ImageReferences.RollbackTag("shop", "db", id));
        await w.Quarantine.Received(1).AddAsync("docker.io/library/postgres:17", RunWorld.Digest, id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        w.Notes.Types.Should().Contain("run.rolled_back").And.Contain("run.failed", "the quarantine warning");
        w.Runs.IntentsOpen.Should().Be(0);
    }

    [Fact]
    public async Task Writable_persistent_mount_turns_auto_rollback_off_the_rollout_stops_the_service_is_left_and_alerted()
    {
        var w = TwoServices(webWritable: true);
        w.Env.BadImages.Add(RunWorld.New);
        var id = await w.ApprovedRunAsync();

        (await w.RunAsync(id)).Should().Be("failed");

        var d = await w.DetailAsync(id);
        d.Info.Result!.Services.Select(s => (s.Service, s.Outcome)).Should().Equal(("db", "updated"), ("web", "failed_left_as_is"));
        d.Info.Result.Services[1].Detail.Should().Contain("Automatic rollback is off").And.Contain("writable persistent mount");
        w.Env.Ups.Should().Equal("db", "web");
        w.Env.Services["web"].Image.Should().Be(RunWorld.New, "no automatic redeploy of the old image over possibly migrated data");
        w.Images.Tags["nginx:1.27"].Should().Be(RunWorld.Old, "but the shared tag no longer points at the failing image");
        d.Steps.Select(s => s.Step).Should().NotContain("rollback");
        d.Info.Guidance.Should().Contain(g => g.StartsWith("docker tag " + RunWorld.Old, StringComparison.Ordinal))
            .And.Contain(g => g.StartsWith("docker compose --project-name shop ", StringComparison.Ordinal))
            .And.Contain(g => g.Contains("persistent mounts", StringComparison.Ordinal));
        w.Notes.Events.Should().Contain(e => e.Type == "service.left_down" && e.Severity == "critical");
    }

    [Fact]
    public async Task Failed_rollback_is_a_plain_failure_with_manual_recovery_and_a_critical_alert()
    {
        var w = TwoServices();
        w.Env.BadImages.Add(RunWorld.New);
        w.Env.BadImages.Add(RunWorld.Old);
        var id = await w.ApprovedRunAsync();

        (await w.RunAsync(id)).Should().Be("failed");

        var d = await w.DetailAsync(id);
        d.Info.Result!.Services[0].Outcome.Should().Be("failed");
        d.Info.Result.Services[0].Detail.Should().Contain("automatic rollback also failed");
        d.Info.Guidance.Should().Contain(g => g.StartsWith("docker tag", StringComparison.Ordinal));
        w.Notes.Events.Should().Contain(e => e.Type == "service.left_down");
    }

    [Fact]
    public async Task Compose_up_failure_restores_the_tag_and_does_not_quarantine_the_image()
    {
        var w = TwoServices();
        w.Env.UpFails.Add("db");
        var id = await w.ApprovedRunAsync();

        await w.RunAsync(id);

        w.Quarantine.ReceivedCalls().Should().BeEmpty("a failed `compose up` is as likely a configuration problem as an image problem (RT-22)");
        w.Images.Tags["postgres:17"].Should().Be(RunWorld.Old);
    }

    [Fact]
    public async Task Approval_that_does_not_match_the_stored_plan_never_executes_anything()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync(approvedHash: new string('0', 64));

        (await w.RunAsync(id)).Should().Be("approval_stale");

        w.Images.Log.Should().BeEmpty();
        w.Env.Ups.Should().BeEmpty();
    }

    [Fact]
    public async Task Plan_that_changes_between_approval_and_execution_is_approval_stale_and_asks_again()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        var moved = RunWorld.BuildPlan(new RunWorld.SvcSpec("db", IdDb, "postgres:17"), new RunWorld.SvcSpec("web", IdWeb, "nginx:1.27"));
        moved = moved with { Services = [.. moved.Services.Select(s => s with { ToDigest = "sha256:" + new string('e', 64) })] };
        w.CurrentPlan = moved with { PlanHash = PlanHasher.Compute(moved) };

        (await w.RunAsync(id)).Should().Be("approval_stale");

        var d = await w.DetailAsync(id);
        d.Info.Reason.Should().Contain("The plan changed since it was approved").And.Contain("target digest moved").And.Contain("A new approval request was created");
        w.Images.Log.Should().BeEmpty("nothing was pulled, tagged or recreated");
        w.Env.Ups.Should().BeEmpty();
        var again = (await w.Runs.ListAsync(10, "awaiting_approval", "shop", default)).Single();
        again.PlanHash.Should().Be(w.CurrentPlan.PlanHash);
        again.Reason.Should().Contain(id);
        w.Notes.Types.Should().Contain("maintenance.awaiting_approval");
    }

    [Fact]
    public async Task A_container_replaced_by_another_tool_mid_run_aborts_without_rolling_back_and_raises_run_conflict()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        w.Hooks.OnStep = (_, step, service, phase) =>
        {
            if (step == "health" && service == "db" && phase == "end")
            {
                w.Env.Services["web"].Ids = [new string('7', 64)]; // somebody recreated web while db was being checked
            }

            return Task.CompletedTask;
        };

        (await w.RunAsync(id)).Should().Be("failed");

        var d = await w.DetailAsync(id);
        d.Info.Reason.Should().Contain("Another tool changed web").And.Contain("without rolling back");
        w.Env.Ups.Should().Equal("db");
        w.Images.Tags["postgres:17"].Should().Be(RunWorld.New, "the finished service is not rolled back");
        w.Notes.Types.Should().Contain("run.conflict");
    }

    [Fact]
    public async Task Compose_configuration_drift_during_the_run_stops_before_the_recreate()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        w.Env.Hashes["web"] = "someone-edited-the-file";

        (await w.RunAsync(id)).Should().Be("partial");

        var d = await w.DetailAsync(id);
        d.Info.Reason.Should().Contain("web").And.Contain("changed after the plan was approved");
        w.Env.Ups.Should().Equal("db");
        d.Info.Result!.Services.Select(s => s.Outcome).Should().Equal("updated", "skipped");
    }

    [Fact]
    public async Task A_closed_window_skips_the_run_and_a_window_that_closes_mid_rollout_skips_the_rest()
    {
        var w = TwoServices();
        w.Windows.GetAsync("win", default).ReturnsForAnyArgs(Task.FromResult<WindowRecord?>(null));
        var closed = await w.ApprovedRunAsync(windowId: "win");
        (await w.RunAsync(closed)).Should().Be("skipped");
        w.Env.Ups.Should().BeEmpty();

        var w2 = TwoServices();
        w2.Windows.GetAsync("win", default).ReturnsForAnyArgs(Task.FromResult<WindowRecord?>(new WindowRecord("win", "now", [0, 1, 2, 3, 4, 5, 6], "11:59", "12:02", "UTC", true, 0)));
        var id = await w2.ApprovedRunAsync(windowId: "win");
        w2.Hooks.OnStep = (_, step, service, phase) =>
        {
            if (step == "health" && service == "db" && phase == "end")
            {
                w2.Clock.Advance(TimeSpan.FromSeconds(90)); // 12:01:30 - web's 60 s health budget no longer fits before 12:02:00
            }

            return Task.CompletedTask;
        };

        (await w2.RunAsync(id)).Should().Be("partial");

        var d = await w2.DetailAsync(id);
        d.Info.WindowEnd.Should().Be("2026-11-14T12:02:00.000Z");
        d.Info.Reason.Should().Contain("window closed");
        d.Info.Result!.Services.Select(s => (s.Service, s.Outcome)).Should().Equal(("db", "updated"), ("web", "skipped"));
        w2.Env.Ups.Should().Equal("db");
        d.Steps.Last(s => s.Step == "deploy").Status.Should().Be("skipped");
    }

    [Fact]
    public async Task Shutdown_does_not_start_new_services_and_nothing_is_interrupted()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        var ctx = new FakeOperationContext { ShuttingDown = true };

        (await w.Executor.ExecuteAsync(id, ctx, default)).Should().Be("skipped");

        w.Env.Ups.Should().BeEmpty();
        (await w.DetailAsync(id)).Info.Result!.Services.Should().OnlyContain(s => s.Outcome == "skipped");
    }

    [Fact]
    public async Task A_service_that_was_stopped_stays_stopped_and_only_the_image_is_asserted()
    {
        var w = new RunWorld(new RunWorld.SvcSpec("web", IdWeb, WasRunning: false));
        w.Env.Services["web"].State = "exited";
        var id = await w.ApprovedRunAsync();

        (await w.RunAsync(id)).Should().Be("completed");
        var d = await w.DetailAsync(id);
        d.Steps.Single(s => s.Step == "health").Detail!["skipped"]!.GetValue<string>().Should().Contain("stays stopped");
    }

    [Fact]
    public async Task Approved_run_that_is_not_approved_any_more_is_refused()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        await w.RunAsync(id);
        var act = () => w.RunAsync(id);
        (await act.Should().ThrowAsync<AppException>()).Which.Status.Should().Be(409);
    }

    [Fact]
    public async Task Crash_mid_run_leaves_a_running_run_and_an_open_intent_and_recovery_interrupts_it_with_exact_commands()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        w.Hooks.OnStep = (_, step, service, phase) =>
            step == "health" && service == "db" && phase == "start" ? throw new ProcessCrashedException() : Task.CompletedTask;

        var act = () => w.RunAsync(id);
        await act.Should().ThrowAsync<ProcessCrashedException>();

        (await w.DetailAsync(id)).Info.Status.Should().Be("running", "a killed process writes nothing more");
        w.Runs.IntentsOpen.Should().Be(1);
        w.Notes.Events.Should().BeEmpty();

        // the new process starts: the container was recreated on the new image but its health never confirmed
        var recovery = new RunRecoveryService(w.Runs, w.Env, w.Notes, w.Gateway, w.Clock);
        (await recovery.RecoverAsync(default)).Should().Be(1);

        var d = await w.DetailAsync(id);
        d.Info.Status.Should().Be("interrupted");
        d.Info.Reason.Should().Contain("Quaymaster stopped while this run was in progress");
        d.Info.Guidance.Should().NotBeEmpty();
        d.Info.Guidance![0].Should().Contain("db: interrupted during \"deploy\"").And.Contain("the new image");
        d.Steps.Where(s => s.Step == "health").Should().OnlyContain(s => s.Status == "failed", "open steps are failed");
        w.Runs.IntentsOpen.Should().Be(0);
        w.Notes.Types.Should().Contain("run.interrupted");
        (await w.Runs.ListAsync(10, "approved", null, default)).Should().BeEmpty("never resumed");
    }

    [Fact]
    public async Task Crash_that_leaves_the_service_down_alerts_critical_with_retag_and_compose_up_commands()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        w.Hooks.OnStep = (_, step, service, phase) =>
        {
            if (step == "deploy" && service == "db" && phase == "end")
            {
                w.Env.Services["db"].State = "exited"; // `compose up` removed the old container and the new one never came up
                throw new ProcessCrashedException();
            }

            return Task.CompletedTask;
        };
        await ((Func<Task>)(() => w.RunAsync(id))).Should().ThrowAsync<ProcessCrashedException>();

        await new RunRecoveryService(w.Runs, w.Env, w.Notes, w.Gateway, w.Clock).RecoverAsync(default);

        var g = (await w.DetailAsync(id)).Info.Guidance!;
        g.Should().Contain("Put the shared tag back on the previous image:");
        g.Should().Contain($"docker tag {RunWorld.Old} postgres:17");
        g.Should().Contain(x => x.StartsWith("docker compose --project-name shop --project-directory /srv/shop --file /srv/shop/compose.yaml up -d --no-deps --no-build --pull never -- db", StringComparison.Ordinal));
        w.Notes.Events.Should().Contain(e => e.Type == "service.left_down" && e.Severity == "critical");
        w.Notes.Events.Single(e => e.Type == "run.interrupted" && e.DedupeKey.StartsWith("interrupted:", StringComparison.Ordinal)).Severity.Should().Be("critical");
    }

    [Fact]
    public async Task Restore_puts_the_recovery_points_back_dependents_first_and_rechecks_health()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        await w.RunAsync(id);
        w.Env.Ups.Clear();

        await w.Executor.RestoreAsync(id, null, new Quaymaster.Application.Auth.Actor("admin", null), new FakeOperationContext(), default);

        w.Env.Ups.Should().Equal("web", "db", "reverse deployment order: dependents first");
        w.Env.Services.Values.Should().OnlyContain(s => s.Image == RunWorld.Old);
        w.Images.Tags["nginx:1.27"].Should().Be(RunWorld.Old);
        (await w.DetailAsync(id)).Steps.Where(s => s.Step == "restore").Select(s => s.Seq).Should().OnlyContain(s => s >= 20_000);
        w.Notes.Types.Should().Contain("run.rolled_back");

        w.Env.Ups.Clear();
        await w.Executor.RestoreAsync(id, ["db"], new Quaymaster.Application.Auth.Actor("admin", null), new FakeOperationContext(), default);
        w.Env.Ups.Should().Equal("db");
    }

    [Fact]
    public async Task Restore_refuses_to_apply_an_unreviewed_configuration_change()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        await w.RunAsync(id);
        w.Env.Drift = true;

        var act = () => w.Executor.RestoreAsync(id, null, new Quaymaster.Application.Auth.Actor("admin", null), new FakeOperationContext(), default);
        (await act.Should().ThrowAsync<AppException>()).Which.Message.Should().Contain("configuration changed");
    }

    [Fact]
    public async Task A_run_owns_its_project_in_the_maintenance_gate_only_while_it_executes()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        var seenOwned = false;
        var blockedForOthers = false;
        w.Hooks.OnStep = (_, step, _, phase) =>
        {
            if (step == "deploy" && phase == "start")
            {
                seenOwned = w.Gate.IsOwned("shop");
                blockedForOthers = Task.Run(() =>
                {
                    try
                    {
                        w.Gate.Assert("shop");
                        return false;
                    }
                    catch (AppException e)
                    {
                        return e.Status == 409;
                    }
                }).GetAwaiter().GetResult();
                w.Gate.Assert("shop"); // the run's own flow passes
            }

            return Task.CompletedTask;
        };

        await w.RunAsync(id);

        seenOwned.Should().BeTrue();
        blockedForOthers.Should().BeTrue("manual actions on the project are refused while the run owns it");
        w.Gate.IsOwned("shop").Should().BeFalse();
    }

    [Fact]
    public async Task Persisted_step_details_carry_the_digests_and_the_staged_tags()
    {
        var w = TwoServices();
        var id = await w.ApprovedRunAsync();
        await w.RunAsync(id);
        var d = await w.DetailAsync(id);

        var check = d.Steps.Single(s => s.Step == "check").Detail!["digests"]!.AsArray();
        check.Should().HaveCount(2);
        check[0]!["to"]!.GetValue<string>().Should().Be(RunWorld.Digest);
        var pulled = d.Steps.Single(s => s.Step == "pull").Detail!["pulled"]!.AsArray();
        pulled[0]!["stagedTag"]!.GetValue<string>().Should().StartWith("quaymaster-staged/");
        pulled[0]!["digest"]!.GetValue<string>().Should().Be(RunWorld.Digest);
        d.Steps.Single(s => s.Step == "impact").Detail.Should().NotBeNull();
        _ = (JsonObject)d.Steps.Single(s => s.Step == "record").Detail!;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

/// <summary>Image engine double for the run executor: tags, staged pulls, scripted pull failure.</summary>
internal sealed class ScriptedImages : IImageOps
{
    public static readonly string NewId = "sha256:" + new string('7', 64);

    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal) { ["nginx:1.27"] = FakeDocker.NginxId };

    public List<string> Log { get; } = [];

    public string? PullFailure { get; set; }

    public Task<JsonObject?> InspectAsync(string nameOrId, CancellationToken ct) =>
        Task.FromResult<JsonObject?>(Tags.TryGetValue(nameOrId, out var id) ? new JsonObject { ["Id"] = id, ["RepoDigests"] = new JsonArray() }
            : nameOrId.StartsWith("sha256:", StringComparison.Ordinal) ? new JsonObject { ["Id"] = nameOrId, ["RepoDigests"] = new JsonArray() } : null);

    public Task<PullResult> PullByDigestAsync(ImageRef image, string digest, Action<string>? log, CancellationToken ct)
    {
        Log.Add("pull:" + digest);
        if (PullFailure is not null)
        {
            throw new Quaymaster.Application.Errors.AppException(502, Quaymaster.Application.Errors.ErrorCode.RegistryError, PullFailure);
        }

        var tag = ImageReferences.StagedTag(image, digest);
        Tags[tag] = NewId;
        return Task.FromResult(new PullResult(NewId, digest, tag));
    }

    public Task TagAsync(string imageId, string repoTag, CancellationToken ct)
    {
        Log.Add($"tag:{repoTag}={imageId[..14]}");
        Tags[repoTag] = imageId;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string nameOrId, CancellationToken ct)
    {
        Tags.Remove(nameOrId);
        return Task.CompletedTask;
    }
}

/// <summary>Reads the world from <see cref="FakeDocker"/>; `compose up` moves web to whatever its user tag points at.</summary>
internal sealed class ScriptedEnv(RunEnvironment real, FakeDocker docker, ScriptedImages images) : IRunEnvironment
{
    public HashSet<string> Unhealthy { get; } = new(StringComparer.Ordinal);

    public List<string> Ups { get; } = [];

    /// <summary>
    /// Models the daemon's recreate window: <c>up</c> creates a new container (new id), the replaced one lingers in the
    /// container list a little longer but is no longer inspectable.
    /// </summary>
    public bool StaleRecreateWindow { get; set; }

    /// <summary>Containers that still list but no longer inspect (the daemon is removing them).</summary>
    public HashSet<string> Vanishing { get; } = new(StringComparer.Ordinal);

    private JsonObject Web => (JsonObject)docker.Containers[0]!;

    public Task<ProjectTarget> TargetAsync(string project, CancellationToken ct) => real.TargetAsync(project, ct);

    public Task<IReadOnlyList<EngineContainer>> ContainersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EngineContainer>>([.. docker.Containers.OfType<JsonObject>().Select(c =>
        {
            var l = (JsonObject)c["Labels"]!;
            return new EngineContainer(c["Id"]!.GetValue<string>(), "x", l["com.docker.compose.project"]?.GetValue<string>(), l["com.docker.compose.service"]?.GetValue<string>(), c["ImageID"]!.GetValue<string>(), c["State"]!.GetValue<string>(), c["Status"]!.GetValue<string>());
        })]);

    public Task<InspectLite?> InspectAsync(string containerId, CancellationToken ct)
    {
        if (Vanishing.Contains(containerId))
        {
            return Task.FromResult<InspectLite?>(null);
        }

        var c = docker.Containers.OfType<JsonObject>().First(x => x["Id"]!.GetValue<string>() == containerId);
        var image = c["ImageID"]!.GetValue<string>();
        var bad = Unhealthy.Contains(image);
        return Task.FromResult<InspectLite?>(new InspectLite(image, 0, bad ? "exited" : "running", !bad, false, bad ? 1 : 0, "2026-11-14T10:00:00Z", bad ? "unhealthy" : "healthy"));
    }

    public Task<DeployResult> UpAsync(string project, string service, bool forceRecreate, bool noStart, Action<string> log, CancellationToken ct)
    {
        Ups.Add(service);
        docker.ComposeRuns.Add(["compose", "up", service]);
        if (service == "web")
        {
            if (StaleRecreateWindow)
            {
                Web["State"] = "exited";
                Web["Status"] = "Exited (0) 5 seconds ago";
                Vanishing.Add(Web["Id"]!.GetValue<string>());
                var fresh = FakeDocker.Summary("d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4", "web", "nginx:1.27", "shop", "web", "running", "Up 10 seconds");
                fresh["ImageID"] = images.Tags["nginx:1.27"];
                docker.Containers.Add(fresh);
            }
            else
            {
                Web["ImageID"] = images.Tags["nginx:1.27"];
            }
        }

        return Task.FromResult(new DeployResult(true, 0, string.Empty, ["compose", "up", service]));
    }

    public Task<string?> ConfigHashAsync(string project, string service, CancellationToken ct) => real.ConfigHashAsync(project, service, ct);

    public Task<bool> HasDriftAsync(string project, string service, CancellationToken ct) => real.HasDriftAsync(project, service, ct);

    public Task<JsonObject> SnapshotAsync(string project, CancellationToken ct) => real.SnapshotAsync(project, ct);

    public Task<IReadOnlyList<Quaymaster.Domain.Compose.GraphEdge>> EdgesAsync(string project, CancellationToken ct) => real.EdgesAsync(project, ct);

    public Task<string?> RestartAsync(string containerId, CancellationToken ct) => Task.FromResult<string?>(null);

    public void Invalidate() => real.Invalidate();
}

/// <summary>
/// T4b failure scenarios over HTTP: the real host, API, planner, stores and executor with the Docker/Compose world and the image
/// engine replaced by scripted fakes (<see cref="ScriptedEnv"/>, <see cref="ScriptedImages"/>).
/// </summary>
public sealed class RunLifecycleApiTests : IDisposable
{
    private DockerHarness? _h;
    private ScriptedImages _images = null!;
    private ScriptedEnv _env = null!;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null)
    {
        var docker = new FakeDocker();
        T3Support.RoomyDisk(docker);
        var registry = new FakeRegistry();
        registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;
        _images = new ScriptedImages();
        var images = _images;
        _h = await DockerHarness.CreateAsync(settings, docker, registry, services =>
        {
            services.AddSingleton<RunEnvironment>();
            services.AddSingleton<IImageOps>(images);
            services.AddSingleton<IRunEnvironment>(sp => _env = new ScriptedEnv(sp.GetRequiredService<RunEnvironment>(), docker, images));
            services.AddSingleton(sp =>
            {
                var clock = (FakeTimeProvider)sp.GetRequiredService<TimeProvider>();
                return new HealthTimings { PollMs = 1000, Sleep = (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; } };
            });
        });
        _ = _h.Factory.Services.GetRequiredService<IRunEnvironment>();
        return _h;
    }

    private static async Task<JsonElement> WaitFinalAsync(DockerHarness h, string id)
    {
        for (var i = 0; i < 500; i++)
        {
            var r = await h.Admin.GetAsync($"/maintenance/runs/{id}");
            var s = r.Json.GetProperty("status").GetString();
            if (s is not ("approved" or "running"))
            {
                return r.Json.Clone();
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("run did not finish");
    }

    private static async Task<(string Id, string Hash)> CreateAsync(DockerHarness h, object? body = null)
    {
        var r = await h.Admin.PostAsync("/maintenance/runs", body ?? new { project = "shop" });
        r.Status.Should().Be(201, r.Body);
        return (r.Json.GetProperty("id").GetString()!, r.Json.GetProperty("planHash").GetString()!);
    }

    private static Task<ApiResponse> ApproveAsync(DockerHarness h, string id, string hash) => h.Admin.PostAsync($"/maintenance/runs/{id}/approve", new { planHash = hash });

    private static JsonElement Step(JsonElement run, string name) => run.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("step").GetString() == name);

    [Fact]
    public async Task Create_approve_execute_complete_over_the_api_with_persisted_steps_and_a_recovery_point()
    {
        var h = await HarnessAsync();
        var (id, hash) = await CreateAsync(h);
        var created = await h.Admin.GetAsync($"/maintenance/runs/{id}");
        created.Json.GetProperty("status").GetString().Should().Be("awaiting_approval");
        created.Json.GetProperty("approvalExpiresAt").GetString().Should().NotBeNull();
        DockerHarness.Keys(created.Json).Should().Contain(["id", "kind", "trigger", "target", "status", "planHash", "approvedBy", "result", "guidance", "plan", "steps"]);

        var stale = await ApproveAsync(h, id, new string('0', 64));
        (stale.Status, stale.ErrorCode).Should().Be((409, "approval_stale"));
        (await ApproveAsync(h, id, "xyz")).Status.Should().Be(400);

        var ok = await ApproveAsync(h, id, hash);
        ok.Status.Should().Be(202, ok.Body);
        ok.Json.GetProperty("approvedBy").GetString().Should().Be("admin");
        var done = await WaitFinalAsync(h, id);

        done.GetProperty("status").GetString().Should().Be("completed", done.ToString());
        done.GetProperty("result").GetProperty("services")[0].GetProperty("outcome").GetString().Should().Be("updated");
        done.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetString()).Should().Equal("preflight", "validate", "check", "recovery_point", "pull", "impact", "deploy", "health", "record");
        _images.Tags["nginx:1.27"].Should().Be(ScriptedImages.NewId);
        var points = await h.Factory.Services.GetRequiredService<IRollbackStore>().ForRunAsync(id, default);
        points.Should().ContainSingle().Which.Tag.Should().StartWith("quaymaster-rollback/");
        points[0].ImageId.Should().Be(FakeDocker.NginxId);

        (await h.Viewer.GetAsync("/maintenance/runs?status=completed&target=shop&limit=5")).Json.GetProperty("runs").GetArrayLength().Should().Be(1);
        (await h.Viewer.GetAsync("/maintenance/runs?limit=0")).Status.Should().Be(400);
        (await h.Viewer.GetAsync("/maintenance/runs/nope")).Status.Should().Be(404);
        (await h.Viewer.PostAsync("/maintenance/runs", new { project = "shop" })).Status.Should().Be(403);
    }

    [Fact]
    public async Task A_recreate_whose_old_container_lingers_in_the_daemon_list_still_completes_the_run()
    {
        // The daemon can still list the container compose just replaced (stopped, pending removal) when the executor
        // asks for the service's containers right after `up`. The health gate must gate on the new container only.
        var h = await HarnessAsync();
        _env.StaleRecreateWindow = true;
        var (id, hash) = await CreateAsync(h);
        (await ApproveAsync(h, id, hash)).Status.Should().Be(202);
        var done = await WaitFinalAsync(h, id);

        done.GetProperty("status").GetString().Should().Be("completed", done.ToString());
        done.GetProperty("result").GetProperty("services")[0].GetProperty("outcome").GetString().Should().Be("updated");
        var web = h.Docker.Containers.OfType<JsonObject>().First(c =>
            ((JsonObject)c["Labels"]!)["com.docker.compose.service"]?.GetValue<string>() == "web" && c["ImageID"]!.GetValue<string>() == ScriptedImages.NewId);
        web["State"]!.GetValue<string>().Should().Be("running");
    }

    [Fact]
    public async Task Start_now_needs_confirmation_and_read_only_mode_refuses()
    {
        var h = await HarnessAsync();
        var r = await h.Admin.PostAsync("/maintenance/runs", new { project = "shop", startNow = true });
        (r.Status, r.ErrorCode).Should().Be((409, "confirmation_required"));
        r.Json.GetProperty("error").GetProperty("details").GetProperty("expected").GetString().Should().Be("shop");
        var started = await h.Admin.PostAsync("/maintenance/runs", new { project = "shop", startNow = true, confirm = "shop" });
        started.Status.Should().Be(202, started.Body);
        (await WaitFinalAsync(h, started.Json.GetProperty("id").GetString()!)).GetProperty("status").GetString().Should().Be("completed");

        using var ro = await DockerHarness.CreateAsync(new Dictionary<string, string> { ["QM_READ_ONLY"] = "true" });
        var refused = await ro.Admin.PostAsync("/maintenance/runs", new { project = "shop" });
        (refused.Status, refused.ErrorCode).Should().Be((409, "read_only"));
    }

    [Fact]
    public async Task Pull_failure_aborts_with_zero_impact()
    {
        var h = await HarnessAsync();
        _images.PullFailure = "registry down";
        var (id, hash) = await CreateAsync(h);
        (await ApproveAsync(h, id, hash)).Status.Should().Be(202);
        var done = await WaitFinalAsync(h, id);

        done.GetProperty("status").GetString().Should().Be("failed");
        done.GetProperty("reason").GetString().Should().Contain("Pull failed").And.Contain("Nothing was restarted");
        _env.Ups.Should().BeEmpty();
        _images.Log.Should().NotContain(l => l.StartsWith("tag:nginx", StringComparison.Ordinal));
        _images.Tags["nginx:1.27"].Should().Be(FakeDocker.NginxId);
        _h!.Docker.Containers[0]!["ImageID"]!.GetValue<string>().Should().Be(FakeDocker.NginxId);
        done.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetString()).Should().NotContain("deploy");
    }

    [Fact]
    public async Task A_restarting_service_blocks_the_run_at_preflight()
    {
        var h = await HarnessAsync();
        h.Docker.Containers[0]!["State"] = "restarting";
        h.Docker.Containers[0]!["Status"] = "Restarting (1) 3 seconds ago";
        var r = await h.Admin.PostAsync("/maintenance/runs", new { project = "shop" });
        r.Status.Should().Be(201, r.Body);
        r.Json.GetProperty("status").GetString().Should().Be("blocked");
        r.Json.GetProperty("plan").GetProperty("blocked").GetProperty("code").GetString().Should().Be("preflight");
        (await h.Admin.PostAsync($"/maintenance/runs/{r.Json.GetProperty("id").GetString()}/approve", new { planHash = r.Json.GetProperty("planHash").GetString() })).Status.Should().Be(409);
        _env.Ups.Should().BeEmpty();
    }

    [Fact]
    public async Task Health_failure_rolls_back_retags_the_recovery_point_and_the_old_image_runs_again()
    {
        var h = await HarnessAsync();
        await h.Factory.Services.GetRequiredService<IPolicyStore>().PutAsync("service", "shop/web", new PolicyInput { AutoRollback = true }, 0, default);
        _env.Unhealthy.Add(ScriptedImages.NewId);
        var (id, hash) = await CreateAsync(h);
        (await ApproveAsync(h, id, hash)).Status.Should().Be(202);
        var done = await WaitFinalAsync(h, id);

        done.GetProperty("status").GetString().Should().Be("rolled_back", done.ToString());
        done.GetProperty("result").GetProperty("services")[0].GetProperty("outcome").GetString().Should().Be("rolled_back");
        Step(done, "rollback").GetProperty("status").GetString().Should().Be("succeeded");
        _images.Tags["nginx:1.27"].Should().Be(FakeDocker.NginxId, "rollback = re-tag the recovery point");
        h.Docker.Containers[0]!["ImageID"]!.GetValue<string>().Should().Be(FakeDocker.NginxId);
        _env.Ups.Should().Equal("web", "web");
        (await h.Factory.Services.GetRequiredService<IQuarantineService>().ListAsync(default)).Should().ContainSingle().Which.Digest.Should().Be(T3Support.NewDigest);
        (await h.Factory.Services.GetRequiredService<IRollbackStore>().ForRunAsync(id, default)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_tag_that_moves_between_approval_and_execution_is_approval_stale_and_a_new_request_is_created()
    {
        var h = await HarnessAsync();
        var (id, hash) = await CreateAsync(h);
        h.Factory.Services.GetRequiredService<RunHooks>().OnStep = (_, step, _, phase) =>
        {
            if (step == "preflight" && phase == "start")
            {
                h.Registry.Digests["docker.io/library/nginx:1.27"] = "sha256:" + new string('9', 64);
            }

            return Task.CompletedTask;
        };
        (await ApproveAsync(h, id, hash)).Status.Should().Be(202);
        var done = await WaitFinalAsync(h, id);

        done.GetProperty("status").GetString().Should().Be("approval_stale");
        done.GetProperty("reason").GetString().Should().Contain("target digest moved").And.Contain("A new approval request was created");
        _env.Ups.Should().BeEmpty();
        _images.Log.Should().BeEmpty();
        var again = (await h.Admin.GetAsync("/maintenance/runs?status=awaiting_approval")).Json.GetProperty("runs");
        again.GetArrayLength().Should().Be(1);
        again[0].GetProperty("planHash").GetString().Should().NotBe(hash);
    }

    [Fact]
    public async Task A_crash_mid_run_is_interrupted_at_startup_with_exact_recovery_commands_and_never_resumed()
    {
        var h = await HarnessAsync();
        var (id, hash) = await CreateAsync(h);
        h.Factory.Services.GetRequiredService<RunHooks>().OnStep = (_, step, service, phase) =>
            step == "health" && service == "web" && phase == "start" ? throw new ProcessCrashedException() : Task.CompletedTask;
        (await ApproveAsync(h, id, hash)).Status.Should().Be(202);
        for (var i = 0; i < 300 && (await h.Admin.GetAsync("/operations/" + id)).Json.GetProperty("status").GetString() == "running"; i++)
        {
            await Task.Delay(20);
        }

        (await h.Admin.GetAsync($"/maintenance/runs/{id}")).Json.GetProperty("status").GetString().Should().Be("running", "the process 'died' mid-health");

        h.Factory.Services.GetRequiredService<RunHooks>().OnStep = null;
        (await h.Factory.Services.GetRequiredService<IRunRecoveryService>().RecoverAsync(default)).Should().Be(1);

        var run = (await h.Admin.GetAsync($"/maintenance/runs/{id}")).Json;
        run.GetProperty("status").GetString().Should().Be("interrupted");
        var guidance = run.GetProperty("guidance").EnumerateArray().Select(g => g.GetString()!).ToList();
        guidance[0].Should().Contain("web: interrupted during \"deploy\"");
        guidance.Should().Contain("Put the shared tag back on the previous image:").And.Contain("docker tag " + FakeDocker.NginxId + " nginx:1.27");
        run.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("step").GetString() == "health").Should().OnlyContain(s => s.GetProperty("status").GetString() == "failed");
        (await h.Admin.GetAsync("/maintenance/runs?status=approved")).Json.GetProperty("runs").GetArrayLength().Should().Be(0);
        var again = await ApproveAsync(h, id, hash);
        again.Status.Should().Be(409, "an interrupted run can never be approved or resumed");
    }

    [Fact]
    public async Task Overlapping_runs_are_serialised_the_second_start_is_declined_while_the_first_executes()
    {
        var h = await HarnessAsync();
        var (id, hash) = await CreateAsync(h);
        var (id2, hash2) = await CreateAsync(h);
        var gate = new TaskCompletionSource();
        h.Factory.Services.GetRequiredService<RunHooks>().OnStep = async (_, step, _, phase) =>
        {
            if (step == "preflight" && phase == "start")
            {
                await gate.Task;
            }
        };
        (await ApproveAsync(h, id, hash)).Status.Should().Be(202);

        var second = await ApproveAsync(h, id2, hash2);
        (second.Status, second.ErrorCode).Should().Be((409, "conflict"));
        second.ErrorMessage.Should().Contain("one run at a time");
        var startNow = await h.Admin.PostAsync("/maintenance/runs", new { project = "shop", startNow = true, confirm = "shop" });
        startNow.Status.Should().Be(409);
        (await h.Admin.GetAsync("/maintenance/runs?status=awaiting_approval")).Json.GetProperty("runs").GetArrayLength().Should().Be(1, "the declined run waits; no orphan was created");

        gate.SetResult();
        (await WaitFinalAsync(h, id)).GetProperty("status").GetString().Should().Be("completed");
        (await ApproveAsync(h, id2, hash2)).Status.Should().Be(202, "serialised: it can go once the first is done");
    }

    [Fact]
    public async Task A_critical_target_with_an_auto_policy_still_waits_for_approval_and_a_non_critical_one_runs_unattended()
    {
        var h = await HarnessAsync();
        foreach (var (kind, key, policy) in new (string, string, object)[] { ("global", "*", new { updateMode = "auto", requireApproval = false }), ("project", "shop", new { critical = true }) })
        {
            (await h.Admin.SendAsync(HttpMethod.Put, $"/maintenance/policies/{kind}/{key}", policy)).Status.Should().Be(200);
        }

        var runs = h.Factory.Services.GetRequiredService<IRunService>();
        var sched = new ScheduledRun("s1", "nightly", "auto", 1, null);
        var critical = await runs.CreateAsync(new CreateRunRequest("shop"), new Actor("system", null), sched, default);
        critical.Status.Should().Be("awaiting_approval");
        critical.Trigger.Should().Be("schedule");
        critical.Plan!.Services[0].Unattended.Blockers.Should().Contain(b => b.Contains("critical", StringComparison.OrdinalIgnoreCase));
        await runs.RejectAsync(critical.Id, new Actor("admin", null), default);

        (await h.Admin.SendAsync(HttpMethod.Put, "/maintenance/policies/project/shop", new { critical = false })).Status.Should().Be(200);
        var auto = await runs.CreateAsync(new CreateRunRequest("shop"), new Actor("system", null), sched, default);
        auto.ApprovedBy.Should().Be("schedule:nightly");
        (await WaitFinalAsync(h, auto.Id)).GetProperty("status").GetString().Should().Be("completed");
    }

    [Fact]
    public async Task Restore_puts_the_recovery_point_back_after_a_confirmed_request()
    {
        var h = await HarnessAsync();
        var (id, hash) = await CreateAsync(h);
        (await ApproveAsync(h, id, hash)).Status.Should().Be(202);
        (await WaitFinalAsync(h, id)).GetProperty("status").GetString().Should().Be("completed");

        var wrong = await h.Admin.PostAsync($"/maintenance/runs/{id}/restore", new { confirm = "nope" });
        (wrong.Status, wrong.ErrorCode).Should().Be((409, "confirmation_required"));
        (await h.Viewer.PostAsync($"/maintenance/runs/{id}/restore", new { confirm = "shop" })).Status.Should().Be(403);

        var ok = await h.Admin.PostAsync($"/maintenance/runs/{id}/restore", new { confirm = "shop" });
        ok.Status.Should().Be(202, ok.Body);
        var op = await T3Support.OperationAsync(h.Admin, ok.Json.GetProperty("operationId").GetString()!);
        op.GetProperty("status").GetString().Should().Be("succeeded", op.ToString());
        _images.Tags["nginx:1.27"].Should().Be(FakeDocker.NginxId);
        h.Docker.Containers[0]!["ImageID"]!.GetValue<string>().Should().Be(FakeDocker.NginxId);
        (await h.Admin.PostAsync("/maintenance/runs/nope/restore", new { confirm = "shop" })).Status.Should().Be(404);
    }

    [Fact]
    public async Task Approval_expires_after_the_configured_hours_and_reject_ends_a_request()
    {
        var h = await HarnessAsync(new Dictionary<string, string> { ["QM_APPROVAL_EXPIRY_HOURS"] = "1" });
        var (id, hash) = await CreateAsync(h);
        var (id2, _) = await CreateAsync(h);
        var rejected = await h.Admin.PostAsync($"/maintenance/runs/{id2}/reject");
        rejected.Json.GetProperty("status").GetString().Should().Be("rejected");
        (await h.Admin.PostAsync($"/maintenance/runs/{id2}/reject")).Status.Should().Be(409);

        h.Factory.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        (await h.Admin.LoginAsync("admin", QmFactory.AdminPassword)).Status.Should().Be(200);
        var late = await ApproveAsync(h, id, hash);
        (late.Status, late.ErrorMessage).Should().Be((409, "The approval request has expired; create a new run"));
        var after = (await h.Admin.GetAsync($"/maintenance/runs/{id}")).Json;
        after.GetProperty("status").GetString().Should().Be("skipped");
        after.GetProperty("reason").GetString().Should().Be("approval expired");
    }
}

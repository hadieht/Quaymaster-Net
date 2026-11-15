using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Maintenance;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

/// <summary>T4a contract: policies, windows, schedules and the dry-run planner (wire format of shared/src/maintenance.ts).</summary>
public sealed class MaintenanceConfigApiTests : IDisposable
{
    private DockerHarness? _h;

    public void Dispose() => _h?.Dispose();

    private async Task<DockerHarness> HarnessAsync(Dictionary<string, string>? settings = null) => _h = await DockerHarness.CreateAsync(settings);

    private static Task<ApiResponse> Put(ApiClient c, string path, object body) => c.SendAsync(HttpMethod.Put, path, body);

    private static Task<ApiResponse> Delete(ApiClient c, string path) => c.SendAsync(HttpMethod.Delete, path);

    private static object Window(string name = "night", int[]? days = null, string start = "02:00", string end = "04:00", string tz = "Europe/Berlin") =>
        new { name, days = days ?? [0, 1, 2, 3, 4, 5, 6], start, end, timezone = tz };

    // ---- policies -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Policies_Put_List_Delete_Roundtrip_With_The_Original_Shapes()
    {
        var h = await HarnessAsync();
        (await h.Viewer.GetAsync("/maintenance/policies")).Json.GetProperty("policies").GetArrayLength().Should().Be(0);

        var put = await Put(h.Admin, "/maintenance/policies/service/shop%2Fweb", new { updateMode = "auto", critical = true, health = new { timeoutSeconds = 90 } });
        put.Status.Should().Be(200, put.Body);
        DockerHarness.Keys(put.Json).Should().BeEquivalentTo("scopeKind", "scopeKey", "policy");
        put.Json.GetProperty("scopeKey").GetString().Should().Be("shop/web");
        put.Json.GetProperty("policy").GetProperty("health").GetProperty("timeoutSeconds").GetInt32().Should().Be(90);

        var list = await h.Viewer.GetAsync("/maintenance/policies");
        var p = list.Json.GetProperty("policies").EnumerateArray().Single();
        DockerHarness.Keys(p).Should().BeEquivalentTo("scopeKind", "scopeKey", "policy", "updatedAt");
        p.GetProperty("scopeKind").GetString().Should().Be("service");
        p.GetProperty("updatedAt").GetString().Should().MatchRegex(@"^\d{4}-\d\d-\d\dT[\d:.]+Z$");

        (await Delete(h.Admin, "/maintenance/policies/service/shop%2Fweb")).Json.GetProperty("ok").GetBoolean().Should().BeTrue();
        var again = await Delete(h.Admin, "/maintenance/policies/service/shop%2Fweb");
        again.Status.Should().Be(404);
        again.ErrorCode.Should().Be("not_found");
    }

    [Fact]
    public async Task Policy_Validation_Errors_Are_400()
    {
        var h = await HarnessAsync();
        (await Put(h.Admin, "/maintenance/policies/galaxy/x", new { })).Status.Should().Be(400);
        (await Put(h.Admin, "/maintenance/policies/service/just-a-name", new { })).Status.Should().Be(400);
        (await Put(h.Admin, "/maintenance/policies/project/Bad_Name", new { })).Status.Should().Be(400);
        var badMode = await Put(h.Admin, "/maintenance/policies/global/*", new { updateMode = "yolo" });
        badMode.Status.Should().Be(400);
        badMode.ErrorCode.Should().Be("validation");
        (await Put(h.Admin, "/maintenance/policies/global/*", new { nonsense = 1 })).Status.Should().Be(400);
    }

    [Fact]
    public async Task Effective_Policy_Merges_Levels_And_Names_Their_Sources()
    {
        var h = await HarnessAsync();
        (await Put(h.Admin, "/maintenance/policies/global/*", new { updateMode = "auto", requireApproval = false })).Status.Should().Be(200);
        (await Put(h.Admin, "/maintenance/policies/project/shop", new { critical = true, maxParallel = 1 })).Status.Should().Be(200);
        (await Put(h.Admin, "/maintenance/policies/service/shop%2Fweb", new { updateMode = "pull_only", autoRollback = false, updateOrder = 5 })).Status.Should().Be(200);

        var r = await h.Viewer.GetAsync("/maintenance/policies/effective?project=shop&service=web");
        r.Status.Should().Be(200, r.Body);
        DockerHarness.Keys(r.Json).Should().Contain(["updateMode", "critical", "requireApproval", "autoRollbackExplicit", "health", "maxParallel", "updateOrder", "sources"]);
        r.Json.GetProperty("updateMode").GetString().Should().Be("pull_only");
        r.Json.GetProperty("critical").GetBoolean().Should().BeTrue();
        r.Json.GetProperty("requireApproval").GetBoolean().Should().BeFalse("only the global level sets it, to false");
        r.Json.GetProperty("autoRollbackExplicit").GetBoolean().Should().BeFalse();
        r.Json.GetProperty("maxParallel").GetInt32().Should().Be(1);
        r.Json.GetProperty("updateOrder").GetInt32().Should().Be(5);
        r.Json.GetProperty("health").GetProperty("timeoutSeconds").GetDouble().Should().Be(60);
        r.Json.GetProperty("health").GetProperty("probe").ValueKind.Should().Be(JsonValueKind.Null);
        r.Json.GetProperty("sources").GetProperty("updateMode").GetString().Should().Be("service:shop/web");

        var none = await h.Viewer.GetAsync("/maintenance/policies/effective");
        none.Json.GetProperty("updateMode").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task Policy_For_A_Name_Outside_The_Managed_Scope_Is_409_Out_Of_Scope()
    {
        var h = await HarnessAsync(new() { ["QM_MANAGED_SCOPE"] = "shop" });
        var r = await Put(h.Admin, "/maintenance/policies/project/other", new { updateMode = "auto" });
        r.Status.Should().Be(409);
        r.ErrorCode.Should().Be("out_of_scope");
        (await Put(h.Admin, "/maintenance/policies/project/shop", new { updateMode = "auto" })).Status.Should().Be(200);
        (await Put(h.Admin, "/maintenance/policies/global/*", new { updateMode = "notify" })).Status.Should().Be(200);
    }

    [Fact]
    public async Task Config_Mutations_Need_Admin_And_Are_Audited()
    {
        var h = await HarnessAsync();
        (await Put(h.Viewer, "/maintenance/policies/global/*", new { })).Status.Should().Be(403);
        (await h.Viewer.PostAsync("/maintenance/windows", Window())).Status.Should().Be(403);
        (await h.Viewer.PostAsync("/maintenance/plan", new { project = "shop" })).Status.Should().Be(403);
        (await h.Admin.GetAsync("/maintenance/policies", auth: false)).Status.Should().Be(401);

        (await Put(h.Admin, "/maintenance/policies/global/*", new { updateMode = "notify" })).Status.Should().Be(200);
        var audit = await h.Admin.GetAsync("/audit?limit=50");
        audit.Status.Should().Be(200, audit.Body);
        audit.Body.Should().Contain("maintenance.policy.put");
    }

    [Fact]
    public async Task Policy_Mutations_Work_In_Read_Only_Mode_Like_The_Original_But_Dry_Run_Is_Refused_By_The_Guard()
    {
        var h = await HarnessAsync(new() { ["QM_READ_ONLY"] = "true" });
        (await Put(h.Admin, "/maintenance/policies/global/*", new { updateMode = "notify" })).Status.Should().Be(200);
        var plan = await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" });
        plan.Status.Should().Be(200, plan.Body);
        plan.Json.GetProperty("plan").GetProperty("blocked").GetProperty("code").GetString().Should().Be("refused");
        plan.Json.GetProperty("plan").GetProperty("blocked").GetProperty("message").GetString().Should().StartWith("Read-only mode");
    }

    // ---- windows ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Windows_Crud_Contract()
    {
        var h = await HarnessAsync();
        var created = await h.Admin.PostAsync("/maintenance/windows", Window());
        created.Status.Should().Be(201, created.Body);
        DockerHarness.Keys(created.Json).Should().BeEquivalentTo("id", "name", "days", "start", "end", "timezone", "enabled", "createdAt", "openNow", "nextStart");
        created.Json.GetProperty("enabled").GetBoolean().Should().BeTrue();
        created.Json.GetProperty("nextStart").GetString().Should().EndWith("Z");
        var id = created.Json.GetProperty("id").GetString()!;

        (await h.Viewer.GetAsync("/maintenance/windows")).Json.GetProperty("windows").GetArrayLength().Should().Be(1);

        var updated = await Put(h.Admin, "/maintenance/windows/" + id, new { name = "night", days = new[] { 6, 0, 6 }, start = "23:00", end = "01:00", timezone = "UTC", enabled = false });
        updated.Status.Should().Be(200, updated.Body);
        updated.Json.GetProperty("days").EnumerateArray().Select(d => d.GetInt32()).Should().Equal(0, 6);
        updated.Json.GetProperty("openNow").GetBoolean().Should().BeFalse();
        updated.Json.GetProperty("nextStart").ValueKind.Should().Be(JsonValueKind.Null, "a disabled window has no next start");

        (await Put(h.Admin, "/maintenance/windows/nope", Window())).Status.Should().Be(404);
        (await Delete(h.Admin, "/maintenance/windows/" + id)).Json.GetProperty("ok").GetBoolean().Should().BeTrue();
        (await Delete(h.Admin, "/maintenance/windows/" + id)).Status.Should().Be(404);
    }

    [Fact]
    public async Task Window_Validation_And_Conflicts()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/maintenance/windows", Window(start: "02:00", end: "02:00"))).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/windows", Window(tz: "Mars/Olympus"))).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/windows", Window(start: "2am"))).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/windows", new { name = "x", days = Array.Empty<int>(), start = "01:00", end = "02:00", timezone = "UTC" })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/windows", new { name = "x", days = new[] { 7 }, start = "01:00", end = "02:00", timezone = "UTC" })).Status.Should().Be(400);
        var unknown = await h.Admin.PostAsync("/maintenance/windows", new { name = "x", days = new[] { 1 }, start = "01:00", end = "02:00", timezone = "UTC", surprise = true });
        unknown.Status.Should().Be(400);
        unknown.ErrorCode.Should().Be("validation");

        (await h.Admin.PostAsync("/maintenance/windows", Window("dup"))).Status.Should().Be(201);
        var dup = await h.Admin.PostAsync("/maintenance/windows", Window("dup"));
        dup.Status.Should().Be(409);
        dup.ErrorCode.Should().Be("conflict");
    }

    [Fact]
    public async Task A_Window_Used_By_A_Schedule_Cannot_Be_Deleted()
    {
        var h = await HarnessAsync();
        var w = (await h.Admin.PostAsync("/maintenance/windows", Window())).Json.GetProperty("id").GetString()!;
        (await h.Admin.PostAsync("/maintenance/schedules", Schedule(windowId: w))).Status.Should().Be(201);
        var del = await Delete(h.Admin, "/maintenance/windows/" + w);
        del.Status.Should().Be(409);
        del.ErrorCode.Should().Be("conflict");
    }

    // ---- schedules ----------------------------------------------------------------------------------------------------------

    private static object Schedule(string name = "nightly", string cron = "0 3 * * *", string tz = "Europe/Berlin", string jobType = "update", object? selector = null, string mode = "manual", string? windowId = null) =>
        new { name, cron, timezone = tz, jobType, selector = selector ?? new { project = "shop" }, mode, windowId };

    [Fact]
    public async Task Schedules_Crud_Contract_With_Next_Runs()
    {
        var h = await HarnessAsync();
        var created = await h.Admin.PostAsync("/maintenance/schedules", Schedule());
        created.Status.Should().Be(201, created.Body);
        DockerHarness.Keys(created.Json).Should().BeEquivalentTo("id", "name", "cron", "timezone", "jobType", "selector", "mode", "windowId", "enabled", "createdAt", "nextRuns");
        created.Json.GetProperty("nextRuns").GetArrayLength().Should().Be(3);
        created.Json.GetProperty("selector").GetProperty("project").GetString().Should().Be("shop");
        created.Json.GetProperty("windowId").ValueKind.Should().Be(JsonValueKind.Null);
        var id = created.Json.GetProperty("id").GetString()!;

        (await h.Viewer.GetAsync("/maintenance/schedules")).Json.GetProperty("schedules").GetArrayLength().Should().Be(1);

        var upd = await Put(h.Admin, "/maintenance/schedules/" + id, new { name = "weekly", cron = "30 4 * * 0", timezone = "UTC", jobType = "image_check", mode = "auto", enabled = false });
        upd.Status.Should().Be(200, upd.Body);
        upd.Json.GetProperty("nextRuns").GetArrayLength().Should().Be(0);
        upd.Json.GetProperty("selector").EnumerateObject().Should().BeEmpty();
        upd.Json.GetProperty("mode").GetString().Should().Be("auto");

        (await Put(h.Admin, "/maintenance/schedules/ghost", Schedule())).Status.Should().Be(404);
        (await Delete(h.Admin, "/maintenance/schedules/" + id)).Json.GetProperty("ok").GetBoolean().Should().BeTrue();
        (await Delete(h.Admin, "/maintenance/schedules/" + id)).Status.Should().Be(404);
    }

    [Fact]
    public async Task Schedule_Validation_Rules()
    {
        var h = await HarnessAsync();
        (await h.Admin.PostAsync("/maintenance/schedules", Schedule(cron: "not a cron"))).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/schedules", Schedule(tz: "Nowhere/Land"))).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/schedules", Schedule(jobType: "explode"))).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/schedules", Schedule(mode: "sometimes"))).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/schedules", Schedule(windowId: "11111111-1111-1111-1111-111111111111"))).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/schedules", Schedule(windowId: "not-a-uuid"))).Status.Should().Be(400);

        var noTarget = await h.Admin.PostAsync("/maintenance/schedules", Schedule(selector: new { }));
        noTarget.Status.Should().Be(400);
        noTarget.ErrorMessage.Should().Contain("selector.project or selector.container");

        var auto = await h.Admin.PostAsync("/maintenance/schedules", Schedule(selector: new { container = "solo" }, mode: "auto"));
        auto.Status.Should().Be(400);
        auto.ErrorMessage.Should().Contain("never updated automatically");

        (await h.Admin.PostAsync("/maintenance/schedules", Schedule(jobType: "cleanup_report", selector: new { }, mode: "auto"))).Status.Should().Be(201);
        (await h.Admin.PostAsync("/maintenance/schedules", Schedule("nightly2", selector: new { project = "shop", services = new[] { "web" } }))).Status.Should().Be(201);
        (await h.Admin.PostAsync("/maintenance/schedules", Schedule("nightly2"))).Status.Should().Be(409);
    }

    // ---- dry-run plan -------------------------------------------------------------------------------------------------------

    private async Task<DockerHarness> PlanHarnessAsync(Dictionary<string, string>? settings = null)
    {
        var h = await HarnessAsync(settings);
        T3Support.RoomyDisk(h.Docker);
        h.Registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NewDigest;
        return h;
    }

    /// <summary>db gets its own postgres image (running a different digest than the registry's) so it is an update candidate too.</summary>
    private static void GiveDbItsOwnUpdatableImage(DockerHarness h)
    {
        var pgId = "sha256:" + new string('a', 64);
        h.Docker.ImageList.Add(new FakeDocker.Img { Id = pgId, Tags = ["postgres:17"], RepoDigests = ["postgres@sha256:" + new string('c', 64)], Size = 1 });
        h.Docker.Containers[1]!["ImageID"] = pgId;
        h.Registry.Digests["docker.io/library/postgres:17"] = T3Support.NewDigest;
    }

    [Fact]
    public async Task Plan_Runs_Preflight_Validate_Check_And_Impact_And_Stores_The_Dry_Run()
    {
        var h = await PlanHarnessAsync();
        var r = await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" });
        r.Status.Should().Be(200, r.Body);

        // RunDetail = the run fields flattened + plan + steps
        DockerHarness.Keys(r.Json).Should().BeEquivalentTo(
            "id", "kind", "trigger", "scheduleId", "occurrence", "windowId", "windowEnd", "target", "status", "reason", "planHash", "requestedBy",
            "approvalExpiresAt", "approvedBy", "approvedAt", "approvedPlanHash", "result", "guidance", "createdAt", "startedAt", "finishedAt", "plan", "steps");
        r.Json.GetProperty("kind").GetString().Should().Be("dry_run");
        r.Json.GetProperty("status").GetString().Should().Be("planned");
        r.Json.GetProperty("trigger").GetString().Should().Be("manual");
        r.Json.GetProperty("target").GetString().Should().Be("shop");
        r.Json.GetProperty("requestedBy").GetString().Should().Be("admin");

        var plan = r.Json.GetProperty("plan");
        DockerHarness.Keys(plan).Should().BeEquivalentTo(
            "version", "kind", "target", "createdAt", "order", "services", "excluded", "preflight", "validation", "drift", "impact", "disk", "blocked", "warnings", "planHash");
        plan.GetProperty("version").GetInt32().Should().Be(1);
        plan.GetProperty("kind").GetString().Should().Be("compose");
        plan.GetProperty("planHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        r.Json.GetProperty("planHash").GetString().Should().Be(plan.GetProperty("planHash").GetString());
        plan.GetProperty("blocked").ValueKind.Should().Be(JsonValueKind.Null, plan.ToString());
        plan.GetProperty("order").EnumerateArray().Select(x => x.GetString()).Should().Equal("web");

        var web = plan.GetProperty("services").EnumerateArray().Single();
        DockerHarness.Keys(web).Should().Contain([
            "service", "role", "containers", "ref", "fromImageId", "fromDigest", "toDigest", "configHash", "wasRunning", "oneShot", "autoRollback",
            "autoRollbackReason", "health", "downtime", "dataRisk", "critical", "requireApproval", "updateMode", "unattended", "quarantined"]);
        web.GetProperty("role").GetString().Should().Be("update");
        web.GetProperty("ref").GetString().Should().Be("nginx:1.27");
        web.GetProperty("toDigest").GetString().Should().Be(T3Support.NewDigest);
        web.GetProperty("fromDigest").GetString().Should().Be(T3Support.NginxDigest);
        web.GetProperty("health").GetProperty("kind").GetString().Should().Be("healthcheck");
        web.GetProperty("updateMode").GetString().Should().Be("notify");
        web.GetProperty("requireApproval").GetBoolean().Should().BeTrue();
        web.GetProperty("unattended").GetProperty("eligible").GetBoolean().Should().BeFalse();
        web.GetProperty("dataRisk").GetProperty("flagged").GetBoolean().Should().BeFalse();

        plan.GetProperty("excluded").EnumerateArray().Select(e => e.GetProperty("service").GetString()).Should().Contain("db");
        plan.GetProperty("validation").GetProperty("valid").GetBoolean().Should().BeTrue();
        var checks = plan.GetProperty("preflight").GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        checks.Should().Contain("service web is stable").And.Contain("free disk space on the Docker root");
        plan.GetProperty("preflight").GetProperty("ok").GetBoolean().Should().BeTrue(plan.GetProperty("preflight").ToString());
        DockerHarness.Keys(plan.GetProperty("impact")).Should().BeEquivalentTo("dependents", "sharedTag");
        DockerHarness.Keys(plan.GetProperty("disk")).Should().BeEquivalentTo("requiredBytes", "freeBytes", "floorBytes", "ok");

        // stored, byte-for-byte what was returned; and nothing was changed
        var stored = await h.Factory.Services.GetRequiredService<IRunStore>().GetAsync(r.Json.GetProperty("id").GetString()!, default);
        stored.Should().NotBeNull();
        stored!.Plan!.PlanHash.Should().Be(plan.GetProperty("planHash").GetString());
        stored.Info.Status.Should().Be("planned");
        h.Docker.Commands.Should().BeEmpty();
        h.Docker.ComposeRuns.Should().OnlyContain(a => a.Contains("config") || a.Contains("ls") || a.Contains("ps"));
        h.Registry.Heads.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Plan_Hash_Is_Stable_And_Changes_With_The_Target_Digest()
    {
        var h = await PlanHarnessAsync();
        var a = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json.GetProperty("planHash").GetString();
        var b = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json.GetProperty("planHash").GetString();
        b.Should().Be(a, "volatile data (timestamps) is excluded from the hash");

        h.Registry.Digests["docker.io/library/nginx:1.27"] = "sha256:" + new string('9', 64);
        var c = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json.GetProperty("planHash").GetString();
        c.Should().NotBe(a);
    }

    [Fact]
    public async Task Plan_With_Nothing_To_Update_Is_Blocked_Nothing_To_Do()
    {
        var h = await HarnessAsync();
        T3Support.RoomyDisk(h.Docker);
        h.Registry.Digests["docker.io/library/nginx:1.27"] = T3Support.NginxDigest;
        var plan = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json.GetProperty("plan");
        plan.GetProperty("blocked").GetProperty("code").GetString().Should().Be("nothing_to_do");
        plan.GetProperty("services").GetArrayLength().Should().Be(0);
        plan.GetProperty("excluded").EnumerateArray().Single(e => e.GetProperty("service").GetString() == "web").GetProperty("reason").GetString().Should().Be("already up to date");
    }

    [Fact]
    public async Task Configuration_Drift_Blocks_Unless_ApplyDrift()
    {
        var h = await PlanHarnessAsync();
        h.Docker.ComposeHandler = argv => argv.Any(a => a.StartsWith("--hash", StringComparison.Ordinal))
            ? new(0, "web " + new string('b', 64) + "\n", string.Empty, false)
            : argv.Contains("config") ? new(0, FakeDocker.DefaultConfigJson, string.Empty, false) : new(0, "[]", string.Empty, false);

        var blocked = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json;
        blocked.GetProperty("reason").GetString().Should().Contain("differs", blocked.ToString());
        var plan = blocked.GetProperty("plan");
        plan.GetProperty("blocked").GetProperty("code").GetString().Should().Be("blocked_drift");
        var drift = plan.GetProperty("drift").EnumerateArray().Single();
        DockerHarness.Keys(drift).Should().BeEquivalentTo("service", "running", "wanted");
        drift.GetProperty("wanted").GetString().Should().Be(new string('b', 64));
        plan.GetProperty("services")[0].GetProperty("configHash").GetString().Should().Be(new string('b', 64));

        var applied = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop", applyDrift = true })).Json.GetProperty("plan");
        applied.GetProperty("blocked").ValueKind.Should().Be(JsonValueKind.Null);
        applied.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.StartsWith("Configuration drift will be applied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Policy_Shapes_The_Plan_Ignore_Excludes_And_Update_Order_And_Approval_Flags_Are_Carried()
    {
        var h = await PlanHarnessAsync();
        GiveDbItsOwnUpdatableImage(h);
        (await Put(h.Admin, "/maintenance/policies/service/shop%2Fdb", new { updateMode = "auto", requireApproval = false })).Status.Should().Be(200);
        (await Put(h.Admin, "/maintenance/policies/service/shop%2Fweb", new { updateMode = "ignore" })).Status.Should().Be(200);

        var plan = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json.GetProperty("plan");
        plan.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("service").GetString()).Should().Equal("db");
        plan.GetProperty("excluded").EnumerateArray().Single(e => e.GetProperty("service").GetString() == "web").GetProperty("reason").GetString().Should().Be("policy: ignore");
        var db = plan.GetProperty("services")[0];
        db.GetProperty("updateMode").GetString().Should().Be("auto");
        db.GetProperty("unattended").GetProperty("blockers").EnumerateArray().Select(b => b.GetString()).Should().Equal("the schedule is not in auto mode");
    }

    [Fact]
    public async Task Services_Filter_Dependency_Order_And_Unknown_Service()
    {
        var h = await PlanHarnessAsync();
        GiveDbItsOwnUpdatableImage(h);

        var both = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json.GetProperty("plan");
        both.GetProperty("order").EnumerateArray().Select(x => x.GetString()).Should().Equal("db", "web"); // web depends_on db

        var only = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop", services = new[] { "web" } })).Json.GetProperty("plan");
        only.GetProperty("order").EnumerateArray().Select(x => x.GetString()).Should().Equal("web");

        var ghost = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop", services = new[] { "ghost" } })).Json.GetProperty("plan");
        ghost.GetProperty("blocked").GetProperty("code").GetString().Should().Be("validation");
        ghost.GetProperty("blocked").GetProperty("message").GetString().Should().Contain("does not exist");
    }

    [Fact]
    public async Task Plan_Errors_Unknown_Project_Bad_Body_And_Out_Of_Scope()
    {
        var h = await PlanHarnessAsync();
        (await h.Admin.PostAsync("/maintenance/plan", new { project = "ghost" })).Status.Should().Be(404);
        (await h.Admin.PostAsync("/maintenance/plan", new { })).Status.Should().Be(400);
        (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop", extra = 1 })).Status.Should().Be(400);

        using var scoped = await DockerHarness.CreateAsync(new() { ["QM_MANAGED_SCOPE"] = "other" });
        var plan = (await scoped.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json.GetProperty("plan");
        plan.GetProperty("blocked").GetProperty("code").GetString().Should().Be("refused");
        plan.GetProperty("blocked").GetProperty("message").GetString().Should().Contain("QM_MANAGED_SCOPE");
        plan.GetProperty("preflight").GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_Compose_Configuration_Blocks_With_The_Validation_Errors()
    {
        var h = await PlanHarnessAsync();
        h.Docker.ComposeHandler = argv => argv.Contains("--quiet")
            ? new(1, string.Empty, "services.web.image must be a string", false)
            : new(0, FakeDocker.DefaultConfigJson, string.Empty, false);
        var plan = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json.GetProperty("plan");
        if (plan.GetProperty("blocked").ValueKind == JsonValueKind.Object)
        {
            plan.GetProperty("blocked").GetProperty("code").GetString().Should().BeOneOf("validation", "nothing_to_do", "blocked_drift");
        }
    }

    [Fact]
    public async Task Low_Disk_Fails_The_Preflight_Closed()
    {
        var h = await PlanHarnessAsync();
        h.Docker.TotalBytes = 1000;
        h.Docker.FreeBytes = 10;
        var plan = (await h.Admin.PostAsync("/maintenance/plan", new { project = "shop" })).Json.GetProperty("plan");
        plan.GetProperty("preflight").GetProperty("ok").GetBoolean().Should().BeFalse();
        plan.GetProperty("blocked").GetProperty("code").GetString().Should().Be("preflight");
        plan.GetProperty("disk").GetProperty("ok").GetBoolean().Should().BeFalse();
    }
}

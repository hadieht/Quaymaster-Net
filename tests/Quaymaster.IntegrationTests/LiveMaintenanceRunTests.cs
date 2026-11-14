using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Maintenance;
using Quaymaster.IntegrationTests.Support;
using Xunit.Abstractions;

namespace Quaymaster.IntegrationTests;

/// <summary>
/// T4c live verification, against the REAL daemon and the throw-away <c>qmtest-upd</c> stack (test-fixtures/, local registry on
/// 127.0.0.1:5055): dry-run plan → create run → approve → execute (real <c>docker compose up</c>, pull by digest, health gate) → verify
/// the new image ID is running → one restore cycle → verify the old image ID is running again. QM_MANAGED_SCOPE is pinned to
/// <c>qmtest-*</c>, so the other stacks on the host are untouchable (their container ids are compared before and after). Cleaned up in
/// <c>finally</c>. Excluded with <c>--filter "Category!=Live"</c>; without a reachable daemon it passes vacuously.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveMaintenanceRunTests(ITestOutputHelper output) : IDisposable
{
    private const string Socket = "/var/run/docker.sock";
    private QmFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Quaymaster.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static (int Code, string Out) Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit(300_000);
            return (p.ExitCode, (o.Result + e.Result).Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, string.Empty);
        }
    }

    private static bool DaemonReachable() => File.Exists(Socket) && Run("docker", "version", "--format", "{{.Server.Version}}").Code == 0;

    private static string Fixture(string script, params string[] args) => Run("bash", [Path.Combine(RepoRoot(), "test-fixtures", "scripts", script), .. args]).Out;

    /// <summary>The image ID the running <c>app</c> container of qmtest-upd was created from.</summary>
    private static string AppImageId()
    {
        var id = Run("docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=qmtest-upd", "--filter", "label=com.docker.compose.service=app").Out;
        id.Should().NotBeNullOrEmpty();
        return Run("docker", "inspect", "--format", "{{.Image}}", id.Split('\n')[0]).Out;
    }

    private static string OtherContainers() =>
        string.Join('\n', Run("docker", "ps", "-aq", "--no-trunc").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => true).Order());

    private static string FixtureContainers() => Run("docker", "ps", "-aq", "--no-trunc", "--filter", "label=com.docker.compose.project=qmtest-upd").Out;

    private static async Task<JsonElement> WaitRunAsync(ApiClient admin, string id)
    {
        for (var i = 0; i < 900; i++)
        {
            var r = await admin.GetAsync($"/maintenance/runs/{id}");
            if (r.Json.GetProperty("status").GetString() is not ("approved" or "running"))
            {
                return r.Json.Clone();
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("run did not finish");
    }

    private static async Task<JsonElement> WaitOperationAsync(ApiClient admin, string id)
    {
        for (var i = 0; i < 900; i++)
        {
            var r = await admin.GetAsync($"/operations/{id}");
            if (r.Json.GetProperty("status").GetString() != "running")
            {
                return r.Json.Clone();
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("operation did not finish");
    }

    [Fact]
    public async Task Full_cycle_plan_approve_execute_verify_new_image_restore_verify_old_image()
    {
        if (!DaemonReachable())
        {
            output.WriteLine("no Docker daemon: skipped");
            return;
        }

        QmFactory? factory = null;
        try
        {
            output.WriteLine(Fixture("upd-up.sh"));
            output.WriteLine(Fixture("publish.sh", "app", "2", "good")); // the registry's :latest moves; the daemon keeps v1 until the run pulls
            var oldImage = AppImageId();
            var fixtureBefore = FixtureContainers();
            var othersBefore = OtherContainers();
            output.WriteLine($"app runs {oldImage}");

            factory = _factory = new QmFactory(
                new Dictionary<string, string>
                {
                    ["QM_MANAGED_SCOPE"] = "qmtest-*",
                    ["QM_READ_ONLY"] = "false",
                    ["QM_DOCKER_SOCKET"] = Socket,
                    ["QM_STACK_PATHS"] = Path.Combine(RepoRoot(), "test-fixtures", "stacks"),
                },
                services => services.AddSingleton(new HealthTimings
                {
                    PollMs = 1000,
                    // real waiting for the real daemon; the fake clock follows so the gate's stabilisation/timeouts measure the same time
                    Sleep = async (ms, ct) =>
                    {
                        await Task.Delay(ms, ct);
                        factory!.Time.Advance(TimeSpan.FromMilliseconds(ms));
                    },
                }));
            factory.Time.SetUtcNow(DateTimeOffset.UtcNow);
            var admin = await factory.NewAdminAsync();

            // 1. dry run
            var dry = await admin.PostAsync("/maintenance/plan", new { project = "qmtest-upd" });
            dry.Status.Should().Be(200, dry.Body);
            dry.Json.GetProperty("status").GetString().Should().Be("planned");
            var planHash = dry.Json.GetProperty("planHash").GetString()!;
            var services = dry.Json.GetProperty("plan").GetProperty("services").EnumerateArray().Select(s => s.GetProperty("service").GetString()).ToList();
            services.Should().Contain("app");
            output.WriteLine($"plan {planHash[..12]} services: {string.Join(",", services)}");

            // 2. create (waits for approval: default policy requires it) and approve exactly the reviewed hash
            var created = await admin.PostAsync("/maintenance/runs", new { project = "qmtest-upd" });
            created.Status.Should().Be(201, created.Body);
            created.Json.GetProperty("status").GetString().Should().Be("awaiting_approval");
            created.Json.GetProperty("planHash").GetString().Should().Be(planHash, "the plan is deterministic while nothing changed");
            var runId = created.Json.GetProperty("id").GetString()!;
            var approve = await admin.PostAsync($"/maintenance/runs/{runId}/approve", new { planHash });
            approve.Status.Should().Be(202, approve.Body);

            // 3. execute, then verify the NEW image id is what runs
            var done = await WaitRunAsync(admin, runId);
            output.WriteLine(done.ToString());
            done.GetProperty("status").GetString().Should().Be("completed", done.GetProperty("reason").ToString());
            done.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetString()).Should().Contain(["preflight", "recovery_point", "pull", "deploy", "health", "record"]);
            done.GetProperty("steps").EnumerateArray().Should().OnlyContain(s => s.GetProperty("status").GetString() == "succeeded");
            var newImage = AppImageId();
            newImage.Should().NotBe(oldImage, "the app container was recreated on the pulled image");
            output.WriteLine($"app now runs {newImage}");

            var points = await factory.Services.GetRequiredService<Quaymaster.Application.Updates.IRollbackStore>().ForRunAsync(runId, default);
            points.Should().Contain(p => p.Service == "app" && p.ImageId == oldImage, "the recovery point holds the image that ran before");

            // 4. one restore cycle -> the OLD image id runs again
            var wrongConfirm = await admin.PostAsync($"/maintenance/runs/{runId}/restore", new { confirm = "nope" });
            (wrongConfirm.Status, wrongConfirm.ErrorCode).Should().Be((409, "confirmation_required"));
            var restore = await admin.PostAsync($"/maintenance/runs/{runId}/restore", new { confirm = "qmtest-upd" });
            restore.Status.Should().Be(202, restore.Body);
            var op = await WaitOperationAsync(admin, restore.Json.GetProperty("operationId").GetString()!);
            op.GetProperty("status").GetString().Should().Be("succeeded", op.ToString());
            AppImageId().Should().Be(oldImage, "restore puts the recorded previous image back");

            // 5. nothing outside the fixture project was touched
            OtherContainers().Split('\n').Except(FixtureContainers().Split('\n')).Order()
                .Should().Equal(othersBefore.Split('\n').Except(fixtureBefore.Split('\n')).Order());
        }
        finally
        {
            output.WriteLine(Fixture("upd-down.sh"));
        }
    }
}

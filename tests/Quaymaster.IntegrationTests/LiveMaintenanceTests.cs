using System.Diagnostics;
using System.Text.Json;
using Quaymaster.IntegrationTests.Support;
using Xunit.Abstractions;

namespace Quaymaster.IntegrationTests;

/// <summary>
/// T4a against the REAL daemon and the throw-away <c>qmtest-upd</c> fixture stack (test-fixtures/, local registry on 127.0.0.1:5055).
/// Read-only apart from standing the fixture up and tearing it down with its own scripts: the dry-run changes nothing, which the test
/// proves by comparing container ids and start times before and after. QM_MANAGED_SCOPE is pinned to <c>qmtest-*</c>. Excluded in CI
/// without Docker with <c>--filter "Category!=Live"</c>; without a reachable daemon the test passes vacuously.
/// </summary>
[Trait("Category", "Live")]
[Collection("Live")]
public sealed class LiveMaintenanceTests(ITestOutputHelper output) : IDisposable
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

    /// <summary>Id, image id, state and start time of every container of the stack: stable while nothing is touched.</summary>
    private static string Snapshot()
    {
        var ids = Run("docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=qmtest-upd").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order().ToArray();
        return ids.Length == 0 ? string.Empty : Run("docker", ["inspect", "--format", "{{.Id}} {{.Image}} {{.State.Status}} {{.State.StartedAt}}", .. ids]).Out;
    }

    [Fact]
    public async Task Effective_policy_and_dry_run_plan_against_the_real_qmtest_upd_stack()
    {
        if (!DaemonReachable())
        {
            output.WriteLine("no Docker daemon: skipped");
            return;
        }

        try
        {
            output.WriteLine(Fixture("upd-up.sh"));
            output.WriteLine(Fixture("publish.sh", "app", "2", "good")); // the registry's :latest moves; the daemon keeps v1 until something pulls
            var before = Snapshot();
            before.Split('\n').Should().HaveCount(3);

            _factory = new QmFactory(new Dictionary<string, string>
            {
                ["QM_MANAGED_SCOPE"] = "qmtest-*",
                ["QM_READ_ONLY"] = "false",
                ["QM_DOCKER_SOCKET"] = Socket,
                ["QM_STACK_PATHS"] = Path.Combine(RepoRoot(), "test-fixtures", "stacks"),
            });
            _factory.Time.SetUtcNow(DateTimeOffset.UtcNow);
            var admin = await _factory.NewAdminAsync();

            // effective policy: defaults, then merged levels
            var defaults = await admin.GetAsync("/maintenance/policies/effective?project=qmtest-upd&service=app");
            defaults.Status.Should().Be(200, defaults.Body);
            defaults.Json.GetProperty("updateMode").GetString().Should().Be("notify");
            defaults.Json.GetProperty("requireApproval").GetBoolean().Should().BeTrue();
            (await admin.SendAsync(HttpMethod.Put, "/maintenance/policies/project/qmtest-upd", new { updateMode = "auto", requireApproval = false })).Status.Should().Be(200);
            (await admin.SendAsync(HttpMethod.Put, "/maintenance/policies/service/qmtest-upd%2Fdb", new { autoRollback = true, updateMode = "ignore" })).Status.Should().Be(200);
            var app = (await admin.GetAsync("/maintenance/policies/effective?project=qmtest-upd&service=app")).Json;
            app.GetProperty("updateMode").GetString().Should().Be("auto");
            app.GetProperty("requireApproval").GetBoolean().Should().BeFalse();
            app.GetProperty("sources").GetProperty("updateMode").GetString().Should().Be("project:qmtest-upd");

            // out of scope is refused with the real guard
            (await admin.SendAsync(HttpMethod.Put, "/maintenance/policies/project/not-a-fixture", new { updateMode = "auto" })).ErrorCode.Should().Be("out_of_scope");

            // dry run
            var r = await admin.PostAsync("/maintenance/plan", new { project = "qmtest-upd" });
            r.Status.Should().Be(200, r.Body);
            output.WriteLine(r.Body);
            var plan = r.Json.GetProperty("plan");
            plan.GetProperty("validation").GetProperty("valid").GetBoolean().Should().BeTrue(plan.GetProperty("validation").ToString());
            var names = plan.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("service").GetString()).ToList();
            names.Should().Contain("app");
            var appPlan = plan.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("service").GetString() == "app");
            appPlan.GetProperty("role").GetString().Should().Be("update");
            appPlan.GetProperty("toDigest").GetString().Should().StartWith("sha256:");
            appPlan.GetProperty("fromDigest").GetString().Should().NotBe(appPlan.GetProperty("toDigest").GetString());
            appPlan.GetProperty("configHash").GetString().Should().MatchRegex("^[0-9a-f]{16,}$", "the real `compose config --hash`");
            appPlan.GetProperty("health").GetProperty("kind").GetString().Should().Be("healthcheck");
            appPlan.GetProperty("updateMode").GetString().Should().Be("auto");
            plan.GetProperty("excluded").EnumerateArray().Single(e => e.GetProperty("service").GetString() == "db").GetProperty("reason").GetString().Should().Be("policy: ignore");
            plan.GetProperty("impact").GetProperty("dependents").EnumerateArray().Select(d => d.GetProperty("service").GetString()).Should().Contain("worker");
            plan.GetProperty("planHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
            r.Json.GetProperty("status").GetString().Should().Be("planned");

            // a second plan is byte-identical in its hash; nothing on the host changed
            (await admin.PostAsync("/maintenance/plan", new { project = "qmtest-upd" })).Json.GetProperty("planHash").GetString()
                .Should().Be(plan.GetProperty("planHash").GetString());
            Snapshot().Should().Be(before);
        }
        finally
        {
            output.WriteLine(Fixture("upd-down.sh"));
        }
    }
}

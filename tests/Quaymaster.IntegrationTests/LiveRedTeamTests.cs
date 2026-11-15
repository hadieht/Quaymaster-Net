using System.Diagnostics;
using Quaymaster.IntegrationTests.Support;
using Xunit.Abstractions;

namespace Quaymaster.IntegrationTests;

/// <summary>
/// Red-team T7 repros against the REAL daemon, on throw-away <c>qmtest-*</c> fixtures only (QM_MANAGED_SCOPE=qmtest-*):
/// F-01 (the <c>qmtest-secrets</c> fixture must never leak a learned value to a viewer) and F-02 (a QM_PROTECTED namespace
/// dependent must block the maintenance plan and must not be recreated). Excluded with <c>--filter "Category!=Live"</c>;
/// without a reachable daemon they pass vacuously.
/// </summary>
[Trait("Category", "Live")]
[Collection("Live")]
public sealed class LiveRedTeamTests(ITestOutputHelper output) : IDisposable
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

    private QmFactory NewFactory(Dictionary<string, string>? extra = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["QM_MANAGED_SCOPE"] = "qmtest-*",
            ["QM_READ_ONLY"] = "false",
            ["QM_DOCKER_SOCKET"] = Socket,
            ["QM_STACK_PATHS"] = Path.Combine(RepoRoot(), "test-fixtures", "stacks"),
        };
        foreach (var (k, v) in extra ?? [])
        {
            settings[k] = v;
        }

        return _factory = new QmFactory(settings);
    }

    [Fact]
    public async Task F01_Qmtest_Secrets_Values_Never_Reach_A_Viewer_In_Any_Read_Endpoint()
    {
        if (!DaemonReachable())
        {
            output.WriteLine("no Docker daemon: skipped");
            return;
        }

        string[] secrets = ["fx-env-password-7c1", "fx-env-token-4d2", "fx-query-pass-5e6", "fx-label-secret-2f4", "fx-config-content-6g7", "fx-ext-secret-9a1"];
        try
        {
            output.WriteLine(Fixture("up.sh", "secrets"));
            var admin = await NewFactory().NewAdminAsync();
            (await admin.PostAsync("/users", new { username = "vera", password = "viewer password 1", role = "viewer" })).Status.Should().Be(201);
            var viewer = _factory!.NewClient();
            (await viewer.LoginAsync("vera", "viewer password 1")).Status.Should().Be(200);

            // load the containers first: that is what teaches the scrubber the values
            var list = await viewer.GetAsync("/containers");
            list.Status.Should().Be(200);
            var id = list.Json.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "qmtest-secrets-leaky-1").GetProperty("id").GetString()!;

            var paths = new[]
            {
                "/containers", $"/containers/{id}", "/containers/qmtest-secrets-leaky-1", "/compose/projects", "/compose/projects/qmtest-secrets",
                "/compose/projects/qmtest-secrets/config", "/compose/projects/qmtest-secrets/graph", "/images", "/volumes", "/networks", "/events", "/audit",
            };
            foreach (var path in paths)
            {
                var r = await viewer.GetAsync(path);
                output.WriteLine($"viewer GET {path} -> {r.Status}");
                r.Status.Should().BeOneOf(200, 403, 404, 409);
                foreach (var secret in secrets)
                {
                    r.Body.Should().NotContain(secret, $"{path} must not leak {secret}");
                }
            }

            // the same through the admin (admins see more, but never raw secrets)
            foreach (var path in new[] { $"/containers/{id}", "/compose/projects/qmtest-secrets/config" })
            {
                var r = await admin.GetAsync(path);
                r.Status.Should().Be(200, path);
                foreach (var secret in secrets)
                {
                    r.Body.Should().NotContain(secret, $"{path} (admin) must not leak {secret}");
                }
            }
        }
        finally
        {
            output.WriteLine(Fixture("down.sh"));
        }
    }

    [Fact]
    public async Task F02_A_Protected_Namespace_Dependent_Blocks_The_Plan_And_Is_Never_Recreated()
    {
        if (!DaemonReachable())
        {
            output.WriteLine("no Docker daemon: skipped");
            return;
        }

        var stack = Path.Combine(RepoRoot(), "test-fixtures", "stacks", "qmtest-rt8ns");
        var compose = Path.Combine(stack, "compose.yaml");
        string[] dc = ["compose", "--project-name", "qmtest-rt8ns", "--project-directory", stack, "--file", compose];
        try
        {
            output.WriteLine(Fixture("upd-up.sh")); // registry + qmtest-app:latest (v1) in the daemon
            Directory.CreateDirectory(stack);
            await File.WriteAllTextAsync(
                compose,
                """
                name: qmtest-rt8ns
                services:
                  provider:
                    image: 127.0.0.1:5055/qmtest-app:latest
                    pull_policy: never
                    init: true
                    stop_grace_period: 2s
                    labels: { quaymaster.test: "true" }
                    healthcheck: { test: ["CMD", "test", "-f", "/tmp/ready"], interval: 2s, timeout: 2s, retries: 3, start_period: 1s }
                  side:
                    image: alpine:latest
                    pull_policy: never
                    init: true
                    stop_grace_period: 2s
                    network_mode: "service:provider"
                    depends_on: [provider]
                    command: ["sh", "-c", "exec sleep 3600"]
                    labels: { quaymaster.test: "true" }
                """);
            Run("docker", [.. dc, "up", "--detach", "--no-build", "--pull", "never", "--wait", "--wait-timeout", "60"]).Code.Should().Be(0);
            output.WriteLine(Fixture("publish.sh", "app", "2", "good")); // an update is available for the provider

            string SideId() => Run("docker", "ps", "-aq", "--no-trunc", "--filter", "name=qmtest-rt8ns-side-1").Out;
            var sideBefore = SideId();
            sideBefore.Should().NotBeNullOrEmpty();

            var admin = await NewFactory(new Dictionary<string, string> { ["QM_PROTECTED"] = "qmtest-rt8ns-side-1" }).NewAdminAsync();

            // a direct action on the protected dependent is refused (the baseline the red team compared against) ...
            var direct = await admin.PostAsync("/containers/qmtest-rt8ns-side-1/actions/restart");
            direct.Status.Should().Be(409);

            // ... and so is a maintenance plan that would recreate it together with its provider
            var dry = await admin.PostAsync("/maintenance/plan", new { project = "qmtest-rt8ns", applyDrift = true });
            dry.Status.Should().Be(200, dry.Body);
            var blocked = dry.Json.GetProperty("plan").GetProperty("blocked");
            blocked.GetProperty("code").GetString().Should().Be("refused", dry.Body);
            blocked.GetProperty("message").GetString().Should().Contain("side");

            var create = await admin.PostAsync("/maintenance/runs", new { project = "qmtest-rt8ns", applyDrift = true });
            create.Json.GetProperty("status").GetString().Should().Be("blocked", "a blocked plan is recorded as a blocked run, never executed: " + create.Body);
            SideId().Should().Be(sideBefore, "the protected container was not recreated");
        }
        finally
        {
            Run("docker", [.. dc, "down", "--volumes", "--timeout", "3"]);
            if (Directory.Exists(stack))
            {
                Directory.Delete(stack, true);
            }

            output.WriteLine(Fixture("upd-down.sh"));
        }
    }
}

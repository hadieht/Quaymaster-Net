using System.Diagnostics;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

/// <summary>
/// Runs the real adapter against the local daemon. Excluded in CI without Docker with <c>--filter "Category!=Live"</c>;
/// when run without a reachable daemon they pass vacuously (no daemon, nothing to verify) instead of failing.
/// Mutations only ever touch a throwaway container labelled <c>quaymaster.test=true</c> that the test creates and removes.
/// </summary>
[Trait("Category", "Live")]
[Collection("Live")]
public sealed class LiveDockerTests : IDisposable
{
    private const string Socket = "/var/run/docker.sock";
    private QmFactory? _factory;

    public void Dispose() => _factory?.Dispose();

    private static bool DaemonReachable() => File.Exists(Socket) && Run("docker", "version", "--format", "{{.Server.Version}}").Code == 0;

    private static (int Code, string Out) Run(params string[] argv)
    {
        try
        {
            var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in argv.Skip(1))
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return (p.ExitCode, output.Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, string.Empty);
        }
    }

    private async Task<ApiClient> AdminAsync(bool readOnly)
    {
        _factory = new QmFactory(new Dictionary<string, string>
        {
            ["QM_MANAGED_SCOPE"] = "*",
            ["QM_READ_ONLY"] = readOnly ? "true" : "false",
            ["QM_DOCKER_SOCKET"] = Socket,
            ["QM_STACK_PATHS"] = "/nonexistent-qm-test",
        });
        return await _factory.NewAdminAsync();
    }

    [Fact]
    public async Task Lists_Containers_Reports_Mode_And_Disk_Usage_Read_Only()
    {
        if (!DaemonReachable())
        {
            return;
        }

        var admin = await AdminAsync(readOnly: true);
        var containers = await admin.GetAsync("/containers");
        containers.Status.Should().Be(200, containers.Body);
        containers.Json.EnumerateArray().Should().OnlyContain(c => !c.GetProperty("mutable").GetBoolean(), "read-only posture");

        var health = await admin.GetAsync("/system/health");
        health.Json.GetProperty("docker").GetString().Should().Be("ok");

        var df = await admin.GetAsync("/system/df?refresh=1");
        df.Status.Should().Be(200, df.Body);

        var info = await admin.GetAsync("/system/info");
        info.Status.Should().Be(200);
        info.Json.TryGetProperty("ServerVersion", out _).Should().BeTrue();

        (await admin.GetAsync("/images")).Status.Should().Be(200);
        (await admin.GetAsync("/volumes")).Status.Should().Be(200);
        (await admin.GetAsync("/networks")).Status.Should().Be(200);
        (await admin.GetAsync("/compose/projects")).Status.Should().Be(200);
    }

    [Fact]
    public async Task Throwaway_Container_Create_Logs_And_Remove_Cycle()
    {
        if (!DaemonReachable())
        {
            return;
        }

        var image = Run("docker", "image", "ls", "--format", "{{.Repository}}:{{.Tag}}", "--filter", "dangling=false").Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(i => !i.Contains("<none>", StringComparison.Ordinal));
        if (image is null)
        {
            return; // no local image to run; never pull in a test
        }

        var name = "qm-live-test-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var created = Run("docker", "run", "--name", name, "--label", "quaymaster.test=true", "--entrypoint", "echo", image, "qm-live-hello");
            created.Code.Should().Be(0);

            var admin = await AdminAsync(readOnly: false);
            var inspect = await admin.GetAsync("/containers/" + name);
            inspect.Status.Should().Be(200, inspect.Body);
            inspect.Json.GetProperty("summary").GetProperty("mutable").GetBoolean().Should().BeTrue();

            var logs = await admin.GetAsync($"/containers/{name}/logs?tail=10");
            logs.Status.Should().Be(200, logs.Body);
            logs.Json.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()).Should().Contain(t => t!.Contains("qm-live-hello", StringComparison.Ordinal));

            (await admin.PostAsync($"/containers/{name}/actions/remove", new { })).Status.Should().Be(409, "name confirmation required");
            var removed = await admin.PostAsync($"/containers/{name}/actions/remove", new { confirm = name });
            removed.Status.Should().Be(200, removed.Body);
            Run("docker", "inspect", name).Code.Should().NotBe(0, "the container is gone");
        }
        finally
        {
            Run("docker", "rm", "-f", name);
        }
    }
}

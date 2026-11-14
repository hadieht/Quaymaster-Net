using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Configuration;

namespace Quaymaster.Infrastructure.Compose;

/// <summary>
/// The single place that spawns the Docker CLI. Rules: an argument list (no shell, ever); the argv was built by
/// <c>ComposeArgv</c> and vetted by <c>MutationGateway</c>; a minimal, documented child environment (PATH, HOME, DOCKER_HOST,
/// LANG, DOCKER_CONFIG) - nothing else is inherited.
/// </summary>
internal sealed class ComposeCli(QmConfig config) : IComposeCli
{
    private const int MaxCapture = 32 * 1024 * 1024;

    public async Task<ComposeRunResult> RunAsync(IReadOnlyList<string> argv, ComposeRunOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(options);
        var info = new ProcessStartInfo(config.DockerBin)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in argv)
        {
            info.ArgumentList.Add(a);
        }

        info.Environment.Clear();
        foreach (var (k, v) in ChildEnvironment(config))
        {
            info.Environment[k] = v;
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new ComposeRunResult(127, string.Empty, $"cannot run {config.DockerBin}: {e.Message}", false);
        }

        process.StandardInput.Close();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout ?? TimeSpan.FromMinutes(10));
        var outTask = PumpAsync(process.StandardOutput, stdout, options.OnOutput, timeout.Token);
        var errTask = PumpAsync(process.StandardError, stderr, options.OnOutput, timeout.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            KillTree(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }
        }

        try
        {
            await Task.WhenAll(outTask, errTask);
        }
        catch (OperationCanceledException)
        {
            // pumps stop with the process
        }

        return new ComposeRunResult(timedOut ? 124 : process.ExitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }

    /// <summary>Environment given to every child process.</summary>
    internal static Dictionary<string, string> ChildEnvironment(QmConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/root",
            ["DOCKER_HOST"] = config.DockerSocket.StartsWith("tcp://", StringComparison.Ordinal) ? config.DockerSocket : $"unix://{config.DockerSocket}",
            ["LANG"] = "C.UTF-8",
        };
        if (Environment.GetEnvironmentVariable("DOCKER_CONFIG") is { Length: > 0 } dc)
        {
            env["DOCKER_CONFIG"] = dc;
        }

        return env;
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, Action<string>? onOutput, CancellationToken ct)
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var chunk = new string(buffer, 0, n);
            if (sink.Length < MaxCapture)
            {
                sink.Append(chunk);
            }

            onOutput?.Invoke(chunk);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // already gone
        }
    }
}

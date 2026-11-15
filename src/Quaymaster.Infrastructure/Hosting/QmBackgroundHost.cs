using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Background;
using Quaymaster.Application.Configuration;

namespace Quaymaster.Infrastructure.Hosting;

/// <summary>
/// Runs every registered <see cref="IQMBackgroundTask"/> concurrently with failure isolation. Does nothing when
/// <c>QM_DISABLE_BACKGROUND=true</c>.
/// </summary>
internal sealed class QmBackgroundHost(
    IEnumerable<IQMBackgroundTask> tasks,
    QmConfig config,
    ILogger<QmBackgroundHost> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config.DisableBackground)
        {
            Log.BackgroundDisabled(log);
            return;
        }

        // Let the host finish starting (StartupInitializer runs first) before the tasks touch the database.
        await Task.Yield();
        await Task.WhenAll(tasks.Select(t => RunIsolatedAsync(t, stoppingToken)));
    }

    /// <summary>First wait before a crashed task is started again; doubles up to <see cref="MaxRestartDelay"/> (F-08).</summary>
    internal static TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromMinutes(5);

    private async Task RunIsolatedAsync(IQMBackgroundTask task, CancellationToken ct)
    {
        var delay = RestartDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log.BackgroundTaskStarted(log, task.Name);
                await task.RunAsync(ct);
                return; // a task that returns normally is finished
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutting down
            }
#pragma warning disable CA1031 // a crashing task must never take the process down
            catch (Exception e)
#pragma warning restore CA1031
            {
                Log.BackgroundTaskCrashed(log, e, task.Name);
            }

            // F-08: a crash used to end the task until the next start (silently stopping schedules, approval expiry, disk alerts)
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRestartDelay.Ticks));
        }
    }
}

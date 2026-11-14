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

    private async Task RunIsolatedAsync(IQMBackgroundTask task, CancellationToken ct)
    {
        try
        {
            Log.BackgroundTaskStarted(log, task.Name);
            await task.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
#pragma warning disable CA1031 // a crashing task must never take the process down
        catch (Exception e)
#pragma warning restore CA1031
        {
            Log.BackgroundTaskCrashed(log, e, task.Name);
        }
    }
}

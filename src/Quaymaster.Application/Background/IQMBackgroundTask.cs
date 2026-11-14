namespace Quaymaster.Application.Background;

/// <summary>
/// A long-running background job (event watcher, stats sampler, housekeeping, schedulers ...).
/// Register implementations with <c>services.AddSingleton&lt;IQMBackgroundTask, T&gt;()</c>; the host runs each one
/// concurrently, isolates failures (a crashing task is logged and never takes the process down) and skips all of
/// them when <c>QM_DISABLE_BACKGROUND=true</c>.
/// </summary>
public interface IQMBackgroundTask
{
    /// <summary>Stable name used in logs.</summary>
    string Name { get; }

    /// <summary>Runs until <paramref name="stoppingToken"/> is cancelled. Own your loop and your delays.</summary>
    Task RunAsync(CancellationToken stoppingToken);
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Background;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Safety;
using Quaymaster.Infrastructure.Streaming;

namespace Quaymaster.Infrastructure.Hosting;

/// <summary>
/// Runs after the database is ready: operations a crash left <c>running</c> are marked interrupted (never resumed), the
/// ignore list is loaded, and the Docker bootstrap (version, self-detection) is attempted without blocking start-up - a
/// daemon that is down must not stop Quaymaster (reads then answer <c>docker_unavailable</c>). On stop, running operations
/// get a grace period and live streams are closed.
/// </summary>
internal sealed partial class DockerStartupService(
    IOperationManager operations,
    IIgnoreList ignore,
    DockerBootstrap bootstrap,
    SseHub hub,
    QmConfig config,
    ILogger<DockerStartupService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var interrupted = await operations.RecoverAsync(cancellationToken);
        if (interrupted > 0)
        {
            Log.Interrupted(log, interrupted);
        }

        await ignore.RefreshAsync(cancellationToken, force: true);
        if (!config.DisableBackground)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await bootstrap.EnsureReadyAsync(CancellationToken.None);
                }
                catch (AppException e)
                {
                    Log.DockerNotReady(log, e.Message);
                }
            }, CancellationToken.None);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        hub.CloseAll("server stopping");
        await operations.DrainAsync(TimeSpan.FromSeconds(10));
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 150, Level = LogLevel.Warning, Message = "{Count} operation(s) left running by a previous crash were marked failed (interrupted)")]
        public static partial void Interrupted(ILogger logger, int count);

        [LoggerMessage(EventId = 151, Level = LogLevel.Warning, Message = "Docker is not available yet ({Reason}); requests answer docker_unavailable until it is")]
        public static partial void DockerNotReady(ILogger logger, string reason);
    }
}

/// <summary>Retention for the event history (QM_EVENT_RETENTION_DAYS) and stats roll-ups (QM_STATS_RETENTION_HOURS).</summary>
internal sealed class RetentionTask(IEventStore events, IStatsStore stats, QmConfig config, TimeProvider time) : IQMBackgroundTask
{
    public string Name => "retention";

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), time);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        _ = await events.PruneAsync(now.AddDays(-config.EventRetentionDays).ToUnixTimeMilliseconds(), ct);
        _ = await stats.PruneAsync(now.AddHours(-config.Stats.RetentionHours).ToUnixTimeMilliseconds() / 60_000, ct);
    }
}

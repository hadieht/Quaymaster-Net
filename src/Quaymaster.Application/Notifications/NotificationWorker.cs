using Quaymaster.Application.Background;

namespace Quaymaster.Application.Notifications;

/// <summary>Primes the scrubber with channel secrets, then retries due failed deliveries every 15 s and prunes the old log daily.</summary>
public sealed class NotificationWorker(INotificationService service, TimeProvider time) : IQMBackgroundTask
{
    public string Name => "notifications";

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await service.PrimeScrubberAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        var lastPrune = DateTimeOffset.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await service.ProcessRetriesAsync(stoppingToken);
                if (time.GetUtcNow() - lastPrune > TimeSpan.FromDays(1))
                {
                    lastPrune = time.GetUtcNow();
                    await service.PruneAsync(stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // a failing retry round must not stop the loop; deliveries keep their state
            }
        }
    }
}

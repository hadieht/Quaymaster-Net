using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Background;
using Quaymaster.Infrastructure.Persistence;

namespace Quaymaster.Infrastructure.Hosting;

/// <summary>
/// Periodic housekeeping: expired sessions (in addition to cleanup-on-access), audit retention
/// (QM_AUDIT_RETENTION_DAYS) and the WAL checkpoint (the original ran it every 10 minutes).
/// </summary>
internal sealed class HousekeepingTask(
    ISessionService sessions,
    IAuditLog audit,
    IDbContextFactory<AppDbContext> factory,
    TimeProvider time,
    ILogger<HousekeepingTask> log) : IQMBackgroundTask
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    public string Name => "housekeeping";

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await sessions.PurgeExpiredAsync(ct);
        var pruned = await audit.PruneAsync(ct);
        if (pruned > 0)
        {
            Log.AuditPruned(log, pruned);
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", ct);
    }
}

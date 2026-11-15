using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Background;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Storage;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;

namespace Quaymaster.Application.Monitoring;

/// <summary>One evaluation pass of the maintenance-side monitors; the background task calls it every minute.</summary>
public interface IMonitorEvaluator
{
    Task TickAsync(CancellationToken ct);
}

/// <summary>
/// The maintenance half of the monitor loop (T4c). Per tick: expire overdue approvals (<c>run.skipped</c>), fire due schedules (RT-09:
/// at-most-once via the cursor, missed/out-of-window occurrences are skipped), announce upcoming maintenance
/// (<c>maintenance.upcoming</c>), announce available updates once a day (<c>updates.available</c>) and watch the free-space floor
/// (<c>host.disk_low</c> at <c>QM_DISK_FLOOR_BYTES</c>). Container/host monitors stay in <see cref="Monitors"/> (event driven).
/// Everything is emitted through <see cref="INotificationSink"/>, which owns de-duplication and cool-down; this class keeps no
/// delivery state of its own except the daily update-check marker (kv table).
/// </summary>
public sealed partial class MaintenanceMonitor(
    IScheduleStore schedules,
    IWindowStore windows,
    IRunStore runStore,
    IRunService runs,
    IUpdateService updates,
    IImageCleanupService cleanup,
    IMonitorSettingsService settings,
    ISystemService system,
    INotificationSink sink,
    IKvStore kv,
    QmConfig config,
    TimeProvider time,
    ILogger<MaintenanceMonitor> log) : IMonitorEvaluator, IQMBackgroundTask
{
    public const string UpdateCheckKey = "monitor.updates.lastCheck";

    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    public string Name => "maintenance-monitor";

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60), time);
        try
        {
            do
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    // F-08: nothing a single tick throws (a registry 429, a timeout ...) may end the monitor for good
                    Log.TickFailed(log, e.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await Guarded("approvals", () => runs.ExpireApprovalsAsync(ct));
        await Guarded("schedules", () => FireDueSchedulesAsync(ct));
        var s = await settings.GetAsync(ct);
        if (!s.Enabled)
        {
            return;
        }

        await Guarded("upcoming", () => AnnounceUpcomingAsync(s.UpcomingMaintenanceMinutes, ct));
        await Guarded("updates", () => DailyUpdateCheckAsync(ct));
        await Guarded("disk", () => CheckDiskFloorAsync(ct));
    }

    private async Task Guarded(string part, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception e) when (e is not OperationCanceledException || e is TaskCanceledException)
        {
            Log.PartFailed(log, part, e.Message);
        }
    }

    // ---- scheduler -------------------------------------------------------------------------------------------------------------

    public async Task FireDueSchedulesAsync(CancellationToken ct)
    {
        var now = Now();
        foreach (var sc in await schedules.ListAsync(ct))
        {
            if (!sc.Enabled)
            {
                await schedules.SetCursorAsync(sc.Id, now, ct);
                continue;
            }

            var due = TimeMath.NextOccurrences(sc.Cron, sc.Timezone, sc.CursorUtc, 500, now);
            if (due.Count == 0)
            {
                await schedules.SetCursorAsync(sc.Id, now, ct);
                continue;
            }

            var isUpdate = sc.JobType == "update";
            var window = sc.WindowId is { } wid ? await windows.GetAsync(wid, ct) : null;
            var inWindow = sc.WindowId is null || (window is { Enabled: true } && TimeMath.WindowAt(window.Def, now) is not null);
            var actions = ScheduleTick.Decide(due, now, isUpdate, sc.WindowId is not null, inWindow);

            // at-most-once: the cursor moves BEFORE anything fires, so a crash can never fire an occurrence twice (RT-09)
            await schedules.SetCursorAsync(sc.Id, due.Count == 500 ? due[^1] : now, ct);
            for (var i = 0; i < due.Count; i++)
            {
                if (actions[i] != OccurrenceAction.Fire)
                {
                    continue;
                }

                try
                {
                    await FireAsync(sc, due[i], ct);
                }
                catch (Exception e) when (e is not AppException && !ct.IsCancellationRequested)
                {
                    Log.ScheduleFailed(log, sc.Name, e.Message); // F-08: one bad occurrence never stops the rest
                }
                catch (AppException e)
                {
                    Log.ScheduleFailed(log, sc.Name, e.Message);
                    if (isUpdate && e.Code == ErrorCode.WindowClosed)
                    {
                        sink.Notify(new NotificationEvent(
                            "run.skipped", "info", $"Scheduled update skipped: {sc.Name}", e.Message, $"skipped:{sc.Id}:{due[i]}", null,
                            new Dictionary<string, object?> { ["schedule"] = sc.Name, ["reason"] = "window_closed" }));
                    }
                }
            }
        }
    }

    private async Task FireAsync(ScheduleRecord sc, long occurrence, CancellationToken ct)
    {
        var actor = new Actor($"schedule:{sc.Name}", null);
        switch (sc.JobType)
        {
            case "update":
                if (sc.Selector["project"]?.GetValue<string>() is { Length: > 0 } project)
                {
                    var services = (sc.Selector["services"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? string.Empty).ToList();
                    _ = await runs.CreateAsync(new CreateRunRequest(project, services, WindowId: sc.WindowId), actor, new ScheduledRun(sc.Id, sc.Name, sc.Mode, occurrence, sc.WindowId), ct);
                }

                break;
            case "image_check":
                {
                    var all = await updates.StatusesAsync(true, ct);
                    var avail = all.Where(x => x.State is "update_available" or "update_staged").ToList();
                    await RecordSimpleAsync("image_check", sc, occurrence, $"{avail.Count} update(s) available, {all.Count} image(s) checked", actor, ct);
                    if (avail.Count > 0)
                    {
                        AnnounceUpdates(avail);
                    }

                    break;
                }

            case "cleanup_report":
                {
                    var p = await cleanup.PreviewAsync(new CleanupPreviewRequest(null, null, null), actor, ct);
                    sink.Notify(new NotificationEvent(
                        "cleanup.report",
                        "info",
                        "Cleanup report",
                        $"{p.Candidates.Count} item(s) could be removed ({p.TotalBytes / 1_048_576} MiB). Nothing was deleted. Review in Quaymaster and run the cleanup yourself.",
                        $"cleanup:{DateTimeOffset.FromUnixTimeMilliseconds(occurrence):yyyy-MM-dd}",
                        null,
                        new Dictionary<string, object?> { ["candidates"] = p.Candidates.Count, ["totalBytes"] = p.TotalBytes, ["protected"] = p.Protected.Count }));
                    await RecordSimpleAsync("cleanup_report", sc, occurrence, $"{p.Candidates.Count} candidate(s), {p.Protected.Count} protected", actor, ct);
                    break;
                }

            default:
                // image_pull: scheduled pre-staging is not wired yet (see PROGRESS "gaps for T6"); the occurrence is consumed.
                Log.JobNotSupported(log, sc.JobType, sc.Name);
                break;
        }
    }

    private async Task RecordSimpleAsync(string kind, ScheduleRecord sc, long occurrence, string summary, Actor actor, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var now = Now();
        await runStore.InsertAsync(new NewRun(id, kind, "schedule", sc.Name, RunStatuses.Completed, null, null, actor.Name, [], now, sc.Id, occurrence), ct);
        await runStore.FinishAsync(id, RunStatuses.Completed, new RunResult([], summary), null, null, now, ct);
    }

    // ---- upcoming maintenance ----------------------------------------------------------------------------------------------------

    public async Task AnnounceUpcomingAsync(int minutes, CancellationToken ct)
    {
        var now = Now();
        var horizon = now + (minutes * 60_000L);
        foreach (var sc in (await schedules.ListAsync(ct)).Where(x => x.Enabled && x.JobType == "update"))
        {
            var next = TimeMath.NextOccurrences(sc.Cron, sc.Timezone, now, 1, horizon).FirstOrDefault(-1);
            if (next < 0)
            {
                continue;
            }

            var what = sc.Selector["project"]?.GetValue<string>() ?? sc.Selector["container"]?.GetValue<string>() ?? "the host";
            sink.Notify(new NotificationEvent(
                "maintenance.upcoming",
                "info",
                $"Maintenance starts soon: {sc.Name}",
                $"Scheduled {sc.JobType} ({sc.Mode}) for {what} at {ImageReferences.Iso(next)}.",
                $"upcoming:{sc.Id}:{next}",
                24 * 3_600_000L,
                new Dictionary<string, object?> { ["schedule"] = sc.Name, ["scheduleId"] = sc.Id, ["mode"] = sc.Mode, ["target"] = what, ["at"] = ImageReferences.Iso(next) }));
        }
    }

    // ---- updates available -----------------------------------------------------------------------------------------------------

    public async Task DailyUpdateCheckAsync(CancellationToken ct)
    {
        var now = Now();
        var last = long.TryParse(await kv.GetAsync(UpdateCheckKey, ct), out var l) ? l : 0;
        if (now - last < (long)UpdateCheckInterval.TotalMilliseconds)
        {
            return;
        }

        await kv.SetAsync(UpdateCheckKey, now.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        var avail = (await updates.StatusesAsync(false, ct)).Where(a => a.State is "update_available" or "update_staged").ToList();
        if (avail.Count > 0)
        {
            AnnounceUpdates(avail);
        }
    }

    public void AnnounceUpdates(IReadOnlyList<ImageUpdateStatusDto> avail)
    {
        ArgumentNullException.ThrowIfNull(avail);
        var key = string.Join('|', avail.Select(a => $"{a.Ref}@{a.RemoteDigest}").Order(StringComparer.Ordinal));
        sink.Notify(new NotificationEvent(
            "updates.available",
            "info",
            $"{avail.Count} image update(s) available",
            string.Join('\n', avail.Take(15).Select(a => $"{a.Ref} ({string.Join(", ", a.UsedBy.Select(u => u.ContainerName))}){(a.State == "update_staged" ? " [staged]" : string.Empty)}")),
            "updates:" + (key.Length > 300 ? key[..300] : key),
            24 * 3_600_000L,
            new Dictionary<string, object?> { ["count"] = avail.Count, ["images"] = avail.Select(a => a.Ref).ToList() }));
    }

    // ---- disk floor -------------------------------------------------------------------------------------------------------------

    public async Task CheckDiskFloorAsync(CancellationToken ct)
    {
        var host = await system.HostAsync(ct);
        if (host.DockerRoot is { Total: > 0 } root && root.Free < config.DiskFloorBytes)
        {
            sink.Notify(new NotificationEvent(
                "host.disk_low",
                "critical",
                "Free space below QM_DISK_FLOOR_BYTES on the Docker root",
                $"{root.Free / Math.Pow(1024, 3):F1} GiB free; updates that need to pull images are refused below {config.DiskFloorBytes / Math.Pow(1024, 3):F1} GiB.",
                "disk-low",
                6 * 3_600_000L,
                new Dictionary<string, object?> { ["freeBytes"] = root.Free, ["floorBytes"] = config.DiskFloorBytes }));
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 150, Level = LogLevel.Error, Message = "Maintenance monitor tick failed: {Reason}")]
        public static partial void TickFailed(ILogger logger, string reason);

        [LoggerMessage(EventId = 151, Level = LogLevel.Warning, Message = "Maintenance monitor part {Part} failed: {Reason}")]
        public static partial void PartFailed(ILogger logger, string part, string reason);

        [LoggerMessage(EventId = 152, Level = LogLevel.Warning, Message = "Scheduled job {Schedule} failed to start: {Reason}")]
        public static partial void ScheduleFailed(ILogger logger, string schedule, string reason);

        [LoggerMessage(EventId = 153, Level = LogLevel.Information, Message = "Scheduled job type {JobType} of {Schedule} is not executed by this build")]
        public static partial void JobNotSupported(ILogger logger, string jobType, string schedule);
    }
}

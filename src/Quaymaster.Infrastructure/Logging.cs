using Microsoft.Extensions.Logging;

namespace Quaymaster.Infrastructure;

/// <summary>Source-generated log messages (CA1848): one place for every Infrastructure log line.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "First-run state: {Outcome}")]
    public static partial void FirstRunState(ILogger logger, Quaymaster.Application.Auth.SetupOutcome outcome);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Background tasks are disabled (QM_DISABLE_BACKGROUND=true)")]
    public static partial void BackgroundDisabled(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Background task {Task} started")]
    public static partial void BackgroundTaskStarted(ILogger logger, string task);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Background task {Task} crashed; it it will be restarted with back-off")]
    public static partial void BackgroundTaskCrashed(ILogger logger, Exception exception, string task);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Pruned {Count} audit rows past retention")]
    public static partial void AuditPruned(ILogger logger, int count);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Database backed up to {Backup} before migrating")]
    public static partial void DatabaseBackedUp(ILogger logger, string backup);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Applied {Count} database migration(s), latest {Latest}")]
    public static partial void MigrationsApplied(ILogger logger, int count, string latest);
}

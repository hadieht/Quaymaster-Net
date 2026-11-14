using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;

namespace Quaymaster.Infrastructure.Hosting;

/// <summary>
/// Runs before the server accepts requests: database first, then the first-run lifecycle (env admin or setup token).
/// A failure aborts startup (fail fast).
/// </summary>
public sealed class StartupInitializer(
    IDatabaseInitializer database,
    ISetupService setup,
    ILogger<StartupInitializer> log)
{
    private static readonly Action<ILogger, string, Exception?> LogDbInitStart =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "DatabaseInitStart"), "Database initialization started: {Message}");
    private static readonly Action<ILogger, string, Exception?> LogDbInitDone =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "DatabaseInitDone"), "Database initialization completed: {Message}");
    private static readonly Action<ILogger, SetupOutcome, Exception?> LogSetupOutcome =
        LoggerMessage.Define<SetupOutcome>(LogLevel.Information, new EventId(3, "SetupOutcome"), "Setup completed with outcome: {Outcome}");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LogDbInitStart(log, "Starting database initialization", null);
        await database.InitializeAsync(cancellationToken);
        LogDbInitDone(log, "Database initialized, starting setup", null);
        var outcome = await setup.InitializeAsync(cancellationToken);
        LogSetupOutcome(log, outcome, null);
        Log.FirstRunState(log, outcome);
    }
}

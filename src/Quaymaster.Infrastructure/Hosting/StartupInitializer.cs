using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;

namespace Quaymaster.Infrastructure.Hosting;

/// <summary>
/// Runs before the server accepts requests: database first, then the first-run lifecycle (env admin or setup token).
/// A failure aborts startup (fail fast).
/// </summary>
internal sealed class StartupInitializer(
    IDatabaseInitializer database,
    ISetupService setup,
    ILogger<StartupInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken);
        var outcome = await setup.InitializeAsync(cancellationToken);
        Log.FirstRunState(log, outcome);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Audit;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Background;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Security;
using Quaymaster.Application.Users;
using Quaymaster.Infrastructure.Files;
using Quaymaster.Infrastructure.Hosting;
using Quaymaster.Infrastructure.Persistence;
using Quaymaster.Infrastructure.Persistence.Repositories;
using Quaymaster.Infrastructure.Security;

namespace Quaymaster.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence, security adapters, the Application services and the hosted startup / background
    /// machinery. Everything is a singleton: repositories open a short-lived <c>AppDbContext</c> per operation from
    /// the pooled factory, so no service captures request-scoped state.
    /// </summary>
    public static IServiceCollection AddQuaymasterInfrastructure(this IServiceCollection services, QmConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(config);
        services.AddSingleton(config.Session);
        services.AddSingleton(new SetupOptions(config.Admin));

        // persistence
        services.AddSingleton(_ => InstanceLock.Acquire(config.DataDir));
        services.AddDbContextFactory<AppDbContext>(o => o
            .UseSqlite($"Data Source={config.DatabaseFile};Mode=ReadWriteCreate;Pooling=True;Default Timeout=5")
            .AddInterceptors(new SqlitePragmaInterceptor()));
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<ISessionRepository, SessionRepository>();
        services.AddSingleton<IAuditRepository, AuditRepository>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();

        // adapters
        services.AddSingleton<IPasswordHasher>(new ScryptPasswordHasher(config.ScryptLog2N));
        services.AddSingleton<IFileReader, FileReader>();
        services.AddSingleton<ISetupTokenStore>(new SetupTokenFile(config.DataDir));
        services.TryAddSingleton<ISetupAnnouncer, ConsoleSetupAnnouncer>();

        // application services
        services.AddSingleton<ILoginThrottle, LoginLimiter>();
        services.AddSingleton<ISessionRevocationBus, SessionRevocationBus>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<ISetupService, SetupService>();
        services.AddSingleton<IAuditLog>(sp => new AuditLog(
            sp.GetRequiredService<IAuditRepository>(),
            sp.GetRequiredService<TimeProvider>(),
            config.AuditRetentionDays,
            sp.GetRequiredService<Quaymaster.Domain.Redaction.Scrubber>()));

        // hosted machinery: StartupInitializer MUST be registered before the background host
        services.AddSingleton<StartupInitializer>(sp =>
        {
            _ = sp.GetRequiredService<InstanceLock>(); // fail fast on a second instance before touching the database
            return ActivatorUtilities.CreateInstance<StartupInitializer>(sp);
        });
        services.AddSingleton<IQMBackgroundTask, HousekeepingTask>();
        services.AddQuaymasterDocker(config);
        services.AddHostedService<QmBackgroundHost>();
        return services;
    }
}

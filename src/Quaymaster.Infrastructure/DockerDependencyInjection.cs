using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Background;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Events;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Notifications;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Registry;
using Quaymaster.Application.Storage;
using Quaymaster.Application.Updates;
using Quaymaster.Application.Safety;
using Quaymaster.Application.Stats;
using Quaymaster.Application.Streaming;
using Quaymaster.Domain.Redaction;
using Quaymaster.Domain.Safety;
using Quaymaster.Infrastructure.Compose;
using Quaymaster.Infrastructure.Docker;
using Quaymaster.Infrastructure.Hosting;
using Quaymaster.Infrastructure.Host;
using Quaymaster.Infrastructure.Persistence.Repositories;
using Quaymaster.Infrastructure.Streaming;

namespace Quaymaster.Infrastructure;

public static class DockerDependencyInjection
{
    /// <summary>
    /// Docker core: engine adapter, the mutation gateway, services, background collectors and the SSE hub. Everything the
    /// tests need to fake sits behind an interface registered with <c>TryAdd</c> semantics where a seam is intended
    /// (<see cref="IDockerEngine"/>, <see cref="IDockerCommands"/>, <see cref="IComposeCli"/>, <see cref="IStackFiles"/>,
    /// <see cref="ISelfProbe"/>, <see cref="IHostProbe"/>).
    /// </summary>
    public static IServiceCollection AddQuaymasterDocker(this IServiceCollection services, QmConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddSingleton<Scrubber>();
        services.AddSingleton(new SafetyPolicy(new SafetyOptions(config.ReadOnly, config.ManagedScope, config.VisibleScope, config.ProtectedNames)));
        services.AddSingleton<Broadcaster<DockerEventRecord>>();
        services.AddSingleton<Broadcaster<StatsSampleEvent>>();

        // adapters (fakeable seams)
        services.AddSingleton<DockerEngineAdapter>();
        services.AddSingleton<IDockerEngine>(sp => sp.GetRequiredService<DockerEngineAdapter>());
        services.AddSingleton<IDockerCommands>(sp => sp.GetRequiredService<DockerEngineAdapter>());
        services.AddSingleton<IComposeCli, ComposeCli>();
        services.AddSingleton<ISelfProbe, SelfProbe>();
        services.AddSingleton<IHostProbe>(_ => new HostProbe());
        services.AddSingleton<DockerBootstrap>();
        services.AddSingleton<IStackFiles>(sp =>
        {
            var bootstrap = sp.GetRequiredService<DockerBootstrap>();
            return new StackFiles(config, () => bootstrap.DockerRoot, () => bootstrap.MountRoots);
        });

        // persistence
        services.AddSingleton<IEventStore, EventStore>();
        services.AddSingleton<IStatsStore, StatsStore>();
        services.AddSingleton<IOperationStore, OperationStore>();
        services.AddSingleton<IKvStore, KvStore>();
        services.AddSingleton<IIgnoreStore, IgnoreStore>();
        services.AddSingleton<IAuditReader, AuditReader>();
        services.AddSingleton<IRunEnvironment, RunEnvironment>();

        // application services
        services.AddSingleton<IMutationGateway, MutationGateway>();
        services.AddSingleton<IIgnoreList, IgnoreList>();
        services.AddSingleton<IOperationManager, OperationManager>();
        services.AddSingleton<IIgnoreService, IgnoreService>();
        services.AddSingleton<Func<IRecreator>>(sp => () => sp.GetRequiredService<IRecreator>());
        services.AddSingleton<IContainerService, ContainerService>();
        services.AddSingleton<ComposeService>();
        services.AddSingleton<IComposeService>(sp => sp.GetRequiredService<ComposeService>());
        services.AddSingleton<IRecreator>(sp => sp.GetRequiredService<ComposeService>());
        services.AddSingleton<IContainerQueries, ContainerQueries>();
        services.AddSingleton<IDiskUsageService, DiskUsageService>();
        services.AddSingleton<IInventoryService, InventoryService>();
        services.AddSingleton<IEventQueries, EventQueries>();
        services.AddSingleton<IEventStreamStatus>(sp => sp.GetRequiredService<EventWatcher>());
        services.AddSingleton<ISystemService, SystemService>();
        // notifications (T5): the sink the monitors / maintenance engine emit into is the real dispatcher
        services.AddSingleton<INotificationStore, Quaymaster.Infrastructure.Persistence.Repositories.NotificationStore>();
        services.AddSingleton<INotificationTransport, Quaymaster.Infrastructure.Notifications.OutboundHttpTransport>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<INotificationSink>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<IQMBackgroundTask, NotificationWorker>();

        // updates, image / volume / network removal, storage cleanup, registry credentials (T3)
        services.AddSingleton<ISecretBox>(_ => new Quaymaster.Infrastructure.Security.SecretBox(config.SecretKey.Bytes));
        services.AddSingleton<IImageChecksStore, ImageChecksStore>();
        services.AddSingleton<IStagedStore, StagedStore>();
        services.AddSingleton<IRollbackStore, RollbackStore>();
        services.AddSingleton<IQuarantineStore, QuarantineStore>();
        services.AddSingleton<IRegistryCredentialStore, RegistryCredentialStore>();
        services.AddSingleton<IRegistryClient>(sp => Quaymaster.Infrastructure.Registry.RegistryClientFactory.Create(config, sp.GetRequiredService<IRegistryCredentialStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IQuarantineService, QuarantineService>();
        services.AddSingleton<IImageOps, ImageOps>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IPullService, PullService>();
        services.AddSingleton<IRegistryCredentialService, RegistryCredentialService>();
        services.AddSingleton<IDfMeasurer, DfMeasurer>();
        services.AddSingleton<IReclaimMeasurer, ReclaimMeasurer>();
        services.AddSingleton<IComposeOwnershipProvider, ComposeOwnershipProvider>();
        services.AddSingleton<IImageCleanupService, ImageCleanupService>();
        services.AddSingleton<IStorageCleanupService, StorageCleanupService>();

        // maintenance configuration + dry-run planner (T4a)
        services.AddSingleton<IPolicyStore, PolicyStore>();
        services.AddSingleton<IWindowStore, WindowStore>();
        services.AddSingleton<IScheduleStore, ScheduleStore>();
        services.AddSingleton<IRunStore, RunStore>();
        services.AddSingleton<IProbeVerifier, HttpProbeVerifier>();
        services.AddSingleton<PolicyService>();
        services.AddSingleton<IPolicyResolver>(sp => sp.GetRequiredService<PolicyService>());
        services.AddSingleton<WindowService>();
        services.AddSingleton<ScheduleService>();
        services.AddSingleton<IPlanService, PlannerService>();

        // run lifecycle: executor, approval, rollback, restore, crash recovery (T4b)
        services.AddSingleton<IMaintenanceGate, MaintenanceGate>();
        services.AddSingleton<IRunEnvironment, RunEnvironment>();
        services.AddSingleton<IHttpProbe, HttpStatusProbe>();
        services.AddSingleton(new HealthTimings());
        services.AddSingleton<HealthGate>();
        services.AddSingleton<RunHooks>();
        services.AddSingleton<IRunExecutor, RunExecutor>();
        services.AddSingleton<IRunService, RunService>();
        services.AddSingleton<IRunRecoveryService, RunRecoveryService>();
        services.AddSingleton<IStandaloneService, StandaloneExecutor>();
        services.AddSingleton<Application.Streaming.IMaintenanceStreamFeed, Application.Streaming.MaintenanceStreamFeed>();

        // background collectors
        services.AddSingleton<EventWatcher>();
        services.AddSingleton<IQMBackgroundTask>(sp => sp.GetRequiredService<EventWatcher>());
        services.AddSingleton<StatsSampler>();
        services.AddSingleton<IStatsSampler>(sp => sp.GetRequiredService<StatsSampler>());
        services.AddSingleton<IQMBackgroundTask>(sp => sp.GetRequiredService<StatsSampler>());
        services.AddSingleton<Monitors>();
        services.AddSingleton<MaintenanceMonitor>();
        services.AddSingleton<IMonitorEvaluator>(sp => sp.GetRequiredService<MaintenanceMonitor>());
        services.AddSingleton<IQMBackgroundTask>(sp => sp.GetRequiredService<MaintenanceMonitor>());
        services.AddSingleton<IMonitorSettingsService>(sp => sp.GetRequiredService<Monitors>());
        services.AddSingleton<IQMBackgroundTask>(sp => sp.GetRequiredService<Monitors>());
        services.AddSingleton<IQMBackgroundTask, RetentionTask>();

        // SSE
        services.AddSingleton(config.Sse);
        services.AddSingleton<SseHub>();
        services.AddSingleton<ISseHub>(sp => sp.GetRequiredService<SseHub>());
        services.AddSingleton<IStreamCounter>(sp => sp.GetRequiredService<SseHub>());

        // after StartupInitializer (database first)
        services.AddHostedService<DockerStartupService>();

        return services;
    }
}

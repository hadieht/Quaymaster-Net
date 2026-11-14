using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Configuration;
using Quaymaster.Infrastructure;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddLogging(l => l.AddConsole().SetMinimumLevel(LogLevel.Debug));

var config = new QmConfig(
    Host: "127.0.0.1",
    Port: 8791,
    DataDir: "/tmp/qm-e2e-test",
    SecretKey: new SecretKey(Convert.FromBase64String("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")),
    CookieSecure: false,
    PublicOrigins: [],
    TrustProxy: [],
    ReadOnly: false,
    ManagedScope: ["*"],
    VisibleScope: ["*"],
    ProtectedNames: [],
    StackPaths: ["/root/projects/quaymaster-net/test-fixtures/stacks"],
    StackAutoMounts: true,
    HelperEnabled: true,
    HelperImage: null,
    HelperLabels: new System.Collections.Generic.Dictionary<string, string>(),
    LogLevel: "debug",
    Session: new SessionConfig(30, 12),
    SseMaxPerSession: 8,
    SseMaxTotal: 32,
    LogFollowBytesPerSec: 524288,
    StatsIntervalSeconds: 10,
    StatsRetentionHours: 48,
    EventRetentionDays: 14,
    AuditRetentionDays: 365,
    DiskFloorBytes: 5368709120,
    DfMinIntervalSeconds: 21600,
    ScryptLog2N: 14,
    DockerBin: "docker",
    WebDir: "/root/projects/quaymaster-net/web/dist",
    DisableBackground: false,
    RegistryInsecure: ["127.0.0.1:5055"],
    RegistryTokenRealms: [],
    CheckTtlMinutes: 360,
    RollbackRetentionDays: 7,
    CleanupMinAgeHours: 24,
    ApprovalExpiryHours: 72,
    Admin: null,
    SelfContainer: null,
    DockerSocket: "/var/run/docker.sock",
    DockerRootMount: "/",
    RedactAllEnv: false
);

builder.Services.AddSingleton(config);
builder.Services.AddQuaymasterInfrastructure(config);

var host = builder.Build();
Console.WriteLine("Starting host...");
await host.StartAsync();
Console.WriteLine("Host started");
await host.StopAsync();
Console.WriteLine("Host stopped");
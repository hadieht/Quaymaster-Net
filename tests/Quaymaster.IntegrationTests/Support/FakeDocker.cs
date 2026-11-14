using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Stats;

namespace Quaymaster.IntegrationTests.Support;

/// <summary>
/// One in-memory Docker world behind every seam (engine reads, lifecycle commands, the Compose CLI, stack files, self probe,
/// host probe). Integration tests drive the real HTTP pipeline against it; nothing touches a daemon.
/// </summary>
public sealed partial class FakeDocker : IDockerEngine, IDockerCommands, IComposeCli, IStackFiles, ISelfProbe, IHostProbe
{
    public const string WebId = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
    public const string DbId = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
    public const string SoloId = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
    private const string ContainersKey = "Containers";
    public const string Secret = "hunter2-very-secret";

    public List<ContainerCommand> Commands { get; } = [];

    public List<string[]> ComposeRuns { get; } = [];

    public int DfCalls => _dfCalls;

    private int _dfCalls;

    public bool DaemonDown { get; set; }

    public Func<IReadOnlyList<string>, ComposeRunResult>? ComposeHandler { get; set; }

    public List<ScannedProject> Scanned { get; } = [];

    public List<string> LogLines { get; } = ["2026-11-14T10:00:00.000000000Z first line", "2026-11-14T10:00:01.000000000Z second line"];

    public JsonArray Containers { get; } =
    [
        Summary(WebId, "web", "nginx:1.27", "shop", "web", "running", "Up 2 hours"),
        Summary(DbId, "db", "postgres:17", "shop", "db", "running", "Up 2 hours (healthy)"),
        Summary(SoloId, "solo", "alpine:3", null, null, "exited", "Exited (0) 3 hours ago"),
    ];

    public static JsonObject Summary(string id, string name, string image, string? project, string? service, string state, string status)
    {
        var labels = new JsonObject { ["maintainer"] = "me" };
        if (project is not null)
        {
            labels["com.docker.compose.project"] = project;
            labels["com.docker.compose.service"] = service;
            labels["com.docker.compose.project.working_dir"] = "/srv/" + project;
            labels["com.docker.compose.project.config_files"] = "/srv/" + project + "/compose.yaml";
            labels["com.docker.compose.config-hash"] = "hash-" + name;
        }

        return new JsonObject
        {
            ["Id"] = id,
            ["Names"] = new JsonArray("/" + name),
            ["Image"] = image,
            ["ImageID"] = "sha256:" + new string('e', 64),
            ["State"] = state,
            ["Status"] = status,
            ["Created"] = 1_700_000_000,
            ["Ports"] = new JsonArray(),
            ["Labels"] = labels,
            ["Mounts"] = new JsonArray(),
            ["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject { ["shop_default"] = new JsonObject() } },
        };
    }

    private static JsonObject Inspect(JsonObject summary)
    {
        var labels = (JsonObject)summary["Labels"]!.DeepClone();
        return new JsonObject
        {
            ["Id"] = summary["Id"]!.GetValue<string>(),
            ["Name"] = summary["Names"]![0]!.GetValue<string>(),
            ["Image"] = summary["ImageID"]!.GetValue<string>(),
            ["RestartCount"] = 2,
            ["State"] = new JsonObject { ["Status"] = summary["State"]!.GetValue<string>(), ["Running"] = summary["State"]!.GetValue<string>() == "running", ["StartedAt"] = "2026-11-14T08:00:00Z" },
            ["Config"] = new JsonObject
            {
                ["Image"] = summary["Image"]!.GetValue<string>(),
                ["Tty"] = false,
                ["Env"] = new JsonArray("PATH=/usr/bin", "DB_PASSWORD=" + Secret, "LANG=C"),
                ["Labels"] = labels,
            },
            ["HostConfig"] = new JsonObject(),
            ["Mounts"] = new JsonArray(),
        };
    }

    private void Up()
    {
        if (DaemonDown)
        {
            throw new Quaymaster.Application.Errors.AppException(503, Quaymaster.Application.Errors.ErrorCode.DockerUnavailable, "Docker daemon unavailable");
        }
    }

    // ---- IDockerEngine ----------------------------------------------------------------------------------------------

    public Task<EngineVersion> GetVersionAsync(CancellationToken ct)
    {
        Up();
        return Task.FromResult(new EngineVersion("29.1.0", "1.52", "1.40", "1.52"));
    }

    public Task PingAsync(CancellationToken ct)
    {
        Up();
        return Task.CompletedTask;
    }

    public Task<JsonArray> ListContainersAsync(CancellationToken ct)
    {
        Up();
        return Task.FromResult((JsonArray)Containers.DeepClone());
    }

    public Task<JsonObject?> InspectContainerAsync(string idOrName, CancellationToken ct)
    {
        Up();
        var hit = Containers.OfType<JsonObject>().FirstOrDefault(c =>
            c["Id"]!.GetValue<string>() == idOrName
            || c["Id"]!.GetValue<string>().StartsWith(idOrName, StringComparison.Ordinal)
            || c["Names"]![0]!.GetValue<string>().TrimStart('/') == idOrName);
        return Task.FromResult(hit is null ? null : Inspect(hit));
    }

    public Task<JsonObject> InfoAsync(CancellationToken ct)
    {
        Up();
        return Task.FromResult(new JsonObject
        {
            ["ID"] = "ABCD",
            [ContainersKey] = 3,
            ["DockerRootDir"] = "/var/lib/docker",
            ["ServerVersion"] = "29.1.0",
            ["Name"] = "testhost",
        });
    }

    public async IAsyncEnumerable<LogFrame> LogsAsync(string containerId, bool tty, LogRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Up();
        foreach (var line in LogLines)
        {
            yield return new LogFrame(false, System.Text.Encoding.UTF8.GetBytes(line + "\n"));
        }

        if (request.Follow)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public Task<RawStats?> StatsOnceAsync(string containerId, CancellationToken ct) =>
        Task.FromResult<RawStats?>(new RawStats(DateTimeOffset.UtcNow, 1000, 2000, 4000, 100, 1, 2, 3, 4));

    public async IAsyncEnumerable<JsonObject> EventsAsync(DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            if (!_eventFeed.Reader.TryRead(out var next))
            {
                Volatile.Write(ref _eventsHandled, _eventsYielded);
                if (!await _eventFeed.Reader.WaitToReadAsync(ct))
                {
                    yield break;
                }

                continue;
            }

            _eventsYielded++;
            yield return next;
        }
    }

    private readonly System.Threading.Channels.Channel<JsonObject> _eventFeed = System.Threading.Channels.Channel.CreateUnbounded<JsonObject>();
    private long _eventsPushed;
    private long _eventsYielded;
    private long _eventsHandled;

    /// <summary>Queues a raw daemon event for the (real) EventWatcher to consume.</summary>
    public void PushEvent(JsonObject raw)
    {
        Interlocked.Increment(ref _eventsPushed);
        _eventFeed.Writer.TryWrite(raw);
    }

    /// <summary>Completes once the watcher has fully handled every pushed event.</summary>
    public async Task WaitEventsHandledAsync()
    {
        for (var i = 0; i < 500 && Volatile.Read(ref _eventsHandled) < Volatile.Read(ref _eventsPushed); i++)
        {
            await Task.Delay(10);
        }
    }

    // ---- IDockerCommands --------------------------------------------------------------------------------------------

    // ---- IComposeCli ------------------------------------------------------------------------------------------------

    public Task<ComposeRunResult> RunAsync(IReadOnlyList<string> argv, ComposeRunOptions options, CancellationToken ct)
    {
        lock (ComposeRuns)
        {
            ComposeRuns.Add([.. argv]);
        }

        var result = ComposeHandler?.Invoke(argv) ?? DefaultRun(argv);
        options.OnOutput?.Invoke("compose output line");
        return Task.FromResult(result);
    }

    public const string DefaultConfigJson =
        "{\"name\":\"shop\",\"services\":{\"web\":{\"image\":\"nginx:1.27\",\"depends_on\":{\"db\":{\"condition\":\"service_started\"}}},\"db\":{\"image\":\"postgres:17\"}}}";

    private static ComposeRunResult DefaultRun(IReadOnlyList<string> argv) =>
        new(0, argv.Contains("ls") ? "[]" : argv.Contains("config") ? DefaultConfigJson : string.Empty, string.Empty, false);

    // ---- IStackFiles ------------------------------------------------------------------------------------------------

    public IReadOnlyList<string> AllowedRoots { get; } = ["/srv"];

    public PathCheck Check(string path, bool requireReadable = false) =>
        path.StartsWith("/srv", StringComparison.Ordinal) ? PathCheck.Success(path) : PathCheck.Fail("outside_allowed_roots", "Path is outside the allowed stack roots");

    public IReadOnlyList<ScannedProject> Scan() => Scanned;

    public string RealPath(string path) => path;

    public string? ReadText(string file) => null;

    // ---- ISelfProbe / IHostProbe --------------------------------------------------------------------------------------

    public string? ReadFile(string path) => null;

    public string Hostname() => "testhost";

    public bool IsDirectory(string path) => false;

    public (double Idle, double Total) CpuTimes() => (100, 200);

    public long MemTotalBytes() => 8L * 1024 * 1024 * 1024;

    public long? MemAvailableBytes() => 4L * 1024 * 1024 * 1024;

    public double[] LoadAverage() => [0.5, 0.4, 0.3];

    public double UptimeSeconds() => 12345;


    /// <summary>Replaces every seam in the host with this fake.</summary>
    public void Install(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(this);
        services.AddSingleton<IDockerEngine>(this);
        services.AddSingleton<IDockerCommands>(this);
        services.AddSingleton<IComposeCli>(this);
        services.AddSingleton<IStackFiles>(this);
        services.AddSingleton<ISelfProbe>(this);
        services.AddSingleton<IHostProbe>(this);
    }
}

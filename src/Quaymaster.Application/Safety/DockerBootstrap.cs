using Microsoft.Extensions.Logging;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Safety;

/// <summary>
/// One-time, lazily retried initialisation that needs the daemon: negotiated version, self-detection (feeds the safety
/// policy), the Docker root directory and the same-path bind mounts that count as stack roots. A daemon that is down at
/// start-up does not stop Quaymaster: reads answer <c>docker_unavailable</c> and the next request retries.
/// </summary>
public sealed partial class DockerBootstrap(
    IDockerEngine engine,
    SafetyPolicy policy,
    ISelfProbe probe,
    QmConfig config,
    ILogger<DockerBootstrap> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;
    private EngineVersion? _version;
    private string? _dockerRoot;
    private IReadOnlyList<string> _mountRoots = [];

    public bool IsReady => _ready;

    public EngineVersion? Version => _version;

    public string? DockerRoot => _dockerRoot;

    /// <summary>Directories mounted into Quaymaster at the identical path (administrator-controlled stack roots).</summary>
    public IReadOnlyList<string> MountRoots => _mountRoots;

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_ready)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_ready)
            {
                return;
            }

            _version = await engine.GetVersionAsync(ct);
            var self = await SelfDetection.DetectAsync(config.SelfContainer, probe, engine, ct);
            policy.Self = self;
            try
            {
                _dockerRoot = (await engine.InfoAsync(ct)).Str("DockerRootDir");
            }
            catch (AppException)
            {
                _dockerRoot = null;
            }

            _mountRoots = config.StackAutoMounts && self.ContainerId is not null
                ? await ComputeMountRootsAsync(self.ContainerId, ct)
                : [];
            if (self.Failure is not null)
            {
                Log.SelfUnknown(log, self.Failure);
            }

            _ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> ComputeMountRootsAsync(string selfId, CancellationToken ct)
    {
        var raw = await engine.InspectContainerAsync(selfId, ct);
        var roots = new List<string>();
        foreach (var m in raw.Arr("Mounts").Objects())
        {
            var src = m.Str("Source");
            if (m.Str("Type") != "bind" || string.IsNullOrEmpty(src) || src != m.Str("Destination") || !src.StartsWith('/') || src == "/")
            {
                continue;
            }

            if (new[] { "/proc", "/sys", "/dev", "/etc", "/run", "/var/run", "/boot" }.Any(d => Under(src, d)))
            {
                continue;
            }

            if (Under(config.DataDir, src) || Under(config.DockerSocket, src))
            {
                continue;
            }

            if (_dockerRoot is not null && (Under(src, _dockerRoot) || Under(_dockerRoot, src)))
            {
                continue;
            }

            if (!probe.IsDirectory(src))
            {
                continue;
            }

            roots.Add(src);
        }

        return [.. roots.Distinct(StringComparer.Ordinal)];
    }

    internal static bool Under(string child, string parent) =>
        child == parent || child.StartsWith(parent.EndsWith('/') ? parent : parent + "/", StringComparison.Ordinal);

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Warning, Message = "Running in a container but could not identify it ({Reason}); mutations are refused (fail closed)")]
        public static partial void SelfUnknown(ILogger logger, string reason);
    }
}

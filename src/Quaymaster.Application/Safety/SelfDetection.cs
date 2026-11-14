using System.Text.RegularExpressions;
using Quaymaster.Application.Docker;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Safety;

/// <summary>Host facts needed to find out whether (and which container) we run in. Abstracted for tests.</summary>
public interface ISelfProbe
{
    string? ReadFile(string path);

    string Hostname();

    bool IsDirectory(string path);
}

/// <summary>
/// Self-detection order (RT-05): QM_SELF_CONTAINER, mountinfo, cgroup, hostname match. Inside a container, failure to
/// identify ourselves is reported (and makes the policy fail closed: read-only).
/// </summary>
public static partial class SelfDetection
{
    public static string? ContainerIdFromMountinfo(string mountinfo)
    {
        ArgumentNullException.ThrowIfNull(mountinfo);
        var m = MountinfoRe().Match(mountinfo);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? ContainerIdFromCgroup(string cgroup)
    {
        ArgumentNullException.ThrowIfNull(cgroup);
        var m = CgroupRe().Match(cgroup);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool LooksContainerised(ISelfProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.ReadFile("/.dockerenv") is not null)
        {
            return true;
        }

        var mi = probe.ReadFile("/proc/self/mountinfo");
        if (mi is not null && ContainerIdFromMountinfo(mi) is not null)
        {
            return true;
        }

        var cg = probe.ReadFile("/proc/1/cgroup");
        return cg is not null && ContainerHintRe().IsMatch(cg);
    }

    public static async Task<SelfInfo> DetectAsync(string? configured, ISelfProbe probe, IDockerEngine engine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(engine);
        configured = configured?.Trim();
        var inContainer = !string.IsNullOrEmpty(configured) || LooksContainerised(probe);
        if (!inContainer)
        {
            return SelfInfo.None;
        }

        var candidates = new List<(string How, string Ref)>();
        if (!string.IsNullOrEmpty(configured))
        {
            candidates.Add(("QM_SELF_CONTAINER", configured));
        }

        var mi = probe.ReadFile("/proc/self/mountinfo");
        if (mi is not null && ContainerIdFromMountinfo(mi) is { } fromMi)
        {
            candidates.Add(("mountinfo", fromMi));
        }

        var cg = probe.ReadFile("/proc/self/cgroup");
        if (cg is not null && ContainerIdFromCgroup(cg) is { } fromCg)
        {
            candidates.Add(("cgroup", fromCg));
        }

        var host = probe.Hostname();
        if (!string.IsNullOrEmpty(host))
        {
            candidates.Add(("hostname", host));
        }

        var lastError = "no identification source succeeded";
        foreach (var (how, reference) in candidates)
        {
            var info = await TryInspectAsync(engine, reference, ct);
            if (info is null)
            {
                lastError = $"{how}=\"{(reference.Length > 12 ? reference[..12] : reference)}\" does not match any container";
                if (how == "QM_SELF_CONTAINER")
                {
                    break; // an explicit setting that does not resolve is an error
                }

                continue;
            }

            return new SelfInfo(
                true,
                info.Str("Id"),
                (info.Str("Name") ?? string.Empty).TrimStart('/'),
                info.Obj("Config")?.Obj("Labels")?.Str(Quaymaster.Domain.Docker.ComposeLabels.Project),
                [.. (info.Obj("NetworkSettings")?.Obj("Networks")?.Select(kv => kv.Key) ?? [])],
                [.. info.Arr("Mounts").Objects().Where(m => m.Str("Type") == "volume" && !string.IsNullOrEmpty(m.Str("Name"))).Select(m => m.Str("Name")!)],
                null);
        }

        return SelfInfo.None with { InContainer = true, Failure = lastError };
    }

    private static async Task<System.Text.Json.Nodes.JsonObject?> TryInspectAsync(IDockerEngine engine, string reference, CancellationToken ct)
    {
        try
        {
            return await engine.InspectContainerAsync(reference, ct);
        }
        catch (Quaymaster.Application.Errors.AppException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"/containers/([0-9a-f]{64})/(?:hostname|hosts|resolv\.conf)", RegexOptions.CultureInvariant)]
    private static partial Regex MountinfoRe();

    [GeneratedRegex(@"(?:docker[-/]|/docker/)([0-9a-f]{64})", RegexOptions.CultureInvariant)]
    private static partial Regex CgroupRe();

    [GeneratedRegex("(docker|containerd|kubepods)", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerHintRe();
}

using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Docker;

/// <summary>Compose bookkeeping labels.</summary>
public static class ComposeLabels
{
    public const string Project = "com.docker.compose.project";
    public const string Service = "com.docker.compose.service";
    public const string WorkingDir = "com.docker.compose.project.working_dir";
    public const string ConfigFiles = "com.docker.compose.project.config_files";
    public const string EnvironmentFile = "com.docker.compose.project.environment_file";
    public const string ConfigHash = "com.docker.compose.config-hash";
    public const string Version = "com.docker.compose.version";
    public const string DependsOn = "com.docker.compose.depends_on";
    public const string ContainerNumber = "com.docker.compose.container-number";
    public const string OneOff = "com.docker.compose.oneoff";
    public const string Image = "com.docker.compose.image";
}

public enum RefKind
{
    Container,
    Volume,
    Network,
    Image,
}

/// <summary>Reference validation and small naming helpers (port of <c>docker/model.ts</c>).</summary>
public static partial class DockerNames
{
    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    {
        "created", "running", "paused", "restarting", "removing", "exited", "dead",
    };

    public static string NormState(string? state)
    {
        var v = (state ?? string.Empty).ToLowerInvariant();
        return States.Contains(v) ? v : "dead";
    }

    public static string HealthFromStatus(string? status)
    {
        var s = status ?? string.Empty;
        if (s.Contains("(healthy)", StringComparison.Ordinal))
        {
            return "healthy";
        }

        if (s.Contains("(unhealthy)", StringComparison.Ordinal))
        {
            return "unhealthy";
        }

        return s.Contains("(health: starting)", StringComparison.Ordinal) ? "starting" : "none";
    }

    public static string ShortId(string id)
    {
        var bare = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
        return bare.Length <= 12 ? bare : bare[..12];
    }

    /// <summary>Container name as Docker lists it (leading slash removed).</summary>
    public static string CleanName(string? name, string id) =>
        (string.IsNullOrEmpty(name) ? (id.Length <= 12 ? id : id[..12]) : name).TrimStart('/');

    public static bool IsFullId(string value) => FullIdRe().IsMatch(value);

    /// <summary>True for a reference that is safe to splice into an Engine API URL path (audit C-02).</summary>
    public static bool IsSafeRef(string? reference, RefKind kind)
    {
        if (reference is null)
        {
            return false;
        }

        if (kind != RefKind.Image)
        {
            return SafeNameRe().IsMatch(reference);
        }

        return SafeRefRe().IsMatch(reference) && reference.Split('/').All(seg => seg.Length > 0 && seg != "." && seg != "..");
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullIdRe();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeNameRe();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:@/-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRefRe();
}

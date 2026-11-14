using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Safety;

/// <summary>Information about the Quaymaster process itself (RT-05).</summary>
public sealed record SelfInfo(
    bool InContainer,
    string? ContainerId,
    string? ContainerName,
    string? Project,
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> Volumes,
    string? Failure)
{
    public static readonly SelfInfo None = new(false, null, null, null, [], [], null);
}

public enum RefusalCode
{
    ReadOnly,
    OutOfScope,
    SelfProtected,
}

public sealed record Refusal(RefusalCode Code, string Message);

public sealed record ContainerRef(string Id, string Name, string? Project);

/// <summary>Names of volumes / networks / containers the resolved project config references.</summary>
public sealed record ProjectRefs(IReadOnlyList<string> Volumes, IReadOnlyList<string> Networks, IReadOnlyList<string> Containers);

public sealed record SafetyOptions(
    bool ReadOnly,
    IReadOnlyList<string> Scope,
    IReadOnlyList<string> VisibleScope,
    IReadOnlyList<string> ProtectedNames);

/// <summary>
/// The safety policy: read-only mode, managed scope (QM_MANAGED_SCOPE) and self-protection. Pure: no I/O. The
/// application's mutation gateway calls it after resolving the target to its full id and inspecting it.
/// Port of <c>security/guard.ts</c> (decision logic only).
/// </summary>
public sealed class SafetyPolicy(SafetyOptions options)
{
    /// <summary>Compose subcommands that only read.</summary>
    public static readonly IReadOnlySet<string> ComposeReadOnly = new HashSet<string>(StringComparer.Ordinal) { "config", "ls", "ps", "images", "version" };

    /// <summary>Compose subcommands that change things (refused in read-only mode).</summary>
    public static readonly IReadOnlySet<string> ComposeMutating = new HashSet<string>(StringComparer.Ordinal) { "up", "stop", "start", "restart", "down" };

    private readonly Regex[] _scope = options.Scope.Select(GlobToRegex).ToArray();
    private readonly Regex[] _protected = options.ProtectedNames.Select(GlobToRegex).ToArray();
    private readonly Regex[] _visible = options.VisibleScope.Select(GlobToRegex).ToArray();
    private SelfInfo _self = SelfInfo.None;

    public IReadOnlyList<string> Scope => options.Scope;

    public SelfInfo Self
    {
        get => Volatile.Read(ref _self);
        set => Volatile.Write(ref _self, value);
    }

    /// <summary>Read-only because configured, or because we run in a container and cannot identify it.</summary>
    public string? ReadOnlyReason
    {
        get
        {
            if (options.ReadOnly)
            {
                return "QM_READ_ONLY is set";
            }

            var self = Self;
            return self.Failure is null ? null : $"self-protection could not identify this container ({self.Failure})";
        }
    }

    public bool ReadOnly => ReadOnlyReason is not null;

    public bool VisibilityRestricted => _visible.Length > 0;

    public static Regex GlobToRegex(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        var escaped = Regex.Replace(glob, @"[.+^${}()|\[\]\\]", "\\$&").Replace("*", ".*", StringComparison.Ordinal).Replace("?", ".", StringComparison.Ordinal);
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>QM_VISIBLE_SCOPE: resources outside it do not exist as far as the UI and API are concerned.</summary>
    public bool Visible(string name) => _visible.Length == 0 || _visible.Any(r => r.IsMatch(name.TrimStart('/')));

    public bool ContainerVisible(string name, string? project) => Visible(name) && (project is null || Visible(project));

    public bool InScope(string name) => _scope.Length == 0 || _scope.Any(r => r.IsMatch(name));

    /// <summary>Containers: name AND compose project (when present) must both match a non-empty scope.</summary>
    public bool ContainerInScope(string name, string? project) => InScope(name.TrimStart('/')) && (project is null || InScope(project));

    public string? ContainerProtectedReason(ContainerRef c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var self = Self;
        if (self.ContainerId is not null && c.Id == self.ContainerId)
        {
            return "this is the Quaymaster container itself";
        }

        return _protected.Any(r => r.IsMatch(c.Name.TrimStart('/'))) ? "listed in QM_PROTECTED" : null;
    }

    /// <summary>Project-level mutations are refused for Quaymaster's own Compose project (RT-05).</summary>
    public string? ProjectProtectedReason(string project)
    {
        var self = Self;
        if (self.Project is not null && project == self.Project)
        {
            return "this is Quaymaster's own Compose project (per-service actions on other services are allowed)";
        }

        return _protected.Any(r => r.IsMatch(project)) ? "listed in QM_PROTECTED" : null;
    }

    public string? VolumeProtectedReason(string name) => Self.Volumes.Contains(name) ? "used by Quaymaster's own container" : null;

    public string? NetworkProtectedReason(string name) => Self.Networks.Contains(name) ? "used by Quaymaster's own container" : null;

    /// <summary>Why mutating this container is refused right now, in priority order. Null = allowed.</summary>
    public Refusal? ContainerRefusal(ContainerRef c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var ro = ReadOnlyReason;
        if (ro is not null)
        {
            return new Refusal(RefusalCode.ReadOnly, $"Read-only mode: {ro}");
        }

        var prot = ContainerProtectedReason(c);
        if (prot is not null)
        {
            return new Refusal(RefusalCode.SelfProtected, $"Refused: {prot}");
        }

        if (!ContainerInScope(c.Name, c.Project))
        {
            var project = c.Project is null ? string.Empty : $" (project \"{c.Project}\")";
            return new Refusal(RefusalCode.OutOfScope, $"Refused: \"{c.Name.TrimStart('/')}\"{project} is outside QM_MANAGED_SCOPE");
        }

        return null;
    }

    public Refusal? ProjectRefusal(string project, ProjectRefs? refs = null, bool perService = false)
    {
        var ro = ReadOnlyReason;
        if (ro is not null)
        {
            return new Refusal(RefusalCode.ReadOnly, $"Read-only mode: {ro}");
        }

        var prot = perService ? null : ProjectProtectedReason(project);
        if (prot is not null)
        {
            return new Refusal(RefusalCode.SelfProtected, $"Refused: {prot}");
        }

        if (!InScope(project))
        {
            return new Refusal(RefusalCode.OutOfScope, $"Refused: project \"{project}\" is outside QM_MANAGED_SCOPE");
        }

        if (refs is not null && _scope.Length > 0)
        {
            var bad = new List<string>();
            bad.AddRange(refs.Volumes.Where(v => !InScope(v)).Select(v => $"volume {v}"));
            bad.AddRange(refs.Networks.Where(n => !InScope(n)).Select(n => $"network {n}"));
            bad.AddRange(refs.Containers.Where(c => !InScope(c)).Select(c => $"container {c}"));
            if (bad.Count > 0)
            {
                return new Refusal(
                    RefusalCode.OutOfScope,
                    $"Refused: project \"{project}\" references resources outside QM_MANAGED_SCOPE: {string.Join(", ", bad)}");
            }
        }

        foreach (var v in refs?.Volumes ?? [])
        {
            var p = VolumeProtectedReason(v);
            if (p is not null)
            {
                return new Refusal(RefusalCode.SelfProtected, $"Refused: volume {v} is {p}");
            }
        }

        return null;
    }
}

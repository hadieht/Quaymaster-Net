using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Domain.Storage;

public enum RefusalKind
{
    SelfProtected,
    OutOfScope,
    Conflict,
}

/// <param name="ComposeOptIn">A Compose-project refusal that an explicit opt-in (volumes only) may override.</param>
public sealed record StorageRefusal(RefusalKind Kind, string Reason, bool ComposeOptIn = false);

/// <summary>What Quaymaster knows about the Compose projects that exist on disk. <see cref="Ok"/> false = discovery failed (fail closed).</summary>
public sealed record ComposeOwnership(
    bool Ok,
    IReadOnlySet<string> Projects,
    IReadOnlyDictionary<string, string> Volumes,
    IReadOnlyDictionary<string, string> Networks)
{
    public static ComposeOwnership Empty { get; } = new(true, new HashSet<string>(), new Dictionary<string, string>(), new Dictionary<string, string>());
}

public sealed record VolumeFacts(string Name, IReadOnlyDictionary<string, string> Labels, string? CreatedAt);

public sealed record NetworkFacts(string Id, string Name, IReadOnlyDictionary<string, string> Labels, bool Ingress, int AttachedCount, string? Created);

/// <summary>
/// Eligibility rules for removing a volume or network (port of <c>volumeRefusal</c>/<c>networkRefusal</c> in
/// storage-cleanup.ts). Pure: shared by preview, execute and single removal, so all three apply the same rules.
/// </summary>
public static partial class StorageRules
{
    /// <summary>Networks the daemon creates itself; never removable.</summary>
    public static readonly IReadOnlySet<string> PredefinedNetworks = new HashSet<string>(StringComparer.Ordinal) { "bridge", "host", "none", "ingress", "docker_gwbridge" };

    /// <summary>Anonymous volume = a 64-hex name created by <c>-v /path</c> without a name (and not Compose-owned).</summary>
    public static bool IsAnonymous(string name, IReadOnlyDictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return AnonRe().IsMatch(name) && !labels.ContainsKey(ComposeLabels.Project);
    }

    public static int? HoursSince(string? iso, long nowMs)
    {
        if (string.IsNullOrEmpty(iso) || iso.StartsWith("0001", StringComparison.Ordinal))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) || t.ToUnixTimeMilliseconds() <= 0)
        {
            return null;
        }

        return (int)Math.Round((nowMs - t.ToUnixTimeMilliseconds()) / 3_600_000.0, MidpointRounding.AwayFromZero);
    }

    public static StorageRefusal? VolumeRefusal(SafetyPolicy g, VolumeFacts v, IReadOnlyList<string> users, ComposeOwnership own, bool includeCompose)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(own);
        var project = v.Labels.GetValueOrDefault(ComposeLabels.Project);
        if (g.VolumeProtectedReason(v.Name) is { } self)
        {
            return new StorageRefusal(RefusalKind.SelfProtected, $"Refused: volume {v.Name} is {self}");
        }

        if (!g.InScope(v.Name) || (project is not null && !g.InScope(project)))
        {
            return new StorageRefusal(RefusalKind.OutOfScope, $"Refused: volume \"{v.Name}\"{(project is null ? string.Empty : $" (project \"{project}\")")} is outside QM_MANAGED_SCOPE");
        }

        if (users.Count > 0)
        {
            return new StorageRefusal(RefusalKind.Conflict, $"in use by {(users.Count == 1 ? "container" : "containers")} {string.Join(", ", users)} (a stopped container counts)");
        }

        own.Volumes.TryGetValue(v.Name, out var declaredBy);
        if (project is not null || declaredBy is not null)
        {
            var proj = project ?? declaredBy;
            if (!own.Ok)
            {
                return new StorageRefusal(RefusalKind.Conflict, "Compose projects could not be discovered, so Compose volumes are kept");
            }

            if (!includeCompose)
            {
                if ((project is not null && own.Projects.Contains(project)) || declaredBy is not null)
                {
                    return new StorageRefusal(RefusalKind.Conflict, $"belongs to Compose project \"{proj}\", which still exists on disk (opt in explicitly to remove it)", true);
                }

                // RT3-01: "not discovered" is not "deleted". After `compose down` a project has no containers; its volume is most
                // likely a stopped stack's database, so the same explicit opt-in is required.
                return new StorageRefusal(RefusalKind.Conflict, $"belongs to Compose project \"{proj}\", which Quaymaster cannot find (no containers, not under QM_STACK_PATHS); it may still exist on disk (opt in explicitly to remove it)", true);
            }
        }

        return null;
    }

    public static StorageRefusal? NetworkRefusal(SafetyPolicy g, NetworkFacts n, IReadOnlyList<string> users, ComposeOwnership own)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(n);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(own);
        var project = n.Labels.GetValueOrDefault(ComposeLabels.Project);
        if (PredefinedNetworks.Contains(n.Name) || n.Ingress)
        {
            return new StorageRefusal(RefusalKind.Conflict, "predefined Docker network");
        }

        if (g.NetworkProtectedReason(n.Name) is { } self)
        {
            return new StorageRefusal(RefusalKind.SelfProtected, $"Refused: network {n.Name} is {self}");
        }

        if (!g.InScope(n.Name) || (project is not null && !g.InScope(project)))
        {
            return new StorageRefusal(RefusalKind.OutOfScope, $"Refused: network \"{n.Name}\"{(project is null ? string.Empty : $" (project \"{project}\")")} is outside QM_MANAGED_SCOPE");
        }

        if (n.AttachedCount > 0 || users.Count > 0)
        {
            return new StorageRefusal(RefusalKind.Conflict, $"containers are attached: {(users.Count > 0 ? string.Join(", ", users) : $"{n.AttachedCount} container(s)")}");
        }

        own.Networks.TryGetValue(n.Name, out var declaredBy);
        if (!own.Ok && (project is not null || declaredBy is not null))
        {
            return new StorageRefusal(RefusalKind.Conflict, "Compose projects could not be discovered, so Compose networks are kept");
        }

        if (declaredBy is not null || (project is not null && own.Projects.Contains(project)))
        {
            return new StorageRefusal(RefusalKind.Conflict, $"defined by Compose project \"{declaredBy ?? project}\", which still exists on disk");
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex AnonRe();
}

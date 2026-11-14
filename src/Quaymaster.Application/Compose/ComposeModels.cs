using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Quaymaster.Domain.Compose;

namespace Quaymaster.Application.Compose;

public sealed record ProjectReason(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null);

public sealed record MountHint(string Path, string Volume, string StackPaths);

public sealed record ComposeContainerRef(string Id, string Name, string State, string Health, string ImageId, string? ConfigHash);

public sealed record ComposeServiceInfo(string Name, string? Image, IReadOnlyList<ComposeContainerRef> Containers, string Status, int Replicas);

public sealed record ComposeProjectSummary(
    string Name,
    string Level,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<ProjectReason> ReasonDetails,
    string FileAccess,
    MountHint? MountHint,
    string? WorkingDir,
    IReadOnlyList<string> ConfigFiles,
    string Source,
    string Status,
    IReadOnlyList<ComposeServiceInfo> Services,
    IReadOnlyList<string> ComposeVersions,
    string? ProtectedReason,
    bool Mutable);

public sealed record ComposeDetailDto(
    ComposeProjectSummary Summary,
    DependencyGraph Graph,
    IReadOnlyList<ComposeServiceInfo> Services,
    IReadOnlyDictionary<string, List<string>> NamespaceProviders);

public sealed record ComposeGraphDto(DependencyGraph Graph, IReadOnlyDictionary<string, List<string>> NamespaceProviders);

public sealed record ProjectFilesView(
    string Source,
    JsonNode? Config,
    IReadOnlyList<string> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Files = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? EnvKeys = null);

public sealed record ValidationResult(bool Valid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed record ComposeActionRequest(
    string? Service = null,
    bool? ForceRecreate = null,
    string? Confirm = null,
    bool? ApplyDrift = null,
    bool? ConfirmNamespaceProvider = null);

public sealed record DriftEntry(string Service, IReadOnlyList<string> Running, string Wanted);

public sealed record ScannedProject(string Name, string Dir, IReadOnlyList<string> Files);

/// <summary>The result of checking a path against the stack allow-list (RT-02).</summary>
public sealed record PathCheck(bool Ok, string? Real = null, string? Code = null, string? Reason = null)
{
    public static PathCheck Success(string real) => new(true, real);

    public static PathCheck Fail(string code, string reason) => new(false, null, code, reason);
}

/// <summary>
/// Everything Compose discovery needs from the file system, behind one seam: the allow-list of directories Quaymaster may
/// run Compose against, the read-only scan of those directories, and reading a Compose file. Implemented in Infrastructure.
/// </summary>
public interface IStackFiles
{
    /// <summary>QM_STACK_PATHS plus directories mounted at the identical path.</summary>
    IReadOnlyList<string> AllowedRoots { get; }

    PathCheck Check(string path, bool requireReadable = false);

    /// <summary>Compose files found under the allowed roots (bounded depth and count; never follows symlinked directories).</summary>
    IReadOnlyList<ScannedProject> Scan();

    /// <summary>Realpath when it resolves, else the input.</summary>
    string RealPath(string path);

    /// <summary>UTF-8 text of a file up to 1 MiB, or null when unreadable.</summary>
    string? ReadText(string file);
}

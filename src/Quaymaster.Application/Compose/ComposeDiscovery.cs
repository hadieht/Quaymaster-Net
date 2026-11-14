using System.Text.RegularExpressions;
using Quaymaster.Application.Docker;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Compose;

public sealed record ComposeLsEntry(string Name, string Status, string ConfigFiles);

public sealed record DiscoveryInput(
    IReadOnlyList<ContainerInfo> Containers,
    IReadOnlyList<ComposeLsEntry> Ls,
    IReadOnlyList<ScannedProject> Scanned,
    IStackFiles Files,
    SafetyPolicy Policy);

/// <summary>
/// Builds project summaries from labels (untrusted!), <c>docker compose ls -a</c> and a filesystem scan, and assigns
/// capability levels (spec 4.2 + RT-02):
/// <list type="bullet">
/// <item><b>full</b> - recorded paths agree everywhere and the realpath is under an allowed root (never the Docker root)</item>
/// <item><b>limited</b> - containers exist but files cannot be trusted/read here; file operations are refused. The summary
/// says precisely why (<c>reasonDetails</c>, one stable code each) and what could be done.</item>
/// </list>
/// Every Compose project Docker reports is listed, whatever its path: discovery needs no configuration.
/// Port of <c>compose/discovery.ts</c> (the read-only file helper is not ported: limited projects have fileAccess none).
/// </summary>
public static partial class ComposeDiscovery
{
    public static string DeriveProjectName(string dir)
    {
        var baseName = Path.GetFileName(dir.TrimEnd('/')).ToLowerInvariant();
        var cleaned = NonNameCharsRe().Replace(baseName, string.Empty);
        return LeadingJunkRe().Replace(cleaned, string.Empty);
    }

    /// <summary>True when a compose file pulls in remote resources via include/extends (RT-21): such projects are limited.</summary>
    public static bool UsesRemoteResources(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var inInclude = false;
        foreach (var line in text.Split('\n'))
        {
            if (IncludeStartRe().IsMatch(line) || IndentedExtendsRe().IsMatch(line))
            {
                inInclude = true;
            }
            else if (NonIndentedRe().IsMatch(line) && !CommentRe().IsMatch(line))
            {
                inInclude = false;
            }

            if (inInclude && RemoteRefRe().IsMatch(line))
            {
                return true;
            }

            if (FileRemoteRe().IsMatch(line))
            {
                return true;
            }
        }

        return false;
    }

    public static ComposeServiceInfo AggregateService(string name, IReadOnlyList<ContainerInfo> cs)
    {
        ArgumentNullException.ThrowIfNull(cs);
        var containers = cs.Select(c => new ComposeContainerRef(
            c.Id,
            c.Name,
            DockerNames.NormState(c.State),
            DockerNames.HealthFromStatus(c.Status),
            c.ImageId,
            c.Labels.GetValueOrDefault(ComposeLabels.ConfigHash))).ToList();
        var running = containers.Where(c => c.State == "running").ToList();
        string status;
        if (containers.Count == 0)
        {
            status = "not-created";
        }
        else if (containers.Any(c => c.Health == "unhealthy" || c.State == "restarting"))
        {
            status = "unhealthy";
        }
        else if (running.Count == containers.Count)
        {
            status = "running";
        }
        else if (running.Count == 0)
        {
            status = "stopped";
        }
        else
        {
            status = "partial";
        }

        return new ComposeServiceInfo(name, cs.Count > 0 ? cs[0].Image : null, containers, status, containers.Count);
    }

    private static string ProjectStatus(IReadOnlyList<ComposeServiceInfo> services)
    {
        var created = services.Where(s => s.Status != "not-created").ToList();
        if (created.Count == 0)
        {
            return "down";
        }

        if (created.Any(s => s.Status == "unhealthy"))
        {
            return "unhealthy";
        }

        if (created.All(s => s.Status == "running"))
        {
            return "running";
        }

        return created.All(s => s.Status == "stopped") ? "stopped" : "partial";
    }

    public static IReadOnlyList<ComposeProjectSummary> BuildProjects(DiscoveryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var policy = input.Policy;
        var files = input.Files;
        var byProject = new Dictionary<string, List<ContainerInfo>>(StringComparer.Ordinal);
        foreach (var c in input.Containers)
        {
            if (c.Project is { } p)
            {
                if (!byProject.TryGetValue(p, out var list))
                {
                    list = [];
                    byProject[p] = list;
                }

                list.Add(c);
            }
        }

        var lsByName = input.Ls.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var scannedByName = input.Scanned.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<ComposeProjectSummary>();
        var consumed = new HashSet<ScannedProject>();

        foreach (var (name, cs) in byProject)
        {
            var reasons = new List<ProjectReason>();

            void Add(string code, string message, string? hint = null) => reasons.Add(new ProjectReason(code, message, hint));

            var dirs = cs.Select(c => c.Labels.GetValueOrDefault(ComposeLabels.WorkingDir) ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
            var fileSets = cs.Select(c => c.Labels.GetValueOrDefault(ComposeLabels.ConfigFiles) ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
            string? workingDir = null;
            IReadOnlyList<string> projectFiles = [];
            if (dirs.Count > 1 || fileSets.Count > 1)
            {
                Add("conflict", "conflicting labels: containers of this project disagree on working directory or config files");
            }

            var dir0 = dirs.FirstOrDefault() ?? string.Empty;
            var files0 = fileSets.FirstOrDefault() ?? string.Empty;
            if (dir0.Length == 0 || files0.Length == 0)
            {
                Add("no_labels", "containers carry no working-directory/config-file labels (not created by Docker Compose, or by an old version)");
            }
            else
            {
                workingDir = dir0;
                projectFiles = LabelPaths.ParseConfigFilesLabel(files0);
            }

            if (lsByName.TryGetValue(name, out var lsEntry) && projectFiles.Count > 0)
            {
                var lsFiles = LabelPaths.ParseConfigFilesLabel(lsEntry.ConfigFiles);
                if (lsFiles.Count > 0 && string.Join(',', lsFiles) != string.Join(',', projectFiles))
                {
                    Add("conflict", "`docker compose ls` reports different config files than the container labels");
                }
            }

            if (!ComposeArgv.IsValidProjectName(name))
            {
                Add("unsafe_name", "unusual project name; refusing file operations");
            }

            var labelDir = workingDir is null ? null : LabelPaths.ValidateLabelDir(workingDir);
            var hint = labelDir is { Ok: true } ? LabelPaths.MountHintFor(labelDir.Dir!) : null;
            if (workingDir is not null)
            {
                if (labelDir is { Ok: false })
                {
                    Add("untrusted", $"the recorded working directory is not usable: {labelDir.Reason}");
                }
                else
                {
                    var c = files.Check(workingDir);
                    if (!c.Ok)
                    {
                        Add(c.Code!, c.Reason!, HintFor(c.Code!, hint?.Volume));
                    }
                    else
                    {
                        foreach (var f in projectFiles)
                        {
                            var fc = files.Check(f, requireReadable: true);
                            if (!fc.Ok)
                            {
                                // the directory is fine, so a vanished file is "missing", not "not mounted"
                                if (fc.Code == "not_mounted")
                                {
                                    Add("file_missing", $"{f} does not exist in the project directory", "Restore the file or recreate the project.");
                                }
                                else
                                {
                                    Add(fc.Code!, fc.Reason!, HintFor(fc.Code!, hint?.Volume));
                                }
                            }
                            else if (UsesRemoteResources(files.ReadText(fc.Real!) ?? string.Empty))
                            {
                                Add("remote_include", $"{f} uses remote include/extends, which Quaymaster does not follow");
                            }
                        }
                    }
                }
            }

            var source = "containers";
            foreach (var s in scannedByName.GetValueOrDefault(name) ?? [])
            {
                var sameDir = workingDir is not null && files.RealPath(s.Dir) == files.RealPath(workingDir);
                if (sameDir)
                {
                    consumed.Add(s);
                    source = "both";
                }
                else
                {
                    Add("duplicate_dir", $"another scanned directory also maps to project \"{name}\"");
                }
            }

            var services = cs
                .GroupBy(c => c.Service ?? "(unknown)", StringComparer.Ordinal)
                .Select(g => AggregateService(g.Key, [.. g]))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
            var level = reasons.Count == 0 ? "full" : "limited";
            var refusal = policy.ProjectRefusal(name);
            result.Add(new ComposeProjectSummary(
                name,
                level,
                [.. reasons.Select(r => r.Message).Distinct(StringComparer.Ordinal)],
                Dedupe(reasons),
                level == "full" ? "local" : "none",
                level != "full" ? hint : null,
                workingDir,
                projectFiles,
                source,
                ProjectStatus(services),
                services,
                [.. cs.Select(c => c.Labels.GetValueOrDefault(ComposeLabels.Version)).Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).Select(v => v!)],
                refusal?.Message,
                refusal is null));
        }

        // Projects visible only on disk (down). They are inside the allow-list by construction.
        foreach (var s in input.Scanned)
        {
            if (consumed.Contains(s) || byProject.ContainsKey(s.Name))
            {
                continue;
            }

            var reasons = new List<ProjectReason>();
            foreach (var f in s.Files)
            {
                var fc = files.Check(f, requireReadable: true);
                if (!fc.Ok)
                {
                    reasons.Add(new ProjectReason(fc.Code!, fc.Reason!));
                }
                else if (UsesRemoteResources(files.ReadText(fc.Real!) ?? string.Empty))
                {
                    reasons.Add(new ProjectReason("remote_include", $"{f} uses remote include/extends"));
                }
            }

            if ((scannedByName.GetValueOrDefault(s.Name)?.Count ?? 0) > 1)
            {
                reasons.Add(new ProjectReason("duplicate_dir", $"several scanned directories map to project \"{s.Name}\""));
            }

            var refusal = policy.ProjectRefusal(s.Name);
            result.Add(new ComposeProjectSummary(
                s.Name,
                reasons.Count == 0 ? "full" : "limited",
                [.. reasons.Select(r => r.Message).Distinct(StringComparer.Ordinal)],
                Dedupe(reasons),
                reasons.Count == 0 ? "local" : "none",
                null,
                s.Dir,
                s.Files,
                "scan",
                "down",
                [],
                [],
                refusal?.Message,
                refusal is null));
        }

        return [.. result.OrderBy(p => p.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>What the operator can do about a reason. Generic on purpose: no site-specific paths.</summary>
    private static string? HintFor(string code, string? volumeLine)
    {
        var mount = volumeLine is null ? null : $"Add this line to the volumes of the Quaymaster service and recreate it:  {volumeLine}";
        return code switch
        {
            "not_mounted" => mount,
            "path_not_allowed" => volumeLine is not null
                ? $"Mount the directory into Quaymaster at the same path ({volumeLine}) or list it in QM_STACK_PATHS."
                : "Mount the directory into Quaymaster at the same path or list it in QM_STACK_PATHS.",
            "not_readable" => "Make the project files readable by the user Quaymaster runs as (for example chmod g+r and a shared group).",
            "untrusted" => "Use a real directory inside the mounted path (no symlinks that leave it) and keep it out of the Docker data root.",
            _ => null,
        };
    }

    private static List<ProjectReason> Dedupe(List<ProjectReason> list)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return list.Where(r => seen.Add($"{r.Code}|{r.Message}")).ToList();
    }

    [GeneratedRegex("[^a-z0-9_-]", RegexOptions.CultureInvariant)]
    private static partial Regex NonNameCharsRe();

    [GeneratedRegex("^[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingJunkRe();

    [GeneratedRegex(@"^(include|extends)\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex IncludeStartRe();

    [GeneratedRegex(@"^\s+extends\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex IndentedExtendsRe();

    [GeneratedRegex(@"^\S", RegexOptions.CultureInvariant)]
    private static partial Regex NonIndentedRe();

    [GeneratedRegex(@"^\s*#", RegexOptions.CultureInvariant)]
    private static partial Regex CommentRe();

    [GeneratedRegex(@"^\s*(?:-\s*)?(?:path\s*:\s*)?[""']?(?:git|oci|https?|ssh)://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RemoteRefRe();

    [GeneratedRegex(@"^\s*file\s*:\s*[""']?(git|oci|https?)://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FileRemoteRe();
}

/// <summary>Validation of paths that arrive in Compose LABELS (untrusted data): port of <c>compose/labelpath.ts</c>.</summary>
public static partial class LabelPaths
{
    private static readonly string[] DeniedPrefixes = ["/proc", "/sys", "/dev"];

    public sealed record LabelPathCheck(bool Ok, string? Dir = null, string? Reason = null);

    public static LabelPathCheck ValidateLabelDir(string? p)
    {
        if (string.IsNullOrEmpty(p))
        {
            return new LabelPathCheck(false, null, "the working directory label is empty");
        }

        if (p.Length > 4096)
        {
            return new LabelPathCheck(false, null, "the working directory label is too long");
        }

        if (ControlRe().IsMatch(p))
        {
            return new LabelPathCheck(false, null, "the working directory contains control characters");
        }

        if (!p.StartsWith('/'))
        {
            return new LabelPathCheck(false, null, $"\"{p}\" is not an absolute path");
        }

        if (p == "/")
        {
            return new LabelPathCheck(false, null, "the filesystem root is not a project directory");
        }

        if (p.Split('/').Contains(".."))
        {
            return new LabelPathCheck(false, null, $"\"{p}\" contains \"..\"");
        }

        if (!IsCanonical(p) || p.EndsWith('/'))
        {
            return new LabelPathCheck(false, null, $"\"{p}\" is not a canonical path");
        }

        if (DeniedPrefixes.Any(d => p == d || p.StartsWith(d + "/", StringComparison.Ordinal)))
        {
            return new LabelPathCheck(false, null, $"{p} is a system directory");
        }

        return new LabelPathCheck(true, p);
    }

    /// <summary>POSIX normalisation check: no empty segments (//) and no "." segments.</summary>
    private static bool IsCanonical(string p) =>
        !p.Contains("//", StringComparison.Ordinal) && !p.Split('/').Skip(1).Any(s => s == ".");

    /// <summary>Splits <c>com.docker.compose.project.config_files</c> (comma separated).</summary>
    public static IReadOnlyList<string> ParseConfigFilesLabel(string? raw) =>
        [.. (raw ?? string.Empty).Split(',').Select(f => f.Trim()).Where(f => f.Length > 0)];

    public static MountHint MountHintFor(string dir) => new(dir, $"- {dir}:{dir}", $"QM_STACK_PATHS={dir}");

    [GeneratedRegex(@"[\u0000-\u001f\u007f]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlRe();
}

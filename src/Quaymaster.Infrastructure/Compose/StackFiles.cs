using System.Text;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Configuration;

namespace Quaymaster.Infrastructure.Compose;

/// <summary>
/// Allow-list of directories Quaymaster may run Compose against (RT-02). Compose labels are untrusted data: a path is
/// acceptable only if its realpath (symlinks resolved, <c>..</c> collapsed) lies under an allowed root and not under the
/// Docker data root. Roots come from administrator-controlled sources only: <c>QM_STACK_PATHS</c> and (optionally) bind
/// mounts of Quaymaster's own container whose source and destination are the SAME path.
/// </summary>
internal sealed class StackFiles(QmConfig config, Func<string?> dockerRoot, Func<IReadOnlyList<string>> mountRoots) : IStackFiles
{
    private static readonly string[] ComposeFileNames = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];
    private static readonly string[] SkipDirs = ["node_modules", ".git", ".cache", "data", "volumes"];

    private readonly string[] _roots = [.. config.StackPaths.Select(RealOrResolved)];

    public IReadOnlyList<string> AllowedRoots => [.. _roots.Concat(mountRoots().Select(RealOrResolved)).Distinct(StringComparer.Ordinal)];

    public string RealPath(string path) => RealOrResolved(path);

    public PathCheck Check(string path, bool requireReadable = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || !Path.IsPathRooted(path))
        {
            return PathCheck.Fail("untrusted", $"\"{path}\" is not an absolute path");
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            return PathCheck.Fail("untrusted", "path contains a NUL byte");
        }

        string real;
        try
        {
            real = Resolve(path);
        }
        catch (UnauthorizedAccessException)
        {
            return PathCheck.Fail("not_readable", $"{path} is not readable by Quaymaster (permission denied)");
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return PathCheck.Fail("not_mounted", $"{path} does not exist inside the Quaymaster container (the directory is not mounted at this path, or is not accessible)");
        }

        if (dockerRoot() is { Length: > 0 } dr && Under(real, RealOrResolved(dr)))
        {
            return PathCheck.Fail("untrusted", $"{path} lies inside the Docker data root");
        }

        var roots = AllowedRoots;
        if (!roots.Any(r => Under(real, r)))
        {
            // a symlink that leaves an allowed root is an escape attempt, not merely a missing allow-list entry
            var lexical = Path.GetFullPath(path);
            if (roots.Any(r => Under(lexical, r)))
            {
                return PathCheck.Fail("untrusted", $"{path} is a symlink that resolves outside the allowed stack paths ({real})");
            }

            return PathCheck.Fail("path_not_allowed", $"{path} is outside the allowed stack paths (QM_STACK_PATHS and directories mounted into Quaymaster at the same path)");
        }

        if (requireReadable && !IsReadable(real))
        {
            return PathCheck.Fail("not_readable", $"{path} is not readable by Quaymaster (permission denied)");
        }

        return PathCheck.Success(real);
    }

    public IReadOnlyList<ScannedProject> Scan()
    {
        var found = new List<ScannedProject>();
        var visited = 0;

        void Walk(string dir, int depth)
        {
            if (visited++ >= 500)
            {
                return;
            }

            string[] entries;
            try
            {
                entries = [.. Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>()];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var file = ComposeFileNames.FirstOrDefault(entries.Contains);
            if (file is not null)
            {
                var name = ComposeDiscovery.DeriveProjectName(dir);
                if (Application.Compose.ComposeArgv.IsValidProjectName(name))
                {
                    found.Add(new ScannedProject(name, dir, [Path.Combine(dir, file)]));
                }

                return; // a project directory: do not descend
            }

            if (depth >= 3)
            {
                return;
            }

            foreach (var e in entries.Order(StringComparer.Ordinal))
            {
                if (e.StartsWith('.') || SkipDirs.Contains(e, StringComparer.Ordinal))
                {
                    continue;
                }

                var p = Path.Combine(dir, e);
                try
                {
                    var info = new DirectoryInfo(p);
                    if (info.Exists && info.LinkTarget is null)
                    {
                        Walk(p, depth + 1);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // unreadable
                }
            }
        }

        foreach (var r in _roots)
        {
            try
            {
                Walk(Resolve(r), 0);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // missing root
            }
        }

        return found;
    }

    public string? ReadText(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Length > 1024 * 1024 ? null : File.ReadAllText(file, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Under(string child, string parent) =>
        child == parent || child.StartsWith(parent.EndsWith('/') ? parent : parent + "/", StringComparison.Ordinal);

    private static string RealOrResolved(string path)
    {
        try
        {
            return Resolve(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Path.GetFullPath(path);
        }
    }

    private static bool IsReadable(string real)
    {
        try
        {
            if (Directory.Exists(real))
            {
                using var e = Directory.EnumerateFileSystemEntries(real).GetEnumerator();
                _ = e.MoveNext();
                return true;
            }

            using var s = File.OpenRead(real);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>realpath(3)</c>: resolves symlinks component by component. Throws <see cref="FileNotFoundException"/> (ENOENT),
    /// <see cref="UnauthorizedAccessException"/> (EACCES) or <see cref="IOException"/> (loop).
    /// </summary>
    internal static string Resolve(string path)
    {
        var pending = new LinkedList<string>(Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries));
        var resolved = "/";
        var links = 0;
        while (pending.First is { } node)
        {
            var part = node.Value;
            pending.RemoveFirst();
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                resolved = Path.GetDirectoryName(resolved.TrimEnd('/')) is { Length: > 0 } up ? up : "/";
                continue;
            }

            var candidate = resolved == "/" ? "/" + part : $"{resolved}/{part}";
            var attributes = File.GetAttributes(candidate); // throws ENOENT / EACCES
            FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (info.LinkTarget is { } target)
            {
                if (++links > 40)
                {
                    throw new IOException("too many levels of symbolic links");
                }

                var absolute = Path.IsPathRooted(target) ? target : $"{resolved.TrimEnd('/')}/{target}";
                foreach (var seg in Path.GetFullPath(absolute).Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
                {
                    pending.AddFirst(seg);
                }

                resolved = "/";
                continue;
            }

            resolved = candidate;
        }

        return resolved;
    }
}

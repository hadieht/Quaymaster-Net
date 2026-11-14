using System.Text.RegularExpressions;
using Quaymaster.Application.Errors;

namespace Quaymaster.Application.Compose;

/// <summary>Where a Compose invocation is anchored. Every field is passed explicitly (RT-01).</summary>
public sealed record ComposeTarget(string ProjectName, string WorkingDir, IReadOnlyList<string> Files);

/// <summary>The only Compose commands Quaymaster can express. There is no way to pass free-form arguments.</summary>
public abstract record ComposeCommand
{
    public sealed record ConfigJson : ComposeCommand;

    public sealed record ConfigValidate : ComposeCommand;

    public sealed record ConfigNoEnvResolution : ComposeCommand;

    public sealed record ConfigHash(IReadOnlyList<string> Services) : ComposeCommand;

    public sealed record Ps : ComposeCommand;

    public sealed record Images : ComposeCommand;

    public sealed record Up(IReadOnlyList<string> Services, bool NoDeps, bool ForceRecreate, bool NoStart = false) : ComposeCommand;

    public sealed record StopServices(IReadOnlyList<string> Services) : ComposeCommand;

    public sealed record StartServices(IReadOnlyList<string> Services) : ComposeCommand;

    public sealed record RestartServices(IReadOnlyList<string> Services) : ComposeCommand;

    public sealed record Down : ComposeCommand;
}

/// <summary>
/// Builds the argv (everything after the <c>docker</c> binary) from a fixed template; user-derived values use the
/// <c>--flag=value</c> form and service names come after <c>--</c>. The result is re-validated by
/// <see cref="AssertSafe"/>, which every executor calls last. Port of <c>compose/argv.ts</c>.
/// </summary>
public static partial class ComposeArgv
{
    /// <summary>Flags that must never appear in an argv we build (RT-28).</summary>
    private static readonly string[] ForbiddenFlags =
    [
        "--remove-orphans", "-v", "--volumes", "--rmi", "--build", "--always-recreate-deps", "--renew-anon-volumes", "-V", "--yes", "-y",
    ];

    /// <summary>
    /// Compose subcommands that must never appear in an argv we build. <c>pull</c> would move the shared, user-visible tag
    /// outside the staged-by-digest workflow (RT-07); the others are simply never needed.
    /// </summary>
    private static readonly string[] ForbiddenSubcommands = ["pull", "push", "build", "create", "run", "exec", "cp", "rm", "kill"];

    /// <summary><c>docker compose ls --all --format=json</c>: the only invocation without project flags.</summary>
    public static readonly IReadOnlyList<string> Ls = ["compose", "ls", "--all", "--format=json"];

    public static readonly IReadOnlyList<string> Version = ["compose", "version", "--short"];

    public static bool IsValidProjectName(string name) => ProjectNameRe().IsMatch(name);

    public static bool IsValidServiceName(string name) => ServiceNameRe().IsMatch(name);

    private static void ValidateName(string kind, string value, bool project)
    {
        var ok = project ? ProjectNameRe().IsMatch(value) : ServiceNameRe().IsMatch(value);
        if (!ok || value.StartsWith('-'))
        {
            throw new AppException(400, ErrorCode.Validation, $"Invalid {kind} name");
        }
    }

    public static IReadOnlyList<string> Build(ComposeTarget target, ComposeCommand command)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(command);
        ValidateName("project", target.ProjectName, true);
        if (target.Files.Count == 0)
        {
            throw new AppException(400, ErrorCode.Validation, "No compose files");
        }

        foreach (var f in target.Files)
        {
            if (!f.StartsWith('/') || f.Contains('\0', StringComparison.Ordinal))
            {
                throw new AppException(400, ErrorCode.Validation, "Compose file path must be absolute");
            }
        }

        if (!target.WorkingDir.StartsWith('/'))
        {
            throw new AppException(400, ErrorCode.Validation, "Project directory must be absolute");
        }

        var argv = new List<string>
        {
            "compose",
            "--ansi=never",
            "--progress=plain",
            $"--project-name={target.ProjectName}",
            $"--project-directory={target.WorkingDir}",
        };
        argv.AddRange(target.Files.Select(f => $"--file={f}"));

        List<string> Svc(IReadOnlyList<string> services)
        {
            foreach (var s in services)
            {
                ValidateName("service", s, false);
            }

            return services.Count > 0 ? ["--", .. services] : [];
        }

        switch (command)
        {
            case ComposeCommand.ConfigJson:
                argv.AddRange(["config", "--format=json"]);
                break;
            case ComposeCommand.ConfigValidate:
                argv.AddRange(["config", "--quiet"]);
                break;
            case ComposeCommand.ConfigNoEnvResolution:
                argv.AddRange(["config", "--format=json", "--no-env-resolution"]);
                break;
            case ComposeCommand.ConfigHash h:
                foreach (var s in h.Services)
                {
                    ValidateName("service", s, false);
                }

                argv.AddRange(["config", $"--hash={(h.Services.Count > 0 ? string.Join(',', h.Services) : "*")}"]);
                break;
            case ComposeCommand.Ps:
                argv.AddRange(["ps", "--all", "--format=json"]);
                break;
            case ComposeCommand.Images:
                argv.AddRange(["images", "--format=json"]);
                break;
            case ComposeCommand.Up up:
                argv.Add("up");
                argv.Add(up.NoStart ? "--no-start" : "--detach");
                argv.AddRange(["--no-build", "--pull=never"]);
                if (up.NoDeps)
                {
                    argv.Add("--no-deps");
                }

                if (up.ForceRecreate)
                {
                    argv.Add("--force-recreate");
                }

                argv.AddRange(Svc(up.Services));
                break;
            case ComposeCommand.StopServices s:
                argv.Add("stop");
                argv.AddRange(Svc(s.Services));
                break;
            case ComposeCommand.StartServices s:
                argv.Add("start");
                argv.AddRange(Svc(s.Services));
                break;
            case ComposeCommand.RestartServices s:
                argv.Add("restart");
                argv.AddRange(Svc(s.Services));
                break;
            case ComposeCommand.Down:
                argv.Add("down"); // never -v / --rmi / --remove-orphans
                break;
            default:
                throw new AppException(500, ErrorCode.Internal, "Unsupported Compose command");
        }

        AssertSafe(argv, command is ComposeCommand.Up { ForceRecreate: true });
        return argv;
    }

    /// <summary>The Compose subcommand of a built argv (first non-flag token after <c>compose</c>).</summary>
    public static string SubcommandOf(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        foreach (var a in argv.Skip(1))
        {
            if (!a.StartsWith('-'))
            {
                return a;
            }
        }

        return string.Empty;
    }

    public static void AssertSafe(IReadOnlyList<string> argv, bool allowForceRecreate = false)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var sub = SubcommandOf(argv);
        if (ForbiddenSubcommands.Contains(sub, StringComparer.Ordinal))
        {
            throw new AppException(500, ErrorCode.Internal, $"Forbidden Compose subcommand {sub}");
        }

        var sep = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i] == "--")
            {
                sep = i;
                break;
            }
        }

        foreach (var a in sep >= 0 ? argv.Take(sep) : argv)
        {
            var eq = a.IndexOf('=', StringComparison.Ordinal);
            var bare = eq >= 0 ? a[..eq] : a;
            if (ForbiddenFlags.Contains(bare, StringComparer.Ordinal))
            {
                throw new AppException(500, ErrorCode.Internal, $"Forbidden Compose flag {bare}");
            }

            if (bare == "--force-recreate" && !allowForceRecreate)
            {
                throw new AppException(500, ErrorCode.Internal, "Forbidden Compose flag --force-recreate");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNameRe();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNameRe();
}

public sealed record ComposeRunResult(int Code, string Stdout, string Stderr, bool TimedOut);

public sealed record ComposeRunOptions(TimeSpan? Timeout = null, Action<string>? OnOutput = null);

/// <summary>
/// Spawns <c>docker</c> with an already validated argv (no shell, minimal environment). Implemented in Infrastructure;
/// only <c>MutationGateway</c> calls it.
/// </summary>
public interface IComposeCli
{
    Task<ComposeRunResult> RunAsync(IReadOnlyList<string> argv, ComposeRunOptions options, CancellationToken ct);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Compose;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Redaction;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Maintenance;

/// <summary>A container as the executor sees it: Compose identity, image and state.</summary>
public sealed record EngineContainer(string Id, string Name, string? Project, string? Service, string ImageId, string State, string Status);

public sealed record DeployResult(bool Ok, int Code, string Detail, IReadOnlyList<string> Argv);

/// <summary>
/// Everything the run executor needs from the Docker / Compose world (the seam the failure-scenario tests fake). Reads are fresh
/// (never the 3 s inventory cache); every mutation goes through <see cref="IMutationGateway"/> in the implementation.
/// </summary>
public interface IRunEnvironment
{
    Task<ProjectTarget> TargetAsync(string project, CancellationToken ct);

    /// <summary>Every container the daemon reports (fresh; ignore list not applied: safety computations see everything).</summary>
    Task<IReadOnlyList<EngineContainer>> ContainersAsync(CancellationToken ct);

    Task<InspectLite?> InspectAsync(string containerId, CancellationToken ct);

    /// <summary><c>docker compose up</c> of one service (<c>--no-deps --no-build --pull never</c>); never pulls.</summary>
    Task<DeployResult> UpAsync(string project, string service, bool forceRecreate, bool noStart, Action<string> log, CancellationToken ct);

    /// <summary>RT-01: the wanted <c>compose config --hash</c> of one service (null = cannot be resolved).</summary>
    Task<string?> ConfigHashAsync(string project, string service, CancellationToken ct);

    /// <summary>RT-01: true when the service's running containers differ from the Compose file.</summary>
    Task<bool> HasDriftAsync(string project, string service, CancellationToken ct);

    /// <summary>File hashes + the redacted resolved configuration (best effort).</summary>
    Task<JsonObject> SnapshotAsync(string project, CancellationToken ct);

    Task<IReadOnlyList<GraphEdge>> EdgesAsync(string project, CancellationToken ct);

    /// <summary>Restart through the service layer (scope, self-protection, audit). Returns an error message or null.</summary>
    Task<string?> RestartAsync(string containerId, CancellationToken ct);

    /// <summary>Drops the inventory caches after a change.</summary>
    void Invalidate();
}

public sealed class RunEnvironment(
    IComposeService compose,
    IContainerService containers,
    IDockerEngine engine,
    IStackFiles files,
    IMutationGateway gateway,
    QmConfig config) : IRunEnvironment
{
    private static readonly Actor System = new("system", null);

    public async Task<ProjectTarget> TargetAsync(string project, CancellationToken ct)
    {
        var t = await compose.TargetAsync(project, ct);
        return new ProjectTarget(t.ProjectName, t.WorkingDir, t.Files);
    }

    public async Task<IReadOnlyList<EngineContainer>> ContainersAsync(CancellationToken ct) =>
        [.. (await containers.ListEverythingFreshAsync(ct)).Select(c => new EngineContainer(c.Id, c.Name, c.Project, c.Service, c.ImageId, c.State, c.Status))];

    public async Task<InspectLite?> InspectAsync(string containerId, CancellationToken ct)
    {
        var i = await engine.InspectContainerAsync(containerId, ct);
        if (i is null)
        {
            return null;
        }

        var state = i["State"] as JsonObject;
        return new InspectLite(
            i["Image"]?.GetValue<string>() ?? string.Empty,
            (int)(i["RestartCount"]?.GetValue<double>() ?? 0),
            state?["Status"]?.GetValue<string>() ?? string.Empty,
            state?["Running"]?.GetValue<bool>() ?? false,
            state?["Restarting"]?.GetValue<bool>() ?? false,
            (int)(state?["ExitCode"]?.GetValue<double>() ?? 0),
            state?["StartedAt"]?.GetValue<string>() ?? string.Empty,
            (state?["Health"] as JsonObject)?["Status"]?.GetValue<string>());
    }

    public async Task<DeployResult> UpAsync(string project, string service, bool forceRecreate, bool noStart, Action<string> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        var target = await compose.TargetAsync(project, ct);
        var argv = ComposeArgv.Build(target, new ComposeCommand.Up([service], true, forceRecreate, noStart));

        // F-02: the same fence as a direct action - the project (and what it references) and EVERY container of the service
        // (QM_PROTECTED, self, scope, hidden ones included) are checked right before the recreate.
        var refs = DependencyGraphBuilder.References(await compose.ResolvedConfigForSafetyAsync(project, ct));
        await gateway.AssertProjectAsync(project, new ProjectRefs(refs.Volumes, refs.Networks, refs.Containers), true, ct);
        foreach (var c in (await containers.ListEverythingFreshAsync(ct)).Where(c => c.Project == project && c.Service == service))
        {
            await gateway.AssertContainerAsync(new ContainerRef(c.Id, c.Name, project), ct);
        }

        try
        {
            var r = await gateway.RunComposeAsync(argv, new ComposeRunOptions(null, log), ct);
            return r.Code == 0
                ? new DeployResult(true, 0, string.Empty, argv)
                : new DeployResult(false, r.Code, $"docker compose up exited with code {r.Code}: {Tail(r.Stderr)}", argv);
        }
        finally
        {
            Invalidate();
        }
    }

    public async Task<string?> ConfigHashAsync(string project, string service, CancellationToken ct)
    {
        try
        {
            return (await compose.HashesAndDriftAsync(project, [service], ct)).Hashes.GetValueOrDefault(service);
        }
        catch (AppException)
        {
            return null;
        }
    }

    public async Task<bool> HasDriftAsync(string project, string service, CancellationToken ct) =>
        (await compose.HashesAndDriftAsync(project, [service], ct)).Drift.Count > 0;

    public async Task<JsonObject> SnapshotAsync(string project, CancellationToken ct)
    {
        var target = await compose.TargetAsync(project, ct);
        var hashes = new JsonArray();
        foreach (var f in target.Files)
        {
            var text = files.ReadText(f);
            hashes.Add(new JsonObject
            {
                ["file"] = f,
                ["sha256"] = text is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            });
        }

        JsonNode? cfg = null;
        try
        {
            cfg = Redactor.RedactDeep(await compose.ResolvedConfigForSafetyAsync(project, ct), new RedactOptions(config.RedactAllEnv));
        }
        catch (AppException)
        {
            // best effort: validation already passed
        }

        return new JsonObject { ["files"] = hashes, ["redactedConfig"] = cfg };
    }

    public async Task<IReadOnlyList<GraphEdge>> EdgesAsync(string project, CancellationToken ct) => (await compose.DetailAsync(project, ct)).Graph.Edges;

    public async Task<string?> RestartAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await containers.ActAsync(containerId, "restart", new ActionRequest(TimeoutSeconds: 5), System, ct);
            return null;
        }
        catch (AppException e)
        {
            return e.Message;
        }
    }

    public void Invalidate()
    {
        compose.Invalidate();
        containers.Invalidate();
    }

    private static string Tail(string s) => s.Length <= 1000 ? s.Trim() : s[^1000..].Trim();
}

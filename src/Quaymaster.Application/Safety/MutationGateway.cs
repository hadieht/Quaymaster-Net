using System.Diagnostics;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Safety;

/// <summary>What is being audited. <see cref="Target"/> may be refined once the real identity is known.</summary>
public sealed class AuditScope(Actor actor, string action, string? target, object? parameters)
{
    public Actor Actor { get; } = actor;

    public string Action { get; } = action;

    public string? Target { get; set; } = target;

    public object? Params { get; set; } = parameters;
}

/// <summary>
/// THE choke point for everything that changes Docker state. Every mutation - Engine API lifecycle calls and every
/// <c>docker compose</c> invocation - passes through here and is checked against read-only mode, the managed scope and
/// self-protection (<see cref="SafetyPolicy"/>), then audited. Nothing else may depend on <see cref="IDockerCommands"/>
/// or <see cref="IComposeCli"/> (architecture test). There is no route that accepts a command line.
/// </summary>
public interface IMutationGateway
{
    SafetyPolicy Policy { get; }

    /// <summary>Runs <paramref name="body"/> and records success / failure / denied (403, 409) with the duration.</summary>
    Task<T> AuditedAsync<T>(AuditScope scope, Func<Task<T>> body, CancellationToken ct);

    Task RecordAsync(AuditScope scope, AuditOutcome outcome, object? parameters, long? durationMs, CancellationToken ct);

    Task AssertContainerAsync(ContainerRef container, CancellationToken ct);

    Task AssertProjectAsync(string project, ProjectRefs? refs, bool perService, CancellationToken ct);

    /// <summary>Engine lifecycle call on one container, by full id. Returns false when it was already in that state.</summary>
    Task<bool> ExecuteContainerAsync(ContainerRef container, ContainerCommand command, CancellationToken ct);

    /// <summary>Pulls <c>repository@digest</c> (read-only mode refuses). Only digests are ever pulled.</summary>
    Task PullImageAsync(ImagePull request, IProgress<PullProgress>? progress, CancellationToken ct);

    Task TagImageAsync(string imageId, string repo, string tag, CancellationToken ct);

    Task RemoveImageAsync(string nameOrId, CancellationToken ct);

    /// <summary>Removes a volume: read-only mode, managed scope and Quaymaster's own volumes are enforced here too.</summary>
    Task RemoveVolumeAsync(string name, CancellationToken ct);

    Task RemoveNetworkAsync(string id, string name, CancellationToken ct);

    // ---- standalone config clone (RT-12). `source` is always the ORIGINAL container's ref (original name): scope, self-protection and
    // the read-only check are decided on it, never on the temporary clone name. ------------------------------------------------

    /// <summary>Creates the stopped clone <paramref name="cloneName"/> from the Engine create body; returns its full id.</summary>
    Task<string> CreateCloneAsync(ContainerRef source, string cloneName, System.Text.Json.Nodes.JsonObject body, CancellationToken ct);

    Task ConnectCloneNetworkAsync(ContainerRef source, string cloneId, string network, System.Text.Json.Nodes.JsonObject endpoint, CancellationToken ct);

    /// <summary>Renames <paramref name="container"/> (full id) to <paramref name="newName"/>.</summary>
    Task RenameContainerAsync(ContainerRef container, string newName, CancellationToken ct);

    Task SetRestartPolicyAsync(ContainerRef container, string policy, int maximumRetryCount, CancellationToken ct);

    /// <summary>Start / stop / remove the clone <c>command.ContainerId</c> on behalf of <paramref name="source"/>.</summary>
    Task<bool> ExecuteOnCloneAsync(ContainerRef source, ContainerCommand command, CancellationToken ct);

    /// <summary>Runs <c>docker compose</c> with a built argv; the subcommand allow-list and read-only mode are enforced here.</summary>
    Task<ComposeRunResult> RunComposeAsync(IReadOnlyList<string> argv, ComposeRunOptions? options, CancellationToken ct);
}

public sealed class MutationGateway(
    SafetyPolicy policy,
    DockerBootstrap bootstrap,
    IDockerCommands commands,
    IComposeCli compose,
    IAuditLog audit,
    IMaintenanceGate? maintenance = null) : IMutationGateway
{
    private static readonly System.Text.Json.JsonSerializerOptions ParamOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public SafetyPolicy Policy => policy;

    public async Task<T> AuditedAsync<T>(AuditScope scope, Func<Task<T>> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(body);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await body();
            await RecordAsync(scope, AuditOutcome.Success, scope.Params, sw.ElapsedMilliseconds, CancellationToken.None);
            return result;
        }
        catch (Exception e)
        {
            var denied = e is AppException { Status: 403 or 409 };
            await RecordAsync(scope, denied ? AuditOutcome.Denied : AuditOutcome.Failure, WithError(scope.Params, e.Message), sw.ElapsedMilliseconds, CancellationToken.None);
            throw;
        }
    }

    public Task RecordAsync(AuditScope scope, AuditOutcome outcome, object? parameters, long? durationMs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return audit.RecordAsync(new AuditRecord(scope.Actor.Name, scope.Action, outcome, scope.Actor.Ip, scope.Target, parameters, durationMs), ct);
    }

    public async Task AssertContainerAsync(ContainerRef container, CancellationToken ct)
    {
        await bootstrap.EnsureReadyAsync(ct);
        Throw(policy.ContainerRefusal(container));
    }

    public async Task AssertProjectAsync(string project, ProjectRefs? refs, bool perService, CancellationToken ct)
    {
        await bootstrap.EnsureReadyAsync(ct);
        Throw(policy.ProjectRefusal(project, refs, perService));
    }

    public async Task<bool> ExecuteContainerAsync(ContainerRef container, ContainerCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(command);
        await AssertContainerAsync(container, ct);
        maintenance?.Assert(container.Project);
        if (!Domain.Docker.DockerNames.IsFullId(command.ContainerId) || command.ContainerId != container.Id)
        {
            throw new AppException(500, ErrorCode.Internal, "Engine mutation requires the full id of the checked container");
        }

        return await commands.ExecuteAsync(command, ct);
    }

    private async Task EnsureWritableAsync(CancellationToken ct)
    {
        await bootstrap.EnsureReadyAsync(ct);
        if (policy.ReadOnlyReason is { } ro)
        {
            throw new AppException(409, ErrorCode.ReadOnly, $"Read-only mode: {ro}");
        }
    }

    public async Task PullImageAsync(ImagePull request, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Domain.Registry.ImageReferences.IsDigest(request.Digest))
        {
            throw new AppException(500, ErrorCode.Internal, "Only digest-pinned pulls are permitted");
        }

        await EnsureWritableAsync(ct);
        await commands.PullImageAsync(request, progress, ct);
    }

    public async Task TagImageAsync(string imageId, string repo, string tag, CancellationToken ct)
    {
        await EnsureWritableAsync(ct);
        await commands.TagImageAsync(imageId, repo, tag, ct);
    }

    public async Task RemoveImageAsync(string nameOrId, CancellationToken ct)
    {
        await EnsureWritableAsync(ct);
        await commands.RemoveImageAsync(nameOrId, ct);
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        await EnsureWritableAsync(ct);
        if (policy.VolumeProtectedReason(name) is { } self)
        {
            throw new AppException(409, ErrorCode.SelfProtected, $"Refused: volume {name} is {self}");
        }

        if (!policy.InScope(name))
        {
            throw new AppException(409, ErrorCode.OutOfScope, $"Refused: volume \"{name}\" is outside QM_MANAGED_SCOPE");
        }

        await commands.RemoveVolumeAsync(name, ct);
    }

    public async Task RemoveNetworkAsync(string id, string name, CancellationToken ct)
    {
        await EnsureWritableAsync(ct);
        if (policy.NetworkProtectedReason(name) is { } self)
        {
            throw new AppException(409, ErrorCode.SelfProtected, $"Refused: network {name} is {self}");
        }

        if (!policy.InScope(name))
        {
            throw new AppException(409, ErrorCode.OutOfScope, $"Refused: network \"{name}\" is outside QM_MANAGED_SCOPE");
        }

        await commands.RemoveNetworkAsync(id, ct);
    }

    public async Task<string> CreateCloneAsync(ContainerRef source, string cloneName, System.Text.Json.Nodes.JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        await AssertCloneSourceAsync(source, ct);
        if (!Domain.Docker.DockerNames.IsSafeRef(cloneName, Domain.Docker.RefKind.Container))
        {
            throw AppException.BadRequest("Invalid container name");
        }

        return await commands.CreateContainerAsync(cloneName, body, ct);
    }

    public async Task ConnectCloneNetworkAsync(ContainerRef source, string cloneId, string network, System.Text.Json.Nodes.JsonObject endpoint, CancellationToken ct)
    {
        await AssertCloneSourceAsync(source, ct);
        RequireFullId(cloneId);
        await commands.ConnectNetworkAsync(network, cloneId, endpoint, ct);
    }

    public async Task RenameContainerAsync(ContainerRef container, string newName, CancellationToken ct)
    {
        await AssertCloneSourceAsync(container, ct);
        RequireFullId(container.Id);
        if (!Domain.Docker.DockerNames.IsSafeRef(newName, Domain.Docker.RefKind.Container))
        {
            throw AppException.BadRequest("Invalid container name");
        }

        await commands.RenameContainerAsync(container.Id, newName, ct);
    }

    public async Task SetRestartPolicyAsync(ContainerRef container, string policy, int maximumRetryCount, CancellationToken ct)
    {
        await AssertCloneSourceAsync(container, ct);
        RequireFullId(container.Id);
        if (policy is not ("no" or "always" or "unless-stopped" or "on-failure"))
        {
            throw AppException.BadRequest("Invalid restart policy");
        }

        await commands.SetRestartPolicyAsync(container.Id, policy, maximumRetryCount, ct);
    }

    public async Task<bool> ExecuteOnCloneAsync(ContainerRef source, ContainerCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await AssertCloneSourceAsync(source, ct);
        RequireFullId(command.ContainerId);
        return await commands.ExecuteAsync(command, ct);
    }

    private async Task AssertCloneSourceAsync(ContainerRef source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        await AssertContainerAsync(source, ct);
        await EnsureWritableAsync(ct);
        maintenance?.Assert(source.Project);
    }

    private static void RequireFullId(string id)
    {
        if (!Domain.Docker.DockerNames.IsFullId(id))
        {
            throw new AppException(500, ErrorCode.Internal, "Engine mutation requires a full container id");
        }
    }

    public async Task<ComposeRunResult> RunComposeAsync(IReadOnlyList<string> argv, ComposeRunOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0 || argv[0] != "compose")
        {
            throw new AppException(500, ErrorCode.Internal, "Only `docker compose` commands are executed");
        }

        var sub = ComposeArgv.SubcommandOf(argv);
        AssertComposeSubcommand(sub);
        ComposeArgv.AssertSafe(argv, argv.Contains("--force-recreate"));
        if (SafetyPolicy.ComposeMutating.Contains(sub))
        {
            await bootstrap.EnsureReadyAsync(ct);
            AssertComposeSubcommand(sub);
            maintenance?.Assert(argv.FirstOrDefault(a => a.StartsWith("--project-name=", StringComparison.Ordinal))?["--project-name=".Length..]);
        }

        return await compose.RunAsync(argv, options ?? new ComposeRunOptions(), ct);
    }

    /// <summary>Compose subcommand authorisation (RT-03): read-only mode allows only the reading ones.</summary>
    public void AssertComposeSubcommand(string sub)
    {
        if (SafetyPolicy.ComposeReadOnly.Contains(sub))
        {
            return;
        }

        if (!SafetyPolicy.ComposeMutating.Contains(sub))
        {
            throw new AppException(400, ErrorCode.Unsupported, $"Compose subcommand \"{sub}\" is not permitted");
        }

        if (policy.ReadOnlyReason is { } ro)
        {
            throw new AppException(409, ErrorCode.ReadOnly, $"Read-only mode: {ro}");
        }
    }

    private static Dictionary<string, object?> WithError(object? parameters, string message)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            var node = System.Text.Json.JsonSerializer.SerializeToNode(parameters, ParamOptions);
            if (node is System.Text.Json.Nodes.JsonObject o)
            {
                foreach (var (k, v) in o)
                {
                    map[k] = v?.DeepClone();
                }
            }
        }

        map["error"] = message;
        return map;
    }

    private static void Throw(Refusal? refusal)
    {
        if (refusal is null)
        {
            return;
        }

        var code = refusal.Code switch
        {
            RefusalCode.ReadOnly => ErrorCode.ReadOnly,
            RefusalCode.OutOfScope => ErrorCode.OutOfScope,
            _ => ErrorCode.SelfProtected,
        };
        throw new AppException(409, code, refusal.Message);
    }
}

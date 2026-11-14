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

    /// <summary>Runs <c>docker compose</c> with a built argv; the subcommand allow-list and read-only mode are enforced here.</summary>
    Task<ComposeRunResult> RunComposeAsync(IReadOnlyList<string> argv, ComposeRunOptions? options, CancellationToken ct);
}

public sealed class MutationGateway(
    SafetyPolicy policy,
    DockerBootstrap bootstrap,
    IDockerCommands commands,
    IComposeCli compose,
    IAuditLog audit) : IMutationGateway
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
        if (!Domain.Docker.DockerNames.IsFullId(command.ContainerId) || command.ContainerId != container.Id)
        {
            throw new AppException(500, ErrorCode.Internal, "Engine mutation requires the full id of the checked container");
        }

        return await commands.ExecuteAsync(command, ct);
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

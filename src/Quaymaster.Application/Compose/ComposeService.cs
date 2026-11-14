using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Compose;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Redaction;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Compose;

public interface IComposeService
{
    Task<IReadOnlyList<ComposeProjectSummary>> DiscoverAsync(bool fresh, CancellationToken ct);

    /// <summary>Includes projects on the "stopped monitoring" list: only for safety computations.</summary>
    Task<IReadOnlyList<ComposeProjectSummary>> DiscoverAllAsync(bool fresh, CancellationToken ct);

    Task<ComposeDetailDto> DetailAsync(string name, CancellationToken ct);

    Task<ProjectFilesView> ConfigViewAsync(string name, CancellationToken ct);

    Task<ValidationResult> ValidateAsync(string name, CancellationToken ct);

    /// <summary>Starts a Compose action as a background operation and returns its id.</summary>
    Task<string> OperateAsync(string name, string action, ComposeActionRequest request, Actor actor, CancellationToken ct);

    /// <summary>
    /// The unredacted resolved configuration of a full-capability project, for safety computations only (which images, volumes
    /// and networks a project owns). Never returned to a client. Throws when the project does not resolve.
    /// </summary>
    Task<JsonObject> ResolvedConfigForSafetyAsync(string name, CancellationToken ct);

    /// <summary>
    /// Everything the maintenance planner needs about a project, read-only: summary (throws <c>limited_capability</c> unless the
    /// project is <c>full</c>), validation result, the unredacted resolved configuration (only when valid) and the live containers.
    /// </summary>
    Task<ComposePlanContext> PlanContextAsync(string name, CancellationToken ct);

    /// <summary>Wanted <c>compose config --hash</c> per service plus the drift decision against the running containers (RT-01).</summary>
    Task<ComposeDriftCheck> HashesAndDriftAsync(string name, IReadOnlyList<string> serviceNames, CancellationToken ct);

    void Invalidate();
}

/// <summary>See <see cref="IComposeService.PlanContextAsync"/>. <paramref name="Config"/> never leaves the server.</summary>
public sealed record ComposePlanContext(
    ComposeProjectSummary Summary,
    ValidationResult Validation,
    JsonObject? Config,
    string? ConfigError,
    IReadOnlyList<ContainerInfo> Containers);

public sealed record ComposeDriftCheck(IReadOnlyDictionary<string, string> Hashes, IReadOnlyList<DriftEntry> Drift);

public sealed partial class ComposeService(
    IMutationGateway gateway,
    IStackFiles stackFiles,
    IContainerService containers,
    IOperationManager operations,
    IIgnoreList ignore,
    Scrubber scrubber,
    QmConfig config,
    TimeProvider time) : IComposeService, IRecreator
{
    public static readonly IReadOnlyList<string> Actions = ["up", "stop", "start", "restart", "down", "redeploy"];

    private static readonly TimeSpan DiscoveryTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConfigTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private (DateTimeOffset At, IReadOnlyList<ComposeProjectSummary> Value)? _cache;
    private readonly Dictionary<string, CachedConfig> _configCache = new(StringComparer.Ordinal);
    private string? _version;

    private sealed record CachedConfig(DateTimeOffset At, JsonObject Config, IReadOnlyList<string> Warnings);

    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
            _configCache.Clear();
        }
    }

    // ---- discovery ------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ComposeProjectSummary>> DiscoverAllAsync(bool fresh, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!fresh && _cache is { } hit && time.GetUtcNow() - hit.At < DiscoveryTtl)
            {
                return hit.Value;
            }
        }

        var lsTask = LsAsync(ct);
        var all = await containers.ListRawAllAsync(fresh, ct);
        var ls = await lsTask;
        var scanned = stackFiles.Scan();
        var policy = gateway.Policy;
        var value = ComposeDiscovery.BuildProjects(new DiscoveryInput(all, ls, scanned, stackFiles, policy))
            .Where(p => policy.Visible(p.Name))
            .ToList();
        lock (_gate)
        {
            _cache = (time.GetUtcNow(), value);
        }

        return value;
    }

    /// <summary>The monitored projects: what the API lists and what every action works on.</summary>
    public async Task<IReadOnlyList<ComposeProjectSummary>> DiscoverAsync(bool fresh, CancellationToken ct)
    {
        var all = await DiscoverAllAsync(fresh, ct);
        await ignore.RefreshAsync(ct);
        return all.Where(p => !ignore.IsProjectIgnored(p.Name, p.WorkingDir)).ToList();
    }

    private async Task<IReadOnlyList<ComposeLsEntry>> LsAsync(CancellationToken ct)
    {
        try
        {
            var r = await gateway.RunComposeAsync(ComposeArgv.Ls, new ComposeRunOptions(TimeSpan.FromSeconds(30)), ct);
            if (r.Code != 0 || JsonNode.Parse(r.Stdout) is not JsonArray arr)
            {
                return [];
            }

            return [.. arr.Objects().Select(o => new ComposeLsEntry(o.Str("Name") ?? string.Empty, o.Str("Status") ?? string.Empty, o.Str("ConfigFiles") ?? string.Empty))];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (AppException e) when (e.Code != ErrorCode.Internal)
        {
            return [];
        }
    }

    private async Task<ComposeProjectSummary> SummaryAsync(string name, bool includeIgnored, CancellationToken ct)
    {
        var all = await DiscoverAllAsync(false, ct);
        await ignore.RefreshAsync(ct);
        var monitored = all.FirstOrDefault(p => p.Name == name && !ignore.IsProjectIgnored(p.Name, p.WorkingDir));
        if (monitored is not null)
        {
            return monitored;
        }

        if (all.FirstOrDefault(p => p.Name == name) is { } hidden)
        {
            return includeIgnored
                ? hidden
                : throw new AppException(409, ErrorCode.Ignored, $"Project \"{name}\" is on the \"stopped monitoring\" list. Restore it in Settings to manage it again.");
        }

        throw AppException.NotFound("Compose project");
    }

    private async Task<IReadOnlyList<ContainerInfo>> RawContainersAsync(string name, bool fresh, CancellationToken ct) =>
        [.. (await containers.ListRawAsync(fresh, ct)).Where(c => c.Project == name)];

    private static ComposeTarget FullTarget(ComposeProjectSummary s)
    {
        if (s.Level != "full" || s.WorkingDir is null)
        {
            throw Limited(s, $"Project \"{s.Name}\" has capability \"{s.Level}\": file-level operations are unavailable. {string.Join("; ", s.Reasons)}");
        }

        return new ComposeTarget(s.Name, s.WorkingDir, s.ConfigFiles);
    }

    private static AppException Limited(ComposeProjectSummary s, string message) =>
        new(409, ErrorCode.LimitedCapability, message, new { level = s.Level, reasons = s.Reasons, reasonDetails = s.ReasonDetails, mountHint = s.MountHint });

    // ---- resolved configuration -----------------------------------------------------------------------------------

    public async Task<JsonObject> ResolvedConfigForSafetyAsync(string name, CancellationToken ct) =>
        (await ResolvedConfigAsync(name, false, ct)).Config;

    /// <summary>Runs <c>compose config --format json</c> (cached briefly). The raw result stays in memory; only redacted views leave.</summary>
    private async Task<CachedConfig> ResolvedConfigAsync(string name, bool fresh, CancellationToken ct)
    {
        if (!fresh)
        {
            lock (_gate)
            {
                if (_configCache.TryGetValue(name, out var hit) && time.GetUtcNow() - hit.At < ConfigTtl)
                {
                    return hit;
                }
            }
        }

        var target = FullTarget(await SummaryAsync(name, false, ct));
        var r = await gateway.RunComposeAsync(ComposeArgv.Build(target, new ComposeCommand.ConfigJson()), new ComposeRunOptions(CliTimeout), ct);
        if (r.Code != 0)
        {
            throw new AppException(422, ErrorCode.ComposeFailed, $"docker compose config failed: {Tail(Clean(r.Stderr), 2000)}");
        }

        JsonObject config;
        try
        {
            config = JsonNode.Parse(r.Stdout) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            throw new AppException(502, ErrorCode.ComposeFailed, "docker compose config returned invalid JSON");
        }

        scrubber.AddAll(Redactor.CollectSecretValues(config, RedactAll));
        var entry = new CachedConfig(time.GetUtcNow(), config, [.. UnsetVariableWarnings(r.Stderr).Select(Clean)]);
        lock (_gate)
        {
            _configCache[name] = entry;
        }

        return entry;
    }

    private bool RedactAll => config.RedactAllEnv;

    private string Clean(string s) => scrubber.Clean(s);

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];

    /// <summary>Compose reports unset interpolation variables as warnings; Quaymaster treats them as errors (RT-01).</summary>
    public static IReadOnlyList<string> UnsetVariableWarnings(string stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        return [.. stderr.Split('\n').Where(l => UnsetRe().IsMatch(l)).Select(l => l.Trim())];
    }

    /// <summary>Redacted configuration. Full projects only: <c>docker compose config</c>; others get a precise <c>limited_capability</c> error.</summary>
    public async Task<ProjectFilesView> ConfigViewAsync(string name, CancellationToken ct)
    {
        var s = await SummaryAsync(name, false, ct);
        if (s.Level != "full")
        {
            throw Limited(s, $"The configuration of \"{name}\" cannot be shown: {(s.Reasons.Count > 0 ? string.Join("; ", s.Reasons) : "files are not accessible")}");
        }

        var cfg = await ResolvedConfigAsync(name, false, ct);
        return new ProjectFilesView("compose", Redactor.RedactDeep(cfg.Config, new RedactOptions(config.RedactAllEnv)), cfg.Warnings);
    }

    public async Task<ValidationResult> ValidateAsync(string name, CancellationToken ct)
    {
        var target = FullTarget(await SummaryAsync(name, false, ct));
        var r = await gateway.RunComposeAsync(ComposeArgv.Build(target, new ComposeCommand.ConfigValidate()), new ComposeRunOptions(CliTimeout), ct);
        var warnings = UnsetVariableWarnings(r.Stderr).Select(Clean).ToList();
        var errors = new List<string>();
        if (r.Code != 0)
        {
            var text = Tail(Clean(r.Stderr.Trim()), 2000);
            errors.Add(text.Length > 0 ? text : $"exit code {r.Code}");
        }

        errors.AddRange(warnings.Select(w => $"unset variable (treated as an error): {w}"));

        // env_file / secrets / configs files must also be inside the allow-list (RT-02)
        if (r.Code == 0)
        {
            errors.AddRange(await FileReferenceProblemsAsync(target, ct));
        }

        return new ValidationResult(errors.Count == 0, errors, warnings);
    }

    /// <summary>Checks env_file / secret / config file paths of the resolved project against QM_STACK_PATHS.</summary>
    private async Task<List<string>> FileReferenceProblemsAsync(ComposeTarget target, CancellationToken ct)
    {
        var problems = new List<string>();
        var r = await gateway.RunComposeAsync(ComposeArgv.Build(target, new ComposeCommand.ConfigNoEnvResolution()), new ComposeRunOptions(CliTimeout), ct);
        if (r.Code != 0)
        {
            return problems;
        }

        JsonObject? cfg;
        try
        {
            cfg = JsonNode.Parse(r.Stdout) as JsonObject;
        }
        catch (JsonException)
        {
            return problems;
        }

        if (cfg is null)
        {
            return problems;
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, svc) in cfg.Obj("services") ?? [])
        {
            var ef = (svc as JsonObject)?["env_file"];
            var list = ef switch
            {
                JsonArray a => a.ToList(),
                null => [],
                _ => new List<JsonNode?> { ef },
            };
            foreach (var e in list)
            {
                var p = e is JsonObject eo ? eo.Str("path") : e.Str();
                if (!string.IsNullOrEmpty(p))
                {
                    paths.Add(p);
                }
            }
        }

        foreach (var (_, s) in cfg.Obj("secrets") ?? [])
        {
            if (s.Str("file") is { Length: > 0 } f)
            {
                paths.Add(f);
            }
        }

        foreach (var (_, c) in cfg.Obj("configs") ?? [])
        {
            if (c.Str("file") is { Length: > 0 } f)
            {
                paths.Add(f);
            }
        }

        foreach (var p in paths)
        {
            var abs = p.StartsWith('/') ? p : $"{target.WorkingDir}/{p}";
            var check = stackFiles.Check(abs);

            // a missing optional env file is not a security problem; only report out-of-root references
            if (!check.Ok && !(check.Reason ?? string.Empty).Contains("does not exist", StringComparison.Ordinal))
            {
                problems.Add(check.Reason!);
            }
        }

        return problems;
    }

    // ---- detail / graph -------------------------------------------------------------------------------------------

    public async Task<ComposeDetailDto> DetailAsync(string name, CancellationToken ct)
    {
        var summary = await SummaryAsync(name, false, ct);
        var raw = await RawContainersAsync(name, false, ct);
        if (summary.Level == "full")
        {
            try
            {
                var cfg = await ResolvedConfigAsync(name, false, ct);
                var graph = DependencyGraphBuilder.Build(cfg.Config);
                var running = summary.Services.ToDictionary(s => s.Name, StringComparer.Ordinal);
                var services = (cfg.Config.Obj("services") ?? [])
                    .Select(kv => running.GetValueOrDefault(kv.Key) ?? new ComposeServiceInfo(kv.Key, kv.Value.Str("image"), [], "not-created", 0))
                    .OrderBy(s => s.Name, StringComparer.CurrentCulture)
                    .ToList();
                services.AddRange(summary.Services.Where(s => services.All(x => x.Name != s.Name)));
                return new ComposeDetailDto(summary, graph, services, DependencyGraphBuilder.NamespaceProviders(graph));
            }
            catch (AppException e) when (e.Code == ErrorCode.ComposeFailed)
            {
                // fall through to the label-based view when the file does not resolve
            }
        }

        var labelGraph = DependencyGraphBuilder.FromLabels(raw.Select(c => c.Labels));
        return new ComposeDetailDto(summary, labelGraph, summary.Services, DependencyGraphBuilder.NamespaceProviders(labelGraph));
    }

    // ---- drift gate (RT-01) ---------------------------------------------------------------------------------------

    /// <summary>Wanted <c>compose config --hash</c> per service.</summary>
    public async Task<Dictionary<string, string>> ConfigHashesAsync(ComposeTarget target, IReadOnlyList<string> serviceNames, CancellationToken ct)
    {
        var r = await gateway.RunComposeAsync(ComposeArgv.Build(target, new ComposeCommand.ConfigHash(serviceNames)), new ComposeRunOptions(CliTimeout), ct);
        if (r.Code != 0)
        {
            throw new AppException(422, ErrorCode.ComposeFailed, $"cannot compute configuration hash: {Tail(Clean(r.Stderr), 1000)}");
        }

        return ParseHashes(r.Stdout);
    }

    public static Dictionary<string, string> ParseHashes(string stdout)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in stdout.Split('\n'))
        {
            var m = HashLineRe().Match(line.Trim());
            if (m.Success)
            {
                wanted[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        return wanted;
    }

    /// <summary>Pure drift decision: a service drifts when any running container's config-hash label differs from the wanted hash.</summary>
    public static List<DriftEntry> ComputeDrift(IReadOnlyList<string> serviceNames, IReadOnlyDictionary<string, string> wanted, IReadOnlyList<ContainerInfo> running)
    {
        ArgumentNullException.ThrowIfNull(serviceNames);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(running);
        var drift = new List<DriftEntry>();
        foreach (var svc in serviceNames.Count > 0 ? serviceNames : [.. wanted.Keys])
        {
            var have = running
                .Where(c => c.Service == svc)
                .Select(c => c.Labels.GetValueOrDefault(ComposeLabels.ConfigHash))
                .Where(h => !string.IsNullOrEmpty(h))
                .Select(h => h!)
                .ToList();
            if (!wanted.TryGetValue(svc, out var w) || have.Count == 0)
            {
                continue; // nothing deployed yet: nothing can drift
            }

            if (have.Any(h => h != w))
            {
                drift.Add(new DriftEntry(svc, [.. have.Distinct(StringComparer.Ordinal)], w));
            }
        }

        return drift;
    }

    public async Task<ComposePlanContext> PlanContextAsync(string name, CancellationToken ct)
    {
        var summary = await SummaryAsync(name, false, ct);
        _ = FullTarget(summary); // limited_capability for anything but a full project
        var validation = await ValidateAsync(name, ct);
        JsonObject? cfg = null;
        string? error = null;
        if (validation.Valid)
        {
            try
            {
                cfg = (await ResolvedConfigAsync(name, true, ct)).Config;
            }
            catch (AppException e) when (e.Code == ErrorCode.ComposeFailed)
            {
                error = e.Message;
            }
        }

        return new ComposePlanContext(summary, validation, cfg, error, await RawContainersAsync(name, true, ct));
    }

    public async Task<ComposeDriftCheck> HashesAndDriftAsync(string name, IReadOnlyList<string> serviceNames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serviceNames);
        var target = FullTarget(await SummaryAsync(name, false, ct));
        var wanted = await ConfigHashesAsync(target, serviceNames, ct);
        return new ComposeDriftCheck(wanted, ComputeDrift(serviceNames, wanted, await RawContainersAsync(name, false, ct)));
    }

    private async Task<List<DriftEntry>> CheckDriftAsync(ComposeTarget target, IReadOnlyList<string> serviceNames, CancellationToken ct)
    {
        var wanted = await ConfigHashesAsync(target, serviceNames, ct);
        var running = await RawContainersAsync(target.ProjectName, false, ct);
        return ComputeDrift(serviceNames, wanted, running);
    }

    // ---- operations -----------------------------------------------------------------------------------------------

    public async Task<string> OperateAsync(string name, string action, ComposeActionRequest request, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (!Actions.Contains(action, StringComparer.Ordinal))
        {
            throw AppException.BadRequest($"Unknown action \"{action}\"");
        }

        if (request.Service is not null && !ComposeArgv.IsValidServiceName(request.Service))
        {
            throw AppException.BadRequest("Invalid service name");
        }

        var scope = new AuditScope(actor, $"compose.{action}", request.Service is null ? name : $"{name}/{request.Service}", request);
        try
        {
            return await PrepareAndStartAsync(name, action, request, actor, scope, ct);
        }
        catch (Exception e)
        {
            var denied = e is AppException { Status: 403 or 409 };
            await gateway.RecordAsync(scope, denied ? AuditOutcome.Denied : AuditOutcome.Failure, new { request.Service, request.ForceRecreate, request.Confirm, request.ApplyDrift, request.ConfirmNamespaceProvider, error = e.Message }, null, CancellationToken.None);
            throw;
        }
    }

    private async Task<string> PrepareAndStartAsync(string name, string action, ComposeActionRequest req, Actor actor, AuditScope scope, CancellationToken ct)
    {
        var summary = await SummaryAsync(name, false, ct);
        var service = req.Service;
        if (service is not null && summary.Level != "full" && summary.Services.All(s => s.Name != service))
        {
            throw AppException.NotFound("Service");
        }

        // ---- limited projects: container-level start/stop/restart only (spec 4.2)
        if (summary.Level != "full")
        {
            if (action is not ("start" or "stop" or "restart"))
            {
                throw Limited(
                    summary,
                    $"\"{action}\" needs the Compose files, which Quaymaster cannot use for \"{name}\": {string.Join("; ", summary.Reasons)}. "
                    + (summary.MountHint is not null
                        ? $"Mount the project directory into Quaymaster at the same path ({summary.MountHint.Volume})."
                        : "See the project page for what to fix."));
            }

            return await EngineLevelOperationAsync(summary, action, service, actor, scope, ct);
        }

        var target = FullTarget(summary);
        var needsConfig = action is "up" or "redeploy";
        CachedConfig? cfg = null;
        if (needsConfig || service is not null)
        {
            cfg = await ResolvedConfigAsync(name, true, ct);
        }

        if (service is not null && cfg is not null && cfg.Config.Obj("services")?.ContainsKey(service) != true)
        {
            throw AppException.NotFound("Service");
        }

        var refs = cfg is null ? null : DependencyGraphBuilder.References(cfg.Config);
        await gateway.AssertProjectAsync(name, refs is null ? null : new ProjectRefs(refs.Volumes, refs.Networks, refs.Containers), service is not null, ct);
        if (service is not null)
        {
            foreach (var c in (await RawContainersAsync(name, false, ct)).Where(c => c.Service == service))
            {
                await gateway.AssertContainerAsync(new ContainerRef(c.Id, c.Name, name), ct);
            }
        }

        if (needsConfig && cfg is { Warnings.Count: > 0 })
        {
            throw new AppException(409, ErrorCode.Validation, "Compose reports unset variables (treated as an error)", new { warnings = cfg.Warnings });
        }

        var destructive = action == "down" || (action == "redeploy" && req.ForceRecreate == true);
        if (destructive && req.Confirm != name)
        {
            throw new AppException(409, ErrorCode.ConfirmationRequired, $"Confirm by sending {{\"confirm\":\"{name}\"}}", new { expected = name });
        }

        if (needsConfig && cfg is not null)
        {
            // RT-11: recreating a namespace/volume provider affects its dependents
            if (service is not null)
            {
                var graph = DependencyGraphBuilder.Build(cfg.Config);
                if (DependencyGraphBuilder.NamespaceProviders(graph).TryGetValue(service, out var dependents) && dependents.Count > 0 && req.ConfirmNamespaceProvider != true)
                {
                    throw new AppException(
                        409,
                        ErrorCode.ConfirmationRequired,
                        $"\"{service}\" provides a network namespace/volumes to {string.Join(", ", dependents)}; recreating it breaks them until they are recreated too. Send {{\"confirmNamespaceProvider\":true}} to proceed.",
                        new { providerFor = dependents, field = "confirmNamespaceProvider" });
                }
            }

            // RT-01: the hash gate
            IReadOnlyList<string> svcs = service is not null ? [service] : [.. (cfg.Config.Obj("services") ?? []).Select(kv => kv.Key)];
            var drift = await CheckDriftAsync(target, svcs, ct);
            if (drift.Count > 0 && req.ApplyDrift != true)
            {
                var drifted = new JsonObject();
                foreach (var d in drift)
                {
                    drifted[d.Service] = cfg.Config.Obj("services")?[d.Service]?.DeepClone();
                }

                throw new AppException(
                    409,
                    ErrorCode.BlockedDrift,
                    "The Compose configuration differs from what is running; a redeploy would apply those changes too. Review the redacted configuration and send {\"applyDrift\":true} to proceed.",
                    new
                    {
                        services = drift,
                        redactedConfig = Redactor.RedactDeep(new JsonObject { ["services"] = drifted }, new RedactOptions(config.RedactAllEnv)),
                        field = "applyDrift",
                    });
            }
        }

        var services = service is null ? (IReadOnlyList<string>)[] : [service];
        ComposeCommand command = action switch
        {
            "up" => new ComposeCommand.Up(services, service is not null, false),
            "redeploy" => new ComposeCommand.Up(services, service is not null, req.ForceRecreate == true),
            "stop" => new ComposeCommand.StopServices(services),
            "start" => new ComposeCommand.StartServices(services),
            "restart" => new ComposeCommand.RestartServices(services),
            _ when service is not null => throw AppException.BadRequest("`down` applies to a whole project; use stop for a single service"),
            _ => new ComposeCommand.Down(),
        };
        var argv = ComposeArgv.Build(target, command);
        var version = await ComposeVersionAsync(ct);

        return await operations.StartAsync(
            new OperationStart(
                $"compose.{action}",
                scope.Target ?? name,
                actor.Name,
                req,
                $"project:{name}",
                async ctx =>
                {
                    var started = time.GetTimestamp();
                    ctx.Log($"$ docker {string.Join(' ', Redactor.RedactArgv(argv))}\n(compose {version ?? "unknown"})\n");
                    try
                    {
                        var r = await gateway.RunComposeAsync(argv, new ComposeRunOptions(null, ctx.Log), CancellationToken.None);
                        if (r.Code != 0)
                        {
                            throw new AppException(502, ErrorCode.ComposeFailed, $"docker compose {ComposeArgv.SubcommandOf(argv)} exited with code {r.Code}{(r.TimedOut ? " (timed out)" : string.Empty)}");
                        }

                        await gateway.RecordAsync(scope, AuditOutcome.Success, new { req.Service, req.ForceRecreate, operation = ctx.Id }, (long)time.GetElapsedTime(started).TotalMilliseconds, CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        await gateway.RecordAsync(scope, AuditOutcome.Failure, new { req.Service, req.ForceRecreate, operation = ctx.Id, error = e.Message }, (long)time.GetElapsedTime(started).TotalMilliseconds, CancellationToken.None);
                        throw;
                    }
                    finally
                    {
                        containers.Invalidate();
                        Invalidate();
                    }
                },
                version),
            ct);
    }

    private async Task<string?> ComposeVersionAsync(CancellationToken ct)
    {
        if (_version is not null)
        {
            return _version;
        }

        var r = await gateway.RunComposeAsync(ComposeArgv.Version, new ComposeRunOptions(TimeSpan.FromSeconds(15)), ct);
        if (r.Code == 0)
        {
            _version = r.Stdout.Trim();
        }

        return _version;
    }

    /// <summary>Limited-level start/stop/restart through the Engine API (one container at a time, dependency-aware order).</summary>
    private async Task<string> EngineLevelOperationAsync(ComposeProjectSummary summary, string action, string? service, Actor actor, AuditScope scope, CancellationToken ct)
    {
        await gateway.AssertProjectAsync(summary.Name, null, service is not null, ct);
        var projectContainers = await RawContainersAsync(summary.Name, false, ct);
        var selected = projectContainers.Where(c => service is null || c.Service == service).ToList();
        if (selected.Count == 0)
        {
            throw AppException.NotFound("Service");
        }

        var graph = DependencyGraphBuilder.FromLabels(projectContainers.Select(c => c.Labels));
        var order = graph.Order.Count > 0 ? graph.Order : graph.Nodes;
        int Rank(ContainerInfo c) => order.ToList().IndexOf(c.Service ?? string.Empty);
        selected = action == "stop" ? [.. selected.OrderByDescending(Rank)] : [.. selected.OrderBy(Rank)];

        // refuse up-front if any container is not allowed (nothing is half-done)
        foreach (var c in selected)
        {
            await gateway.AssertContainerAsync(new ContainerRef(c.Id, c.Name, summary.Name), ct);
        }

        return await operations.StartAsync(
            new OperationStart(
                $"compose.{action}",
                scope.Target ?? summary.Name,
                actor.Name,
                new { engineLevel = true },
                $"project:{summary.Name}",
                async ctx =>
                {
                    var started = time.GetTimestamp();
                    try
                    {
                        foreach (var c in selected)
                        {
                            ctx.Log($"{action} {c.Name} (Engine API, limited project)\n");
                            await containers.ActAsync(c.Id, action, new ActionRequest(), actor, CancellationToken.None);
                        }

                        await gateway.RecordAsync(scope, AuditOutcome.Success, scope.Params, (long)time.GetElapsedTime(started).TotalMilliseconds, CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        await gateway.RecordAsync(scope, AuditOutcome.Failure, new { error = e.Message }, (long)time.GetElapsedTime(started).TotalMilliseconds, CancellationToken.None);
                        throw;
                    }
                    finally
                    {
                        Invalidate();
                    }
                }),
            ct);
    }

    /// <summary>Recreator: Compose-managed containers are only ever recreated through Compose (RT-12).</summary>
    public async Task<string> RecreateAsync(ResolvedContainer container, ActionRequest request, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(request);
        if (container.Project is null || container.Service is null)
        {
            throw new AppException(409, ErrorCode.Unsupported, "Not a Compose-managed container");
        }

        return await OperateAsync(
            container.Project,
            "redeploy",
            new ComposeActionRequest(container.Service, true, container.Project, request.ApplyDrift, request.ConfirmNamespaceProvider),
            actor,
            ct);
    }

    [GeneratedRegex(@"variable is not set|Defaulting to a blank string", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnsetRe();

    [GeneratedRegex(@"^(\S+)\s+([0-9a-f]{16,})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HashLineRe();
}

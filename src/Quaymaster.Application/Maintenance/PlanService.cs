using System.Text.Json.Nodes;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Registry;
using Quaymaster.Application.Safety;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Compose;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Maintenance;

/// <param name="Project">Compose project.</param>
/// <param name="Services">Restrict to these services (default: every service with an update).</param>
/// <param name="ScheduleMode">Mode of the schedule that triggers the plan (null = manual).</param>
/// <param name="OnlyModes">Only services whose update mode is in this list are candidates (scheduled runs: <c>["auto"]</c>).</param>
/// <param name="ApplyDrift">Proceed although the Compose file differs from what runs.</param>
/// <param name="AllowUnhealthy">Skip the "service is stable" preflight checks.</param>
/// <param name="Fresh">Bypass the registry cache.</param>
public sealed record PlanRequest(
    string Project,
    IReadOnlyList<string>? Services = null,
    string? ScheduleMode = null,
    IReadOnlyList<string>? OnlyModes = null,
    bool ApplyDrift = false,
    bool AllowUnhealthy = false,
    bool Fresh = true);

public interface IPlanService
{
    /// <summary>
    /// Builds the plan (spec 7.5 steps 1, 2, 4, 5: preflight, validate, check, impact). Reads only: nothing is pulled, tagged,
    /// created or restarted. T4b's executor calls this again at its own preflight and compares <see cref="Plan.PlanHash"/>.
    /// </summary>
    Task<Plan> PlanProjectAsync(PlanRequest request, CancellationToken ct);

    /// <summary>POST /maintenance/plan: plans, stores the result as a <c>dry_run</c> run (status <c>planned</c>) and returns it.</summary>
    Task<RunDetailDto> DryRunAsync(PlanRequest request, Actor actor, CancellationToken ct);
}

public sealed class PlannerService(
    IComposeService compose,
    IContainerService containers,
    IUpdateService updates,
    IRegistryClient registry,
    IPolicyResolver policies,
    IQuarantineStore quarantine,
    ISystemService system,
    IRunStore runs,
    IMutationGateway gateway,
    QmConfig config,
    TimeProvider time) : IPlanService
{
    private const long MiB = 1024 * 1024;

    private sealed record Facts(string Name, JsonObject Cfg, IReadOnlyList<ContainerInfo> Containers, ResolvedContainer? Inspect);

    public async Task<RunDetailDto> DryRunAsync(PlanRequest request, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var plan = await PlanProjectAsync(request, ct);
        var id = Guid.NewGuid().ToString();
        var options = new JsonObject();
        if (request.Services is not null)
        {
            options["services"] = new JsonArray([.. request.Services.Select(s => (JsonNode?)JsonValue.Create(s))]);
        }

        options["applyDrift"] = request.ApplyDrift;
        options["allowUnhealthy"] = request.AllowUnhealthy;
        await runs.InsertAsync(
            new NewRun(id, "dry_run", "manual", request.Project, RunStatuses.Planned, plan.Blocked?.Message, plan, actor.Name, options, time.GetUtcNow().ToUnixTimeMilliseconds()),
            ct);
        await gateway.RecordAsync(
            new AuditScope(actor, "maintenance.dry_run", request.Project, null),
            Domain.Auditing.AuditOutcome.Success,
            new { run = id, planHash = plan.PlanHash, blocked = plan.Blocked?.Code },
            null,
            ct);
        return RunDetailDto.From(await runs.GetAsync(id, ct) ?? throw new InvalidOperationException("stored run vanished"));
    }

    public async Task<Plan> PlanProjectAsync(PlanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var req = request;
        var policy = gateway.Policy;
        var createdAt = ImageReferences.Iso(time.GetUtcNow().ToUnixTimeMilliseconds());

        Plan Finish(Plan p) => p with { PlanHash = PlanHasher.Compute(p) };
        Plan Empty(string code, string message, PlanValidation? validation = null) => Finish(new Plan
        {
            Target = req.Project,
            CreatedAt = createdAt,
            Order = [],
            Services = [],
            Excluded = [],
            Preflight = new PlanPreflight(false, [new PreflightCheck(code, false, message)]),
            Validation = validation ?? new PlanValidation(true, [], []),
            Drift = [],
            Impact = new PlanImpact([], []),
            Disk = new PlanDisk(null, null, config.DiskFloorBytes, true),
            Blocked = new PlanBlocked(code, message),
            Warnings = [],
            PlanHash = string.Empty,
        });

        // ---- 1. preflight: capability, scope, self-protection -------------------------------------------------------------
        if (policy.ProjectRefusal(req.Project, null, true) is { } early)
        {
            return Empty("refused", early.Message);
        }

        ComposePlanContext ctx;
        try
        {
            ctx = await compose.PlanContextAsync(req.Project, ct);
        }
        catch (AppException e) when (e.Code == ErrorCode.LimitedCapability)
        {
            return Empty("limited_capability", e.Message);
        }

        // ---- 2. validate (unset variables are errors) --------------------------------------------------------------------
        if (!ctx.Validation.Valid)
        {
            return Empty("validation", $"The Compose configuration is not valid: {(ctx.Validation.Errors.Count > 0 ? ctx.Validation.Errors[0] : "unknown error")}", AsPlan(ctx.Validation));
        }

        if (ctx.Config is null)
        {
            var msg = ctx.ConfigError ?? "The Compose configuration does not resolve";
            return Empty("validation", msg, new PlanValidation(false, [msg], []));
        }

        var cfg = ctx.Config;
        var refs = DependencyGraphBuilder.References(cfg);
        if (policy.ProjectRefusal(req.Project, new ProjectRefs(refs.Volumes, refs.Networks, refs.Containers), true) is { } scope)
        {
            return Empty(CodeOf(scope.Code), scope.Message);
        }

        var graph = DependencyGraphBuilder.Build(cfg);
        if (graph.Cycle is { Count: > 0 })
        {
            return Empty("cycle", $"Dependency cycle: {string.Join(" \u2192 ", graph.Cycle)}");
        }

        // ---- 3. check: facts per service + registry digests --------------------------------------------------------------
        var facts = new Dictionary<string, Facts>(StringComparer.Ordinal);
        foreach (var (name, node) in cfg["services"] as JsonObject ?? [])
        {
            var cs = ctx.Containers
                .Where(c => c.Service == name && c.Labels.GetValueOrDefault(ComposeLabels.OneOff) != "True")
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
            facts[name] = new Facts(name, node as JsonObject ?? [], cs, cs.Count > 0 ? await containers.ResolveAsync(cs[0].Id, ct) : null);
        }

        var requested = req.Services is { Count: > 0 } ? req.Services.ToHashSet(StringComparer.Ordinal) : null;
        if (requested is not null)
        {
            foreach (var r in requested.Where(r => !facts.ContainsKey(r)))
            {
                return Empty("validation", $"Service \"{r}\" does not exist in project {req.Project}");
            }
        }

        var statuses = await updates.StatusesAsync(req.Fresh, ct);
        var excluded = new List<ExcludedService>();
        var warnings = new List<string>();
        var candidates = new Dictionary<string, (ImageUpdateStatusDto Status, EffectivePolicy Policy)>(StringComparer.Ordinal);
        var self = policy.Self;
        foreach (var (name, f) in facts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (requested is not null && !requested.Contains(name))
            {
                continue;
            }

            var reason = ExclusionReason(f, req, policy, self);
            EffectivePolicy? eff = null;
            ImageUpdateStatusDto? status = null;
            if (reason is null)
            {
                eff = await policies.ResolveAsync(req.Project, name, null, ct);
                if (eff.UpdateMode == UpdateModes.Ignore)
                {
                    reason = "policy: ignore";
                }
                else if (req.OnlyModes is not null && !req.OnlyModes.Contains(eff.UpdateMode))
                {
                    reason = $"policy update_mode \"{eff.UpdateMode}\" is not part of this run";
                }
            }

            if (reason is null)
            {
                var first = f.Containers[0];
                if (f.Containers.Select(c => c.ImageId).Distinct(StringComparer.Ordinal).Count() > 1)
                {
                    warnings.Add($"{name}: replicas run different images; they are updated as one unit");
                }

                var image = f.Cfg.Str("image")!;
                status = statuses.FirstOrDefault(s => s.Ref == image && s.UsedBy.Any(u => u.ImageId == first.ImageId)) ?? statuses.FirstOrDefault(s => s.Ref == image);
                if (status is null)
                {
                    reason = "unknown: no update status for this image";
                }
                else if (status.State is not ("update_available" or "update_staged"))
                {
                    reason = status.State == "up_to_date" ? "already up to date" : $"{status.State}{(status.Reason is null ? string.Empty : $": {status.Reason}")}";
                }
                else if (string.IsNullOrEmpty(status.RemoteDigest))
                {
                    reason = "no remote digest known";
                }
            }

            if (reason is not null)
            {
                excluded.Add(new ExcludedService(name, reason));
                continue;
            }

            candidates[name] = (status!, eff!);
        }

        // ---- 4. impact: namespace / volume dependents are recreated in the same step (RT-11) ------------------------------
        var selected = candidates.Keys.ToHashSet(StringComparer.Ordinal);
        var dependentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var e in graph.Edges.Where(e => e.Kind != "depends_on"))
            {
                if ((selected.Contains(e.To) || dependentOf.ContainsKey(e.To)) && !selected.Contains(e.From) && !dependentOf.ContainsKey(e.From)
                    && facts.TryGetValue(e.From, out var df) && df.Containers.Count > 0)
                {
                    dependentOf[e.From] = dependentOf.GetValueOrDefault(e.To, e.To);
                    grew = true;

                    // F-02: a dependent is recreated with its provider, so it must pass the same fence as a selected service
                    if (DependentRefusal(df, req, policy, self) is { } refusal)
                    {
                        return Empty("refused", $"{e.From} would be recreated together with {dependentOf[e.From]} ({e.Kind}) but is refused: {refusal}");
                    }
                }
            }
        }

        var nodes = selected.Select(n => new OrderNode(n, candidates[n].Policy.UpdateOrder))
            .Concat(dependentOf.Keys.Select(n => new OrderNode(n, null)))
            .ToList();
        var (order, cycle) = ServiceOrdering.Order(nodes, graph.Edges);
        if (cycle is not null)
        {
            return Empty("cycle", $"Cannot order services: {string.Join(", ", cycle)}");
        }

        var services = new List<PlanService>();
        foreach (var name in order)
        {
            var f = facts[name];
            var isUpdate = candidates.TryGetValue(name, out var cand);
            var eff = isUpdate ? cand.Policy : await policies.ResolveAsync(req.Project, name, null, ct);
            services.Add(await BuildServiceAsync(f, isUpdate, isUpdate ? cand.Status : null, eff, dependentOf.GetValueOrDefault(name), graph, req.ScheduleMode, ct));
        }

        // config hashes (RT-01) for every service in the plan, then the drift gate
        IReadOnlyList<DriftEntry> drift = [];
        if (services.Count > 0)
        {
            try
            {
                var names = services.Select(s => s.Service).ToList();
                var check = await compose.HashesAndDriftAsync(req.Project, names, ct);
                services = [.. services.Select(s => s with { ConfigHash = check.Hashes.GetValueOrDefault(s.Service) })];
                drift = check.Drift;
            }
            catch (AppException e) when (e.Code == ErrorCode.ComposeFailed)
            {
                return Empty("validation", e.Message);
            }
        }

        // impact: depends_on dependents (RT-29) and every container on the host that references the same tag (RT-07)
        var inRun = services.Select(s => s.Service).ToHashSet(StringComparer.Ordinal);
        var dependents = new List<ImpactDependent>();
        foreach (var s in services.Where(x => x.IsUpdate))
        {
            foreach (var e in graph.Edges.Where(e => e.To == s.Service))
            {
                if (e.Kind != "depends_on" || !inRun.Contains(e.From))
                {
                    dependents.Add(new ImpactDependent(e.From, s.Service, e.Kind));
                }
            }
        }

        var sharedTag = new List<SharedTag>();
        var myRefs = services.Where(s => s.IsUpdate).GroupBy(s => KeyOf(s.Ref), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var sharing = new List<(ContainerInfo C, string? Project)>();
        var hidden = 0;
        // RT4-04: count EVERY container on the daemon, also ones hidden by the visible scope or the "stopped monitoring" list
        foreach (var c in await containers.ListEverythingFreshAsync(ct))
        {
            if (!myRefs.ContainsKey(KeyOf(c.Image)))
            {
                continue;
            }

            if (c.Project == req.Project && c.Service is not null && inRun.Contains(c.Service))
            {
                continue;
            }

            sharing.Add((c, c.Project));
            if (!policy.ContainerVisible(c.Name, c.Project))
            {
                hidden++; // never named in the plan: the operator may not see it
                continue;
            }

            sharedTag.Add(new SharedTag(c.Image, c.Name, c.Project, c.Service, false,
                "references the same tag: it will deploy the new image the next time anything recreates it (the tag moves during this run)"));
        }

        if (hidden > 0)
        {
            warnings.Add($"{hidden} container(s) outside the visible scope also use this tag and will run the new image when recreated");
        }

        // RT-03: the tag moves for EVERY container that uses it; never for ones outside the managed scope (or protected ones)
        var checks = new List<PreflightCheck>();
        var fenced = new List<string>();
        var fencedHidden = 0;
        foreach (var (c, project) in sharing)
        {
            if (policy.ContainerRefusal(new ContainerRef(c.Id, c.Name, project)) is not { } r || r.Code == RefusalCode.ReadOnly)
            {
                continue;
            }

            if (policy.ContainerVisible(c.Name, project))
            {
                fenced.Add(c.Name);
            }
            else
            {
                fencedHidden++;
            }
        }

        var all = fenced.Concat(fencedHidden > 0 ? [$"{fencedHidden} container(s) outside the visible scope"] : []).ToList();
        checks.Add(new PreflightCheck(
            "containers sharing the image tag are within the managed scope",
            all.Count == 0,
            all.Count == 0 ? "ok" : $"{string.Join(", ", all)} use the same tag but are outside QM_MANAGED_SCOPE or protected; moving the tag would change what they run on their next recreate"));

        if (!req.AllowUnhealthy)
        {
            foreach (var s in services)
            {
                var bad = facts[s.Service].Containers.FirstOrDefault(c => c.State is "restarting" or "dead" || c.Status.Contains("unhealthy", StringComparison.Ordinal));
                checks.Add(new PreflightCheck($"service {s.Service} is stable", bad is null, bad is null ? "ok" : $"container {bad.Name} is {bad.State} ({bad.Status})"));
            }
        }

        var disk = await DiskCheckAsync(services, ct);
        checks.Add(new PreflightCheck(
            "free disk space on the Docker root",
            disk.Ok,
            disk.FreeBytes is null
                ? "free space cannot be determined (container mode needs QM_DOCKER_ROOT_MOUNT); failing closed"
                : $"{disk.FreeBytes / MiB} MiB free, need >= {((disk.RequiredBytes ?? 0) + disk.FloorBytes) / MiB} MiB (2.5 x download + floor)"));
        var preflightOk = checks.All(c => c.Ok);

        PlanBlocked? blocked = null;
        if (!preflightOk)
        {
            var failed = checks.First(c => !c.Ok);
            blocked = new PlanBlocked("preflight", $"Preflight failed: {failed.Name} \u2014 {failed.Detail}");
        }
        else if (drift.Count > 0 && !req.ApplyDrift)
        {
            blocked = new PlanBlocked(
                "blocked_drift",
                $"The Compose configuration differs from what runs for: {string.Join(", ", drift.Select(d => d.Service))}. An update would also apply those changes. Review the redacted configuration and re-plan with applyDrift to proceed manually.");
        }
        else if (services.Count == 0)
        {
            blocked = new PlanBlocked("nothing_to_do", "No service has an available update");
        }

        if (drift.Count > 0 && req.ApplyDrift)
        {
            warnings.Add($"Configuration drift will be applied for: {string.Join(", ", drift.Select(d => d.Service))}");
        }

        return Finish(new Plan
        {
            Target = req.Project,
            CreatedAt = createdAt,
            Order = order,
            Services = services,
            Excluded = excluded,
            Preflight = new PlanPreflight(preflightOk, checks),
            Validation = AsPlan(ctx.Validation),
            Drift = [.. drift.Select(d => new PlanDrift(d.Service, d.Running, d.Wanted))],
            Impact = new PlanImpact(dependents, sharedTag),
            Disk = disk,
            Blocked = blocked,
            Warnings = warnings,
            PlanHash = string.Empty,
        });
    }

    private static PlanValidation AsPlan(ValidationResult v) => new(v.Valid, v.Errors, v.Warnings);

    private static string CodeOf(RefusalCode c) => c switch
    {
        RefusalCode.ReadOnly => "read_only",
        RefusalCode.OutOfScope => "out_of_scope",
        _ => "self_protected",
    };

    /// <summary>Why a service cannot be a candidate (null = it can); the policy and registry checks come afterwards.</summary>
    private static string? DependentRefusal(Facts f, PlanRequest req, SafetyPolicy policy, SelfInfo self)
    {
        if (f.Containers.Any(c => self.ContainerId is not null && c.Id == self.ContainerId))
        {
            return "this is the Quaymaster container itself; it never recreates itself";
        }

        return f.Containers
            .Select(c => policy.ContainerRefusal(new ContainerRef(c.Id, c.Name, req.Project)))
            .FirstOrDefault(r => r is not null)?.Message;
    }

    private static string? ExclusionReason(Facts f, PlanRequest req, SafetyPolicy policy, SelfInfo self)
    {
        if (f.Containers.Count == 0)
        {
            return "no container exists yet (not created)";
        }

        if (f.Cfg.ContainsKey("build"))
        {
            return "built from source (`build:`); only registry images are updatable (RT-06)";
        }

        if (string.IsNullOrEmpty(f.Cfg.Str("image")))
        {
            return "no image reference";
        }

        if (f.Containers.Any(c => self.ContainerId is not null && c.Id == self.ContainerId))
        {
            return "this is the Quaymaster container itself; it never updates itself";
        }

        return f.Containers
            .Select(c => policy.ContainerRefusal(new ContainerRef(c.Id, c.Name, req.Project)))
            .FirstOrDefault(r => r is not null)?.Message;
    }

    private async Task<PlanService> BuildServiceAsync(
        Facts f, bool isUpdate, ImageUpdateStatusDto? status, EffectivePolicy eff, string? dependentOf, DependencyGraph graph, string? scheduleMode, CancellationToken ct)
    {
        var first = f.Containers[0];
        var raw = f.Inspect?.Raw;
        var mounts = (raw?["Mounts"] as JsonArray).Objects()
            .Select(m => new PlanMount(m.Str("Type") ?? string.Empty, m.Str("Name") ?? m.Str("Source") ?? string.Empty, m.Str("Destination") ?? string.Empty, m["RW"].Bool() == false))
            .ToList();
        var persistent = mounts.Where(m => !m.ReadOnly && m.Type is "volume" or "bind").ToList();
        var rb = PolicyRules.ResolveAutoRollback(eff.AutoRollbackExplicit, persistent.Count > 0, eff.AutoRollbackBroad);

        var hc = raw.Obj("Config").Obj("Healthcheck");
        var test = hc.Arr("Test");
        var hasHealthcheck = test is { Count: > 0 } && test[0].Str() != "NONE";
        HealthTimeouts.HealthcheckTimings? timings = hasHealthcheck
            ? new HealthTimeouts.HealthcheckTimings((hc.Num("Interval") ?? 30_000_000_000) / 1e6, (int)(hc.Num("Retries") ?? 3), (hc.Num("StartPeriod") ?? 0) / 1e6)
            : null;
        var oneShot = graph.Edges.Any(e => e.To == f.Name && e.Condition == "service_completed_successfully");
        var wasRunning = f.Containers.All(c => c.State == "running");
        var timeout = HealthTimeouts.EffectiveSeconds(eff.Health.TimeoutSeconds, timings);

        var image = f.Cfg.Str("image") ?? first.Image;
        var quarantined = !string.IsNullOrEmpty(status?.RemoteDigest) && await quarantine.FindAsync(KeyOf(image), status.RemoteDigest, ct) is { ClearedAt: null };

        // RT-11/23: a namespace dependent is recreated too, so its own critical / require_approval flags count; its update mode is irrelevant
        var blockers = PolicyRules.UnattendedBlockers(isUpdate ? eff.UpdateMode : UpdateModes.Auto, eff.Critical, eff.RequireApproval, scheduleMode);
        if (quarantined)
        {
            blockers.Add("this digest failed before and is quarantined (RT-22)");
        }

        if (!wasRunning)
        {
            blockers.Add("the service is currently stopped");
        }

        return new PlanService
        {
            Service = f.Name,
            Role = isUpdate ? "update" : "dependent",
            DependentOf = isUpdate ? null : dependentOf,
            Containers = [.. f.Containers.Select(c => new PlanContainer(c.Id, c.Name))],
            Ref = image,
            FromImageId = first.ImageId,
            FromDigest = status?.LocalDigest,
            ToDigest = status?.RemoteDigest,
            ConfigHash = null,
            WasRunning = wasRunning,
            OneShot = oneShot,
            AutoRollback = rb.Value,
            AutoRollbackReason = rb.Reason,
            Health = new PlanHealth(oneShot ? "oneshot" : hasHealthcheck ? "healthcheck" : "stability", timeout, eff.Health.StabilizationSeconds, eff.Health.Probe),
            Downtime = wasRunning
                ? $"brief downtime expected: the container is recreated (stop, create, start, then up to {timeout}s health gate)"
                : "none: the service is stopped and stays stopped (updated with --no-start, RT-28)",
            DataRisk = new DataRisk(
                persistent.Count > 0,
                mounts,
                persistent.Count > 0
                    ? "Persistent writable mounts: a rollback restores the image, not the data. A new version may have migrated it; take a backup first."
                    : "No persistent writable mounts."),
            Critical = eff.Critical,
            RequireApproval = eff.RequireApproval,
            UpdateMode = eff.UpdateMode,
            Unattended = new Unattended(blockers.Count == 0, blockers),
            Quarantined = quarantined,
        };
    }

    private static string KeyOf(string reference) => ImageReferences.Parse(reference) is { } r ? ImageReferences.CacheKey(r) : reference;

    /// <summary>RT-13: free >= 2.5 x sum of compressed sizes + max(floor, 10 % of the filesystem); fails closed if unknown.</summary>
    private async Task<PlanDisk> DiskCheckAsync(IReadOnlyList<PlanService> services, CancellationToken ct)
    {
        HostRootDto? root = null;
        try
        {
            root = await system.DockerRootSpaceAsync(ct);
        }
        catch (Exception e) when (e is IOException or AppException)
        {
            // unknown: fail closed below
        }

        var floor = Math.Max(config.DiskFloorBytes, root is null ? 0 : (long)Math.Floor(root.Total * 0.1));
        long? required = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in services.Where(x => x.IsUpdate && x.ToDigest is not null))
        {
            var r = ImageReferences.Parse(s.Ref);
            if (r is null || !seen.Add($"{ImageReferences.CacheKey(r)}@{s.ToDigest}"))
            {
                continue;
            }

            try
            {
                required += (long)Math.Ceiling(2.5 * await registry.CompressedSizeAsync(r, s.ToDigest!, ct));
            }
            catch (Exception e) when (e is AppException or HttpRequestException or TaskCanceledException)
            {
                required = null; // unknown: reported, the floor still applies
                break;
            }
        }

        return new PlanDisk(required, root?.Free, floor, root is not null && root.Free >= (required ?? 0) + floor);
    }
}

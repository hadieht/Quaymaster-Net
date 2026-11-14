using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Maintenance;

public static class UpdateModes
{
    public const string Ignore = "ignore";
    public const string Notify = "notify";
    public const string PullOnly = "pull_only";
    public const string Auto = "auto";

    public static readonly IReadOnlyList<string> All = [Ignore, Notify, PullOnly, Auto];
}

public sealed record ProbeSpec(string Url, int ExpectStatus);

/// <summary>The health gate settings of a target. <see cref="Probe"/> is an optional HTTP probe run from the server.</summary>
public sealed record HealthPolicy(double TimeoutSeconds, double StabilizationSeconds, ProbeSpec? Probe);

/// <summary>Health fields stored at one level; <see cref="ProbeSet"/> tells "probe: null" (clear) from "unset" (inherit).</summary>
public sealed record HealthInput(double? TimeoutSeconds, double? StabilizationSeconds, bool ProbeSet, ProbeSpec? Probe);

/// <summary>What is stored at one level. Every field is optional: unset = inherit.</summary>
public sealed record PolicyInput
{
    public string? UpdateMode { get; init; }

    public bool? Critical { get; init; }

    public bool? RequireApproval { get; init; }

    public bool? AutoRollback { get; init; }

    public HealthInput? Health { get; init; }

    public int? MaxParallel { get; init; }

    public long? UpdateOrder { get; init; }
}

/// <summary>One level of the policy chain: <c>"global:*"</c>, <c>"project:media"</c>, <c>"service:media/web"</c>, <c>"container:nginx"</c>.</summary>
public sealed record PolicyLevel(string Level, PolicyInput Policy);

public sealed record EffectivePolicy(
    string UpdateMode,
    bool Critical,
    bool RequireApproval,
    bool? AutoRollbackExplicit,
    bool AutoRollbackBroad,
    HealthPolicy Health,
    int MaxParallel,
    long? UpdateOrder,
    IReadOnlyDictionary<string, string> Sources);

public sealed record AutoRollbackDecision(bool Value, string Reason);

public sealed class PolicyValidationException(string message) : Exception(message);

public static partial class PolicyRules
{
    public static readonly HealthPolicy DefaultHealth = new(60, 15, null);

    public const string DefaultUpdateMode = UpdateModes.Notify;

    /// <summary>
    /// Resolves the effective policy (RT-23). <paramref name="levels"/> is ordered least to most specific.
    /// Safety flags combine as the MOST RESTRICTIVE across all levels (<c>critical</c> = OR, <c>requireApproval</c> = OR,
    /// <c>autoRollback</c> = AND: an explicit false anywhere wins); every other field: the most specific level that sets it wins.
    /// Global default: notify + approval required (automation is opt-in).
    /// </summary>
    public static EffectivePolicy Resolve(IEnumerable<PolicyLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["updateMode"] = "default",
            ["critical"] = "default",
            ["requireApproval"] = "default",
            ["autoRollback"] = "default",
        };
        var updateMode = DefaultUpdateMode;
        var critical = false;
        var requireApproval = true;
        var approvalDemanded = false;
        bool? autoRollback = null;
        var broad = false;
        long? updateOrder = null;
        var timeout = DefaultHealth.TimeoutSeconds;
        var stab = DefaultHealth.StabilizationSeconds;
        var probe = DefaultHealth.Probe;

        foreach (var (level, p) in levels)
        {
            if (p.UpdateMode is not null)
            {
                updateMode = p.UpdateMode;
                sources["updateMode"] = level;
            }

            if (p.Critical == true)
            {
                critical = true;
                sources["critical"] = level;
            }

            if (p.RequireApproval == true)
            {
                requireApproval = true;
                approvalDemanded = true;
                sources["requireApproval"] = level;
            }
            else if (p.RequireApproval == false && !approvalDemanded)
            {
                requireApproval = false;
                sources["requireApproval"] = level;
            }

            if (p.AutoRollback == false)
            {
                autoRollback = false;
                sources["autoRollback"] = level;
            }
            else if (p.AutoRollback == true)
            {
                // F-09 (RT-10): only a service/container-level true may override the stateful default; a project/global true is a
                // convenience that covers stateless services only
                if (autoRollback is null && (level.StartsWith("service:", StringComparison.Ordinal) || level.StartsWith("container:", StringComparison.Ordinal)))
                {
                    autoRollback = true;
                    sources["autoRollback"] = level;
                }
                else if (autoRollback is null)
                {
                    broad = true;
                    sources["autoRollbackBroad"] = level;
                }
            }

            if (p.UpdateOrder is { } order)
            {
                updateOrder = order;
                sources["updateOrder"] = level;
            }

            if (p.Health is { } h)
            {
                timeout = h.TimeoutSeconds ?? timeout;
                stab = h.StabilizationSeconds ?? stab;
                if (h.ProbeSet)
                {
                    probe = h.Probe;
                }

                sources["health"] = level;
            }
        }

        return new EffectivePolicy(updateMode, critical, requireApproval, autoRollback, broad, new HealthPolicy(timeout, stab, probe), 1, updateOrder, sources);
    }

    /// <summary>
    /// The unattended-deploy truth table (RT-23): a deploy without a human happens only if
    /// <c>update_mode = auto ∧ schedule.mode = auto ∧ ¬require_approval ∧ ¬critical</c>. Returns the reasons it is NOT allowed (empty = allowed).
    /// </summary>
    public static List<string> UnattendedBlockers(string updateMode, bool critical, bool requireApproval, string? scheduleMode)
    {
        var blockers = new List<string>();
        if (updateMode != UpdateModes.Auto)
        {
            blockers.Add($"update_mode is \"{updateMode}\", not \"auto\"");
        }

        if (scheduleMode != "auto")
        {
            blockers.Add("the schedule is not in auto mode");
        }

        if (requireApproval)
        {
            blockers.Add("approval is required");
        }

        if (critical)
        {
            blockers.Add("the target is critical (always needs explicit per-run approval)");
        }

        return blockers;
    }

    /// <summary>Rollback default for a service (RT-10): off when it has writable persistent mounts.</summary>
    public static AutoRollbackDecision ResolveAutoRollback(bool? explicitValue, bool hasPersistentMounts, bool broadTrue = false)
    {
        if (explicitValue == false)
        {
            return new AutoRollbackDecision(false, "disabled by policy");
        }

        if (explicitValue == true)
        {
            return new AutoRollbackDecision(true, "enabled by policy");
        }

        if (broadTrue && !hasPersistentMounts)
        {
            return new AutoRollbackDecision(true, "enabled by the project/global policy (stateless service)");
        }

        return hasPersistentMounts
            ? new AutoRollbackDecision(false, "default is OFF: the service has writable persistent mounts, and a rollback could start old code on migrated data (the rollout stops, the service is left as is, and you are alerted)")
            : new AutoRollbackDecision(true, "default is ON for a stateless service");
    }

    // ---- storage / wire codec -----------------------------------------------------------------------------------

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "updateMode", "critical", "requireApproval", "autoRollback", "health", "maxParallel", "updateOrder",
    };

    private static PolicyValidationException Bad(string m) => new(m);

    /// <summary>Validates a raw policy body exactly like the original and returns the typed form.</summary>
    public static PolicyInput Parse(JsonNode? body)
    {
        if (body is not JsonObject o)
        {
            throw Bad("policy must be an object");
        }

        foreach (var k in o.Select(kv => kv.Key))
        {
            if (!Allowed.Contains(k))
            {
                throw Bad($"unknown policy field \"{k}\"");
            }
        }

        string? mode = null;
        if (o.ContainsKey("updateMode"))
        {
            mode = o["updateMode"] is JsonValue mv && mv.TryGetValue<string>(out var ms) && UpdateModes.All.Contains(ms)
                ? ms
                : throw Bad("updateMode must be ignore|notify|pull_only|auto");
        }

        bool? Flag(string key)
        {
            if (!o.ContainsKey(key))
            {
                return null;
            }

            return o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw Bad($"{key} must be a boolean");
        }

        var critical = Flag("critical");
        var approval = Flag("requireApproval");
        var rollback = Flag("autoRollback");
        int? maxParallel = null;
        if (o.ContainsKey("maxParallel"))
        {
            maxParallel = Number(o["maxParallel"]) == 1 ? 1 : throw Bad("maxParallel is fixed to 1 in this version");
        }

        long? order = null;
        if (o.ContainsKey("updateOrder"))
        {
            var n = Number(o["updateOrder"]);
            order = n is { } d && d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (long)d : throw Bad("updateOrder must be an integer");
        }

        HealthInput? health = null;
        if (o.ContainsKey("health"))
        {
            health = ParseHealth(o["health"]);
        }

        return new PolicyInput
        {
            UpdateMode = mode,
            Critical = critical,
            RequireApproval = approval,
            AutoRollback = rollback,
            MaxParallel = maxParallel,
            UpdateOrder = order,
            Health = health,
        };
    }

    private static double? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static HealthInput ParseHealth(JsonNode? node)
    {
        if (node is not JsonObject h)
        {
            throw Bad("health must be an object");
        }

        double? timeout = null;
        double? stab = null;
        var probeSet = false;
        ProbeSpec? probe = null;
        if (h.ContainsKey("timeoutSeconds"))
        {
            var v = Number(h["timeoutSeconds"]);
            timeout = v is >= 5 and <= 3600 ? v : throw Bad("health.timeoutSeconds must be 5..3600");
        }

        if (h.ContainsKey("stabilizationSeconds"))
        {
            var v = Number(h["stabilizationSeconds"]);
            stab = v is >= 0 and <= 600 ? v : throw Bad("health.stabilizationSeconds must be 0..600");
        }

        if (h.ContainsKey("probe"))
        {
            probeSet = true;
            if (h["probe"] is not null)
            {
                if (h["probe"] is not JsonObject pr
                    || pr["url"] is not JsonValue uv || !uv.TryGetValue<string>(out var url) || !HttpUrlRe().IsMatch(url))
                {
                    throw Bad("health.probe.url must be http(s)");
                }

                var st = pr.ContainsKey("expectStatus") && pr["expectStatus"] is not null ? Number(pr["expectStatus"]) : 200;
                if (st is not { } sd || sd != Math.Floor(sd) || sd < 100 || sd > 599)
                {
                    throw Bad("health.probe.expectStatus invalid");
                }

                probe = new ProbeSpec(url, (int)sd);
            }
        }

        return new HealthInput(timeout, stab, probeSet, probe);
    }

    /// <summary>The stored / wire form: only the fields that are set.</summary>
    public static JsonObject ToJson(PolicyInput p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var o = new JsonObject();
        if (p.UpdateMode is not null)
        {
            o["updateMode"] = p.UpdateMode;
        }

        if (p.Critical is { } c)
        {
            o["critical"] = c;
        }

        if (p.RequireApproval is { } r)
        {
            o["requireApproval"] = r;
        }

        if (p.AutoRollback is { } a)
        {
            o["autoRollback"] = a;
        }

        if (p.MaxParallel is { } m)
        {
            o["maxParallel"] = m;
        }

        if (p.UpdateOrder is { } u)
        {
            o["updateOrder"] = u;
        }

        if (p.Health is { } h)
        {
            var ho = new JsonObject();
            if (h.TimeoutSeconds is { } t)
            {
                ho["timeoutSeconds"] = t;
            }

            if (h.StabilizationSeconds is { } s)
            {
                ho["stabilizationSeconds"] = s;
            }

            if (h.ProbeSet)
            {
                ho["probe"] = h.Probe is null ? null : new JsonObject { ["url"] = h.Probe.Url, ["expectStatus"] = h.Probe.ExpectStatus };
            }

            o["health"] = ho;
        }

        return o;
    }

    // ---- scope keys -----------------------------------------------------------------------------------------------

    public static string ServiceKey(string project, string service) => $"{project}/{service}";

    /// <summary>The (kind, key) pairs consulted for a target, least to most specific.</summary>
    public static List<(string Kind, string Key)> ChainFor(string? project, string? service, string? container)
    {
        var keys = new List<(string, string)> { ("global", "*") };
        if (!string.IsNullOrEmpty(project))
        {
            keys.Add(("project", project));
        }

        if (!string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(service))
        {
            keys.Add(("service", ServiceKey(project, service)));
        }

        if (string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(container))
        {
            keys.Add(("container", container));
        }

        return keys;
    }

    [GeneratedRegex(@"^https?://", RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrlRe();
}

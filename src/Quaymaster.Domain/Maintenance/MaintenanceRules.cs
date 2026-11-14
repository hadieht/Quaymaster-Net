using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quaymaster.Domain.Compose;

namespace Quaymaster.Domain.Maintenance;

/// <summary>Stable JSON (sorted keys) so equal plans hash equally.</summary>
public static class CanonicalJson
{
    public static string Write(JsonNode? node)
    {
        var sb = new StringBuilder();
        Append(sb, node);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var key in o.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append(JsonSerializer.Serialize(key)).Append(':');
                    Append(sb, o[key]);
                }

                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    Append(sb, a[i]);
                }

                sb.Append(']');
                break;
            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }
}

/// <summary>
/// RT-08: the approval is bound to this hash - service list and order, per-service from-image ID, to-digest and config hash, and the
/// policy snapshot. Volatile data (timestamps, disk numbers) is excluded.
/// </summary>
public static class PlanHasher
{
    public static string Compute(Plan p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var services = new JsonArray();
        foreach (var s in p.Services)
        {
            services.Add(new JsonObject
            {
                ["service"] = s.Service,
                ["role"] = s.Role,
                ["ref"] = s.Ref,
                ["fromImageId"] = s.FromImageId,
                ["toDigest"] = s.ToDigest,
                ["configHash"] = s.ConfigHash,
                ["wasRunning"] = s.WasRunning,
                ["policy"] = new JsonObject
                {
                    ["updateMode"] = s.UpdateMode,
                    ["critical"] = s.Critical,
                    ["requireApproval"] = s.RequireApproval,
                    ["autoRollback"] = s.AutoRollback,
                    ["health"] = new JsonObject
                    {
                        ["kind"] = s.Health.Kind,
                        ["timeoutSeconds"] = s.Health.TimeoutSeconds,
                        ["stabilizationSeconds"] = s.Health.StabilizationSeconds,
                        ["probe"] = s.Health.Probe is null ? null : new JsonObject { ["url"] = s.Health.Probe.Url, ["expectStatus"] = s.Health.Probe.ExpectStatus },
                    },
                },
            });
        }

        var material = new JsonObject
        {
            ["kind"] = p.Kind,
            ["target"] = p.Target,
            ["order"] = new JsonArray([.. p.Order.Select(x => (JsonNode?)JsonValue.Create(x))]),
            ["services"] = services,
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Write(material))));
    }

    /// <summary>Explains why two plans differ (for the approval_stale message).</summary>
    public static string DescribeDiff(Plan approved, Plan current)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(current);
        var output = new List<string>();
        var ma = approved.Services.ToDictionary(s => s.Service, StringComparer.Ordinal);
        var mb = current.Services.ToDictionary(s => s.Service, StringComparer.Ordinal);
        foreach (var (name, s) in ma)
        {
            if (!mb.TryGetValue(name, out var t))
            {
                output.Add($"{name} is no longer part of the plan");
                continue;
            }

            if (t.ToDigest != s.ToDigest)
            {
                output.Add($"{name}: target digest moved");
            }

            if (t.FromImageId != s.FromImageId)
            {
                output.Add($"{name}: running image changed");
            }

            if (t.ConfigHash != s.ConfigHash)
            {
                output.Add($"{name}: configuration changed");
            }
        }

        output.AddRange(mb.Keys.Where(n => !ma.ContainsKey(n)).Select(n => $"{n} was added"));
        return output.Count > 0 ? string.Join("; ", output) : "service order or policy changed";
    }
}

public sealed record OrderNode(string Name, long? Order);

/// <summary>Dependency-aware order (Kahn's algorithm) with the policy <c>updateOrder</c> hint and name as deterministic tie-breakers.</summary>
public static class ServiceOrdering
{
    /// <summary>
    /// A service always comes after everything in <paramref name="selected"/> it depends on (any edge kind), also through services
    /// outside the selection. Returns the cycle members when the selection cannot be ordered.
    /// </summary>
    public static (IReadOnlyList<string> Order, IReadOnlyList<string>? Cycle) Order(IReadOnlyList<OrderNode> selected, IReadOnlyList<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(edges);
        var names = selected.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        var hint = selected.ToDictionary(n => n.Name, n => n.Order, StringComparer.Ordinal);
        var deps = names.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var direct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (!direct.TryGetValue(e.From, out var set))
            {
                direct[e.From] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(e.To);
        }

        foreach (var n in names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(direct.GetValueOrDefault(n) ?? []);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (!seen.Add(x))
                {
                    continue;
                }

                if (names.Contains(x) && x != n)
                {
                    deps[n].Add(x);
                }
                else
                {
                    foreach (var y in direct.GetValueOrDefault(x) ?? [])
                    {
                        stack.Push(y);
                    }
                }
            }
        }

        int Cmp(string a, string b)
        {
            var ha = hint.GetValueOrDefault(a) ?? long.MaxValue;
            var hb = hint.GetValueOrDefault(b) ?? long.MaxValue;
            return ha != hb ? ha.CompareTo(hb) : string.Compare(a, b, StringComparison.Ordinal);
        }

        var order = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (done.Count < names.Count)
        {
            var ready = names.Where(n => !done.Contains(n) && deps[n].All(done.Contains)).ToList();
            if (ready.Count == 0)
            {
                return ([], [.. names.Where(n => !done.Contains(n)).OrderBy(n => n, StringComparer.Ordinal)]);
            }

            ready.Sort(Cmp);
            order.Add(ready[0]);
            done.Add(ready[0]);
        }

        return (order, null);
    }
}

/// <summary>Compose duration parsing and the RT-10 health-timeout rule.</summary>
public static partial class HealthTimeouts
{
    public sealed record HealthcheckTimings(double IntervalMs, int Retries, double StartPeriodMs);

    /// <summary>Parses a Compose duration ("1m30s", "500ms", "2s") into milliseconds.</summary>
    public static double? ParseDurationMs(string? v)
    {
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        double total = 0;
        var matched = false;
        foreach (Match m in DurationRe().Matches(v))
        {
            matched = true;
            var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += n * m.Groups[2].Value switch
            {
                "h" => 3_600_000d,
                "m" => 60_000d,
                "s" => 1000d,
                "ms" => 1d,
                "us" or "µs" => 0.001,
                "ns" => 1e-6,
                _ => 0d,
            };
        }

        return matched ? total : null;
    }

    /// <summary>RT-10: effective timeout = max(policy, start_period + interval × retries + 30 s).</summary>
    public static double EffectiveSeconds(double policySeconds, HealthcheckTimings? hc)
    {
        if (hc is null)
        {
            return policySeconds;
        }

        var own = Math.Ceiling((hc.StartPeriodMs + (hc.IntervalMs * hc.Retries)) / 1000) + 30;
        return Math.Max(policySeconds, own);
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|us|µs|ns|s|m|h)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRe();
}

/// <summary>What to do after a service's deploy + health gate (spec 7.5 steps 8-9). Pure, so the decision table is unit-tested.</summary>
public sealed record FailureDecision(string Outcome, bool Quarantine, bool RestoreTag, bool AttemptRollback);

public static class RollbackRules
{
    /// <summary>
    /// <list type="bullet">
    /// <item>healthy: nothing to undo.</item>
    /// <item>L-3 (RT-22): only an image that actually started and failed its health gate is quarantined; a failed <c>compose up</c> is as
    /// likely a configuration problem as an image problem.</item>
    /// <item>RT-07 / F-08: when the update did not stick and no rollback redeploys the old image (auto-rollback off, or the deploy itself
    /// failed) the shared tag is put back on the previous image so a later <c>compose up</c> does not redeploy the failing one.</item>
    /// <item>Auto-rollback only applies to the updated service, never to a namespace dependent; with it off the rollout stops and the service is left as is.</item>
    /// </list>
    /// </summary>
    public static FailureDecision Decide(bool isUpdate, bool deployOk, bool healthy, bool autoRollback)
    {
        if (healthy)
        {
            return new FailureDecision(isUpdate ? ServiceOutcomes.Updated : ServiceOutcomes.DependentRecreated, false, false, false);
        }

        var quarantine = isUpdate && deployOk;
        var restoreTag = isUpdate && (!autoRollback || !deployOk);
        var rollback = isUpdate && autoRollback;
        return new FailureDecision(rollback ? ServiceOutcomes.RolledBack : ServiceOutcomes.FailedLeftAsIs, quarantine, restoreTag, rollback);
    }

    /// <summary>The service outcome once the rollback ran: a failed rollback is a plain failure with manual recovery guidance.</summary>
    public static string AfterRollback(bool rollbackOk) => rollbackOk ? ServiceOutcomes.RolledBack : ServiceOutcomes.Failed;

    /// <summary>The run status for a failed service outcome.</summary>
    public static string RunStatusFor(string outcome) => outcome == ServiceOutcomes.RolledBack ? RunStatuses.RolledBack : RunStatuses.Failed;
}

/// <summary>Counts container deaths in a sliding window (restart-loop detection). Not thread-safe: the owner locks.</summary>
public sealed class RestartLoopTracker
{
    private readonly Dictionary<string, List<long>> _died = new(StringComparer.Ordinal);

    /// <summary>Records a death at <paramref name="nowMs"/> and returns how many fall inside the window (including this one).</summary>
    public int Record(string containerId, long nowMs, long windowMs)
    {
        ArgumentNullException.ThrowIfNull(containerId);
        var list = (_died.GetValueOrDefault(containerId) ?? []).Where(t => nowMs - t < windowMs).ToList();
        list.Add(nowMs);
        _died[containerId] = list;
        return list.Count;
    }

    public static bool IsLoop(int deaths, int threshold) => deaths >= threshold;
}

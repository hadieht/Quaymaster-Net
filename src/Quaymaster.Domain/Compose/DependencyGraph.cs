using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Compose;

public sealed record GraphEdge(
    string From,
    string To,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Condition = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Restart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Required = null);

public sealed record DependencyGraph(IReadOnlyList<string> Nodes, IReadOnlyList<GraphEdge> Edges, IReadOnlyList<string> Order, IReadOnlyList<string>? Cycle);

public sealed record ProjectReferences(IReadOnlyList<string> Volumes, IReadOnlyList<string> Networks, IReadOnlyList<string> Containers);

/// <summary>Service dependency graph over a resolved Compose config (port of <c>compose/graph.ts</c>).</summary>
public static partial class DependencyGraphBuilder
{
    /// <summary>Builds the graph including namespace-sharing and volumes_from edges (RT-11).</summary>
    public static DependencyGraph Build(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var services = config["services"] as JsonObject ?? [];
        var nodes = services.Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var edges = new List<GraphEdge>();

        void Add(string from, string to, string kind, string? condition = null, bool? restart = null, bool? required = null)
        {
            if (from != to)
            {
                edges.Add(new GraphEdge(from, to, kind, condition, restart, required));
            }
        }

        foreach (var (name, node) in services)
        {
            if (node is not JsonObject svc)
            {
                continue;
            }

            switch (svc["depends_on"])
            {
                case JsonArray arr:
                    foreach (var d in arr)
                    {
                        if (d is JsonValue dv && dv.TryGetValue<string>(out var dep))
                        {
                            Add(name, dep, "depends_on", "service_started");
                        }
                    }

                    break;
                case JsonObject map:
                    foreach (var (d, v) in map)
                    {
                        var vo = v as JsonObject;
                        Add(
                            name,
                            d,
                            "depends_on",
                            Str(vo?["condition"]) ?? "service_started",
                            Bool(vo?["restart"]) ?? false,
                            Bool(vo?["required"]) ?? true);
                    }

                    break;
            }

            foreach (var field in new[] { Str(svc["network_mode"]), Str(svc["pid"]), Str(svc["ipc"]) })
            {
                var m = field is null ? null : ServiceRefRe().Match(field);
                if (m is { Success: true } && m.Groups[1].Value == "service" && m.Groups[2].Value.Length > 0)
                {
                    Add(name, m.Groups[2].Value, "shares_namespace");
                }
            }

            if (svc["volumes_from"] is JsonArray vf)
            {
                foreach (var item in vf)
                {
                    var r = (Str(item) ?? string.Empty).Split(':');
                    if (r[0] == "container")
                    {
                        continue; // external container; handled as a scope reference
                    }

                    if (r[0].Length > 0)
                    {
                        Add(name, r[0], "volumes_from");
                    }
                }
            }
        }

        var (order, cycle) = TopoOrder(nodes, edges);
        return new DependencyGraph(nodes, edges, order, cycle);
    }

    /// <summary>Dependencies first. Deterministic (alphabetical among equals). Returns the cycle when there is one.</summary>
    public static (IReadOnlyList<string> Order, IReadOnlyList<string>? Cycle) TopoOrder(IReadOnlyList<string> nodes, IReadOnlyList<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        var deps = nodes.ToDictionary(n => n, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (deps.TryGetValue(e.From, out var set) && deps.ContainsKey(e.To))
            {
                set.Add(e.To);
            }
        }

        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        List<string>? cycle = null;
        var stack = new List<string>();

        void Visit(string n)
        {
            if (cycle is not null)
            {
                return;
            }

            var s = state.GetValueOrDefault(n);
            if (s == 2)
            {
                return;
            }

            if (s == 1)
            {
                var start = stack.IndexOf(n);
                cycle = [.. stack.Skip(start), n];
                return;
            }

            state[n] = 1;
            stack.Add(n);
            foreach (var d in deps[n])
            {
                Visit(d);
            }

            stack.RemoveAt(stack.Count - 1);
            state[n] = 2;
            order.Add(n);
        }

        foreach (var n in nodes.OrderBy(x => x, StringComparer.Ordinal))
        {
            Visit(n);
        }

        return cycle is not null ? ([], cycle) : (order, null);
    }

    /// <summary>Services that other services share a network/pid/ipc namespace or volumes with (RT-11): provider -> dependents.</summary>
    public static Dictionary<string, List<string>> NamespaceProviders(DependencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var m = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in graph.Edges.Where(e => e.Kind != "depends_on"))
        {
            if (!m.TryGetValue(e.To, out var list))
            {
                list = [];
                m[e.To] = list;
            }

            list.Add(e.From);
        }

        return m;
    }

    /// <summary>Resources a project's resolved config references by name - all must be in scope for mutations (RT-03).</summary>
    public static ProjectReferences References(JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var volumes = new HashSet<string>(StringComparer.Ordinal);
        var networks = new HashSet<string>(StringComparer.Ordinal);
        var containers = new HashSet<string>(StringComparer.Ordinal);

        static bool IsExternal(JsonNode? n) => n is JsonObject o && o["external"] is { } e && (e is JsonObject || (e is JsonValue v && v.TryGetValue<bool>(out var b) && b));

        foreach (var (key, v) in config["volumes"] as JsonObject ?? [])
        {
            if (IsExternal(v))
            {
                volumes.Add(Str(v?["name"]) ?? key);
            }
        }

        foreach (var (key, v) in config["networks"] as JsonObject ?? [])
        {
            if (IsExternal(v))
            {
                networks.Add(Str(v?["name"]) ?? key);
            }
        }

        foreach (var (_, node) in config["services"] as JsonObject ?? [])
        {
            if (node is not JsonObject svc)
            {
                continue;
            }

            if (Str(svc["container_name"]) is { Length: > 0 } cn)
            {
                containers.Add(cn);
            }

            foreach (var field in new[] { Str(svc["network_mode"]), Str(svc["pid"]), Str(svc["ipc"]) })
            {
                var m = field is null ? null : ServiceRefRe().Match(field);
                if (m is { Success: true } && m.Groups[1].Value == "container" && m.Groups[2].Value.Length > 0)
                {
                    containers.Add(m.Groups[2].Value);
                }
            }

            if (svc["volumes_from"] is JsonArray vf)
            {
                foreach (var item in vf)
                {
                    var r = (Str(item) ?? string.Empty).Split(':');
                    if (r[0] == "container" && r.Length > 1 && r[1].Length > 0)
                    {
                        containers.Add(r[1]);
                    }
                }
            }
        }

        return new ProjectReferences([.. volumes], [.. networks], [.. containers]);
    }

    /// <summary>Dependency graph reconstructed from <c>com.docker.compose.depends_on</c> labels (limited projects).</summary>
    public static DependencyGraph FromLabels(IEnumerable<IReadOnlyDictionary<string, string>> containerLabels)
    {
        ArgumentNullException.ThrowIfNull(containerLabels);
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        foreach (var labels in containerLabels)
        {
            if (!labels.TryGetValue(ComposeLabelNames.Service, out var svc) || svc.Length == 0)
            {
                continue;
            }

            nodes.Add(svc);
            var dep = labels.GetValueOrDefault(ComposeLabelNames.DependsOn) ?? string.Empty;
            foreach (var part in dep.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = part.Split(':');
                var to = bits[0];
                if (to.Length == 0)
                {
                    continue;
                }

                nodes.Add(to);
                if (!edges.Any(e => e.From == svc && e.To == to))
                {
                    var condition = bits.Length > 1 && bits[1].Length > 0 ? bits[1] : "service_started";
                    edges.Add(new GraphEdge(svc, to, "depends_on", condition, bits.Length > 2 && bits[2] == "true"));
                }
            }
        }

        var list = nodes.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var (order, cycle) = TopoOrder(list, edges);
        return new DependencyGraph(list, edges, order, cycle);
    }

    /// <summary>Services that (transitively) depend on <paramref name="target"/> through any edge kind.</summary>
    public static IReadOnlyList<string> DependentsOf(DependencyGraph graph, string target)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([target]);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var e in graph.Edges.Where(e => e.To == cur && !found.Contains(e.From)))
            {
                found.Add(e.From);
                queue.Enqueue(e.From);
            }
        }

        return graph.Order.Count > 0 ? graph.Order.Where(found.Contains).ToList() : [.. found.OrderBy(x => x, StringComparer.Ordinal)];
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    [GeneratedRegex("^(service|container):(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceRefRe();
}

/// <summary>Label names needed by the pure graph code (kept apart from the Docker naming helpers to avoid a cycle).</summary>
internal static class ComposeLabelNames
{
    public const string Service = Docker.ComposeLabels.Service;
    public const string DependsOn = Docker.ComposeLabels.DependsOn;
}

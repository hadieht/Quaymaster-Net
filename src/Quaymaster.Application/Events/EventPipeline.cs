using System.Text.Json.Nodes;
using Quaymaster.Application.Docker;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.Application.Events;

/// <summary>
/// Reduces raw Engine events to the allow-listed shape (RT-04/RT-25). Raw events carry the full exec command line in
/// <c>Action</c> and every container label in <c>Actor.Attributes</c>; neither leaves this class.
/// </summary>
public static class EventSanitizer
{
    /// <summary>Actions persisted and streamed. Everything else - notably exec_* healthcheck spam - is dropped.</summary>
    public static readonly IReadOnlySet<string> PersistedActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "create", "start", "die", "oom", "kill", "stop", "restart", "pause", "unpause", "destroy", "health_status", "rename", "pull", "delete", "tag", "untag",
    };

    public static readonly IReadOnlyList<string> EventTypes = ["container", "image", "volume", "network"];

    public static NewEvent? Sanitize(JsonObject ev, Scrubber scrubber, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(scrubber);
        var type = ev.Str("Type") ?? string.Empty;
        if (!EventTypes.Contains(type))
        {
            return null;
        }

        var rawAction = ev.Str("Action") ?? ev.Str("status") ?? string.Empty;
        var action = rawAction.Split(':')[0].Trim();
        if (action.StartsWith("exec_", StringComparison.Ordinal) || !PersistedActions.Contains(action))
        {
            return null;
        }

        var a = ev.Obj("Actor")?.Obj("Attributes").StringMap() ?? [];
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (action == "health_status")
        {
            var m = System.Text.RegularExpressions.Regex.Match(rawAction, @"health_status:\s*(\w+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (m.Success)
            {
                attributes["health"] = m.Groups[1].Value;
            }
        }

        string Clean(string s) => Redactor.RedactText(scrubber.Scrub(s));
        foreach (var k in new[] { "exitCode", "signal", "oldName" })
        {
            if (a.TryGetValue(k, out var v))
            {
                attributes[k] = Clean(v);
            }
        }

        var nano = ev.Num("timeNano");
        var ts = nano is > 0 ? nano.Value / 1_000_000 : (ev.Num("time") ?? (nowMs / 1000)) * 1000;
        return new NewEvent(
            ts,
            type,
            action,
            ev.Obj("Actor")?.Str("ID") ?? string.Empty,
            a.TryGetValue("name", out var name) ? Clean(name) : null,
            a.TryGetValue("image", out var image) ? Clean(image) : null,
            a.GetValueOrDefault(ComposeLabels.Project),
            a.GetValueOrDefault(ComposeLabels.Service),
            attributes,
            false);
    }
}

public sealed record KnownContainer(string State, string Name, string? Project, string? Service, string Image);

public static class InventoryDiff
{
    /// <summary>Derives synthetic "missed event" records by diffing two container inventories (RT-25).</summary>
    public static IReadOnlyList<NewEvent> Diff(IReadOnlyDictionary<string, KnownContainer> before, IReadOnlyDictionary<string, KnownContainer> after, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var output = new List<NewEvent>();

        NewEvent Make(string id, KnownContainer k, string action, string? extraKey = null, string? extraValue = null)
        {
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["missed"] = "true" };
            if (extraKey is not null)
            {
                attrs[extraKey] = extraValue ?? string.Empty;
            }

            return new NewEvent(nowMs, "container", action, id, k.Name, k.Image, k.Project, k.Service, attrs, true);
        }

        foreach (var (id, k) in before)
        {
            if (!after.TryGetValue(id, out var n))
            {
                output.Add(Make(id, k, "destroy"));
            }
            else if (k.State != n.State)
            {
                if (n.State == "running")
                {
                    output.Add(Make(id, n, "start"));
                }
                else if (n.State is "exited" or "dead")
                {
                    output.Add(Make(id, n, "die", "state", n.State));
                }
                else if (n.State == "paused")
                {
                    output.Add(Make(id, n, "pause"));
                }
            }
        }

        foreach (var (id, k) in after)
        {
            if (!before.ContainsKey(id))
            {
                output.Add(Make(id, k, "create"));
            }
        }

        return output;
    }
}

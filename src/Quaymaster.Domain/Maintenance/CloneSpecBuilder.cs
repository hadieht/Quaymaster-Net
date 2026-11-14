using System.Text.Json.Nodes;

namespace Quaymaster.Domain.Maintenance;

/// <summary>A network the clone joins after creation (the first user network is given at create time).</summary>
public sealed record NetworkAttachment(string Network, IReadOnlyList<string> Aliases, string? Ipv4, string? Ipv6)
{
    /// <summary>The Engine <c>EndpointConfig</c> for <c>POST /networks/{id}/connect</c>.</summary>
    public JsonObject ToEndpointConfig()
    {
        var o = new JsonObject { [nameof(Aliases)] = new JsonArray([.. Aliases.Select(a => (JsonNode?)JsonValue.Create(a))]) };
        if (Ipv4 is not null || Ipv6 is not null)
        {
            var ipam = new JsonObject();
            if (Ipv4 is not null)
            {
                ipam["IPv4Address"] = Ipv4;
            }

            if (Ipv6 is not null)
            {
                ipam["IPv6Address"] = Ipv6;
            }

            o["IPAMConfig"] = ipam;
        }

        return o;
    }
}

/// <summary>Body for <c>POST /containers/create</c> (without the name), the networks to connect before start and notes for the run record.</summary>
public sealed record CloneSpec(JsonObject Body, IReadOnlyList<NetworkAttachment> ExtraNetworks, IReadOnlyList<string> Notes);

/// <summary>Why a container cannot be cloned safely (maps to 409 <c>unsupported</c>).</summary>
public sealed class NotCloneableException(string message) : Exception(message);

/// <summary>
/// Config clone of a standalone container (RT-12). <c>docker inspect</c> merges the image's defaults into <c>Config</c>, so copying it
/// verbatim would pin the OLD image's Env/Cmd/Entrypoint onto the new one: only what differs from the old image's own config is carried
/// over. Every mount (anonymous volumes included) is re-attached by volume name so no data is orphaned. Pure: JSON in, JSON out.
/// </summary>
public static class CloneSpecBuilder
{
    private static readonly string[] BuiltinModes = ["default", "bridge", "host", "none"];

    public static void AssertCloneable(JsonObject old)
    {
        ArgumentNullException.ThrowIfNull(old);
        var host = old["HostConfig"] as JsonObject;
        var mode = host?["NetworkMode"]?.GetValue<string>() ?? string.Empty;
        if (mode.StartsWith("container:", StringComparison.Ordinal))
        {
            throw new NotCloneableException("The container shares another container's network namespace (container:...); it cannot be cloned safely.");
        }

        if (host?["AutoRemove"]?.GetValue<bool>() == true)
        {
            throw new NotCloneableException("The container was started with --rm; stopping it would delete it and there would be nothing to roll back to.");
        }

        if (old["Config"]?["Labels"]?["com.docker.compose.project"] is not null)
        {
            throw new NotCloneableException("Compose-managed containers are updated through Compose.");
        }
    }

    private static bool Same(JsonNode? a, JsonNode? b) => (a?.ToJsonString() ?? "null") == (b?.ToJsonString() ?? "null");

    private static bool Empty(JsonNode? v) => v is null || (v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length == 0) || (v is JsonArray { Count: 0 });

    public static CloneSpec Build(JsonObject old, JsonObject oldImage, string newImageRef)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(oldImage);
        AssertCloneable(old);
        var notes = new List<string>();
        var cfg = old["Config"] as JsonObject ?? [];
        var img = oldImage["Config"] as JsonObject ?? [];
        var id = old["Id"]?.GetValue<string>() ?? string.Empty;
        var oldName = (old["Name"]?.GetValue<string>() ?? string.Empty).TrimStart('/');
        var body = new JsonObject { ["Image"] = newImageRef };

        var imgEnv = (img["Env"] as JsonArray)?.Select(e => e?.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
        var env = (cfg["Env"] as JsonArray)?.Select(e => e?.GetValue<string>()).Where(e => e is not null && !imgEnv.Contains(e)).ToList() ?? [];
        if (env.Count > 0)
        {
            body["Env"] = new JsonArray([.. env.Select(e => (JsonNode?)JsonValue.Create(e))]);
        }

        bool KeepIfDiffers(string key)
        {
            var mine = cfg[key];
            var theirs = img[key];
            if (Same(mine, theirs) || (Empty(mine) && Empty(theirs)))
            {
                return false;
            }

            body[key] = mine?.DeepClone();
            return true;
        }

        var entrypointChanged = KeepIfDiffers("Entrypoint");
        var cmdChanged = KeepIfDiffers("Cmd");

        // an overridden entrypoint resets the image's default Cmd in the daemon: keep the Cmd explicit then
        if (entrypointChanged && !cmdChanged && cfg["Cmd"] is { } cmd)
        {
            body["Cmd"] = cmd.DeepClone();
        }

        _ = KeepIfDiffers("WorkingDir");
        _ = KeepIfDiffers("User");

        var imgLabels = img["Labels"] as JsonObject ?? [];
        var labels = new JsonObject();
        foreach (var (k, v) in cfg["Labels"] as JsonObject ?? [])
        {
            if (!(imgLabels.ContainsKey(k) && Same(imgLabels[k], v)))
            {
                labels[k] = v?.DeepClone();
            }
        }

        if (labels.Count > 0)
        {
            body["Labels"] = labels;
        }

        if (cfg["Healthcheck"] is { } hc && !Same(hc, img["Healthcheck"]))
        {
            body["Healthcheck"] = hc.DeepClone();
        }

        var imgPorts = img["ExposedPorts"] as JsonObject ?? [];
        var ports = new JsonObject();
        foreach (var (k, v) in cfg["ExposedPorts"] as JsonObject ?? [])
        {
            if (!imgPorts.ContainsKey(k))
            {
                ports[k] = v?.DeepClone();
            }
        }

        if (ports.Count > 0)
        {
            body["ExposedPorts"] = ports;
        }

        if (cfg["Tty"]?.GetValue<bool>() == true)
        {
            body["Tty"] = true;
        }

        if (cfg["OpenStdin"]?.GetValue<bool>() == true)
        {
            body["OpenStdin"] = true;
        }

        if (cfg["StopSignal"]?.GetValue<string>() is { Length: > 0 } sig && sig != img["StopSignal"]?.GetValue<string>())
        {
            body["StopSignal"] = sig;
        }

        if (cfg["StopTimeout"] is { } st)
        {
            body["StopTimeout"] = st.DeepClone();
        }

        if (cfg["Domainname"]?.GetValue<string>() is { Length: > 0 } dn)
        {
            body["Domainname"] = dn;
        }

        // an auto-generated hostname is the short container ID: the clone gets its own
        if (cfg["Hostname"]?.GetValue<string>() is { Length: > 0 } hn && hn != id[..Math.Min(12, id.Length)])
        {
            body["Hostname"] = hn;
            notes.Add("explicit hostname kept");
        }

        // ---- HostConfig: copied (it holds only explicit settings), with the mounts completed
        var host = (old["HostConfig"] as JsonObject)?.DeepClone() as JsonObject ?? [];
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in host["Binds"] as JsonArray ?? [])
        {
            declared.Add((b?.GetValue<string>() ?? string.Empty).Split(':').ElementAtOrDefault(1) ?? string.Empty);
        }

        foreach (var m in host["Mounts"] as JsonArray ?? [])
        {
            declared.Add(m?["Target"]?.GetValue<string>() ?? string.Empty);
        }

        foreach (var (k, _) in host["Tmpfs"] as JsonObject ?? [])
        {
            declared.Add(k);
        }

        var mounts = new JsonArray([.. (host["Mounts"] as JsonArray ?? []).Select(m => m?.DeepClone())]);
        foreach (var m in old["Mounts"] as JsonArray ?? [])
        {
            var dest = m?["Destination"]?.GetValue<string>() ?? string.Empty;
            if (declared.Contains(dest))
            {
                continue;
            }

            var type = m?["Type"]?.GetValue<string>();
            var readOnly = m?["RW"]?.GetValue<bool>() == false;
            if (type == "volume" && m?["Name"]?.GetValue<string>() is { Length: > 0 } volume)
            {
                mounts.Add(new JsonObject { ["Type"] = "volume", ["Source"] = volume, ["Target"] = dest, ["ReadOnly"] = readOnly });
                notes.Add($"volume {volume} re-attached at {dest}");
            }
            else if (type == "bind")
            {
                mounts.Add(new JsonObject { ["Type"] = "bind", ["Source"] = m?["Source"]?.GetValue<string>(), ["Target"] = dest, ["ReadOnly"] = readOnly });
            }
        }

        if (mounts.Count > 0)
        {
            host["Mounts"] = mounts;
        }

        _ = host.Remove("ContainerIDFile");
        _ = host.Remove("AutoRemove");

        // ---- networks: one at create time, the rest before start
        var shortId = id[..Math.Min(12, id.Length)];
        var attachments = new List<NetworkAttachment>();
        foreach (var (name, n) in (old["NetworkSettings"]?["Networks"] as JsonObject) ?? [])
        {
            var ipam = n?["IPAMConfig"];
            var aliases = (n?["Aliases"] as JsonArray ?? []).Select(a => a?.GetValue<string>()).Where(a => a is not null && a != shortId && a != oldName).Cast<string>().ToList();
            attachments.Add(new NetworkAttachment(name, aliases, ipam?["IPv4Address"]?.GetValue<string>(), ipam?["IPv6Address"]?.GetValue<string>()));
        }

        var mode = host["NetworkMode"]?.GetValue<string>() ?? "default";
        var builtin = BuiltinModes.Contains(mode);
        var primary = attachments.Find(a => a.Network == mode) ?? (builtin ? null : attachments.FirstOrDefault());
        var extra = attachments.Where(a => a != primary && a.Network is not ("host" or "none")).ToList();
        if (primary is not null && !builtin)
        {
            body["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = new JsonObject { [primary.Network] = primary.ToEndpointConfig() } };
        }

        body["HostConfig"] = host;
        if (extra.Count > 0)
        {
            notes.Add($"networks connected before start: {string.Join(", ", extra.Select(e => e.Network))}");
        }

        var skip = builtin ? "bridge" : primary?.Network;
        return new CloneSpec(body, [.. extra.Where(e => e.Network != skip)], notes);
    }
}

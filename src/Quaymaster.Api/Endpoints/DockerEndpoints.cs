using System.Text.RegularExpressions;
using FluentValidation;
using Quaymaster.Api.Http;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Stats;
using Quaymaster.Application.Validation;
using Quaymaster.Domain.Identity;
using Quaymaster.Domain.Stats;

namespace Quaymaster.Api.Endpoints;

/// <summary><c>/system/*</c>, <c>/containers/*</c>, <c>/compose/*</c> and the read-only inventory (images, volumes, networks).</summary>
public static partial class DockerEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var admin = api.MapGroup(string.Empty).RequireCapability(Capability.Write);
        var logs = api.MapGroup(string.Empty).RequireCapability(Capability.Logs);
        MapSystem(api);
        MapContainers(api, admin, logs);
        MapCompose(api, admin);
        MapInventory(api);
    }

    private static void MapSystem(RouteGroupBuilder api)
    {
        api.MapGet("/system/mode", (ISystemService system) => system.Mode());
        api.MapGet("/system/health", (HttpContext http, ISystemService system) => system.HealthAsync(http.RequestAborted));
        api.MapGet("/system/info", (HttpContext http, ISystemService system) => system.InfoAsync(http.RequestAborted));
        api.MapGet("/system/host", (HttpContext http, ISystemService system) => system.HostAsync(http.RequestAborted));
        api.MapGet("/system/df", async (HttpContext http, IDiskUsageService disk) =>
        {
            var refresh = http.Request.Query["refresh"].FirstOrDefault() == "1";
            var usage = await disk.GetAsync(refresh, http.RequestAborted);
            return new DiskUsageResponseDto(usage, usage is null);
        });
    }

    private static void MapContainers(RouteGroupBuilder api, RouteGroupBuilder admin, RouteGroupBuilder logs)
    {
        api.MapGet("/containers", (HttpContext http, IContainerQueries queries) => queries.ListAsync(http.RequestAborted));

        api.MapGet("/containers/{reference}", (HttpContext http, string reference, IContainerService containers) =>
            containers.InspectAsync(reference, http.RequestAborted));

        logs.MapGet("/containers/{reference}/logs", async (HttpContext http, string reference, IContainerService containers) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var tail = q.NumberInRange("tail", 1, 5000);
            var since = q.Number64InRange("since", 0, long.MaxValue);
            var timestamps = q.Enum("timestamps", "true", "false");
            q.ThrowIfInvalid();
            return await containers.LogsTailAsync(reference, new LogQuery(tail, since, timestamps != "false"), http.RequestAborted);
        });

        api.MapGet("/containers/{reference}/stats", async (HttpContext http, string reference, IContainerService containers, IStatsSampler stats) =>
        {
            var c = await containers.ResolveAsync(reference, http.RequestAborted);
            return new { points = stats.History(c.Id) };
        });

        api.MapGet("/containers/{reference}/stats/history", async (HttpContext http, string reference, IContainerService containers, IStatsStore store, TimeProvider time) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            const int maxMinutes = 30 * 24 * 60; // = the QM_STATS_RETENTION_HOURS ceiling (720 h)
            var minutes = q.NumberInRange("minutes", 1, maxMinutes, 60) ?? 60;
            var bucket = q.NumberInRange("bucket", 1, 1440);
            var sinceParam = q.Number64InRange("since", 0, long.MaxValue);
            var untilParam = q.Number64InRange("until", 0, long.MaxValue);
            q.ThrowIfInvalid();
            var c = await containers.ResolveAsync(reference, http.RequestAborted);
            var nowMinute = time.GetUtcNow().ToUnixTimeMilliseconds() / 60_000;
            var until = untilParam ?? nowMinute;
            var since = sinceParam ?? (until - minutes);
            if (until <= since || until - since > maxMinutes)
            {
                throw AppException.BadRequest($"since/until must span 1..{maxMinutes} minutes (epoch minutes)");
            }

            var rawRows = await store.HistoryAsync(c.Id, since, maxMinutes + 1_000, http.RequestAborted, untilParam);
            var rows = RollupBucketing.Downsample(rawRows, bucket ?? RollupBucketing.AutoBucket(until - since));
            return new
            {
                rollups = rows.Select(r => new RollupDto(r.ContainerId, r.Minute, r.Name, r.Samples, r.CpuAvg, r.CpuMax, r.MemAvg, r.MemMax, r.NetRx, r.NetTx, r.BlkRead, r.BlkWrite)),
            };
        });

        admin.MapPost("/containers/{reference}/actions/{action}", async (HttpContext http, string reference, string action, IValidator<ActionRequest> validator, IContainerService containers) =>
        {
            if (!ContainerService.Actions.Contains(action, StringComparer.Ordinal))
            {
                throw AppException.BadRequest($"Unknown action \"{action}\"");
            }

            var body = await StrictJsonBody.ReadAsync(http, validator);
            return await containers.ActAsync(reference, action, body, http.ActorOf(), http.RequestAborted);
        });
    }

    private static string ProjectName(string raw) =>
        ComposeArgv.IsValidProjectName(raw) ? raw : throw AppException.BadRequest("Invalid project name");

    private static void MapCompose(RouteGroupBuilder api, RouteGroupBuilder admin)
    {
        api.MapGet("/compose/projects", (HttpContext http, IComposeService compose) => compose.DiscoverAsync(false, http.RequestAborted));

        api.MapGet("/compose/projects/{name}", (HttpContext http, string name, IComposeService compose) =>
            compose.DetailAsync(ProjectName(name), http.RequestAborted));

        api.MapGet("/compose/projects/{name}/graph", async (HttpContext http, string name, IComposeService compose) =>
        {
            var d = await compose.DetailAsync(ProjectName(name), http.RequestAborted);
            return new ComposeGraphDto(d.Graph, d.NamespaceProviders);
        });

        // Redacted resolved configuration (secrets are masked server-side; there is no reveal API).
        api.MapGet("/compose/projects/{name}/config", (HttpContext http, string name, IComposeService compose) =>
            compose.ConfigViewAsync(ProjectName(name), http.RequestAborted));

        // `docker compose config --quiet` plus path-policy checks. Read-only.
        api.MapGet("/compose/projects/{name}/validate", (HttpContext http, string name, IComposeService compose) =>
            compose.ValidateAsync(ProjectName(name), http.RequestAborted));

        admin.MapPost("/compose/projects/{name}/actions/{action}", async (HttpContext http, string name, string action, IValidator<ComposeActionRequest> validator, IComposeService compose) =>
        {
            if (!ComposeService.Actions.Contains(action, StringComparer.Ordinal))
            {
                throw AppException.BadRequest($"Unknown action \"{action}\"");
            }

            var body = await StrictJsonBody.ReadAsync(http, validator);
            var id = await compose.OperateAsync(ProjectName(name), action, body, http.ActorOf(), http.RequestAborted);
            return Results.Json(new OperationIdDto(id), statusCode: StatusCodes.Status202Accepted);
        });
    }

    private static void MapInventory(RouteGroupBuilder api)
    {
        api.MapGet("/images", (HttpContext http, IInventoryService inventory) => inventory.ListImagesAsync(http.RequestAborted));
        api.MapGet("/images/{reference}", (HttpContext http, string reference, IInventoryService inventory) => inventory.InspectImageAsync(Uri.UnescapeDataString(reference), http.RequestAborted));
        api.MapGet("/volumes", (HttpContext http, IInventoryService inventory) => inventory.ListVolumesAsync(http.RequestAborted));
        api.MapGet("/volumes/{name}", (HttpContext http, string name, IInventoryService inventory) => inventory.InspectVolumeAsync(name, http.RequestAborted));
        api.MapGet("/networks", (HttpContext http, IInventoryService inventory) => inventory.ListNetworksAsync(http.RequestAborted));
        api.MapGet("/networks/{reference}", (HttpContext http, string reference, IInventoryService inventory) => inventory.InspectNetworkAsync(reference, http.RequestAborted));
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex FullIdRe();
}

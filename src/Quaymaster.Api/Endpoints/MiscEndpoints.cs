using FluentValidation;
using Quaymaster.Api.Http;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Events;
using Quaymaster.Application.Monitoring;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Validation;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Api.Endpoints;

/// <summary><c>/events</c>, <c>/audit</c> (admin), <c>/operations</c> and <c>/monitors/settings</c>.</summary>
public static class MiscEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var admin = api.MapGroup(string.Empty).RequireCapability(Capability.Write);

        api.MapGet("/events", async (HttpContext http, IEventQueries events) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var limit = q.NumberInRange("limit", 1, 500, 100) ?? 100;
            var beforeId = q.Number64InRange("beforeId", 1, long.MaxValue);
            var container = q.Text("container", 64, FullIdPattern);
            var project = q.Text("project", 100);
            q.ThrowIfInvalid();
            return new EventsDto(await events.ListAsync(new EventQuery(limit, beforeId, null, container, project), http.RequestAborted));
        });

        // The audit log is admin-only and has no write/update/delete API.
        admin.MapGet("/audit", async (HttpContext http, IAuditReader audit) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var limit = q.NumberInRange("limit", 1, 500, 100) ?? 100;
            var beforeId = q.Number64InRange("beforeId", 1, long.MaxValue);
            var action = q.Text("action", 100);
            var actor = q.Text("actor", 100);
            q.ThrowIfInvalid();
            return new AuditListDto(await audit.ListAsync(new AuditQuery(limit, beforeId, action, actor), http.RequestAborted));
        });

        api.MapGet("/operations", async (HttpContext http, IOperationManager operations) =>
            new OperationsDto(await operations.ListAsync(50, http.RequestAborted)));

        api.MapGet("/operations/{id}", async (HttpContext http, string id, IOperationManager operations) =>
            await operations.GetAsync(id, http.RequestAborted) ?? throw AppException.NotFound("Operation"));

        api.MapGet("/monitors/settings", (HttpContext http, IMonitorSettingsService monitors) => monitors.GetAsync(http.RequestAborted));

        admin.MapPut("/monitors/settings", async (HttpContext http, IValidator<MonitorSettingsRequest> validator, IMonitorSettingsService monitors) =>
        {
            var body = await StrictJsonBody.ReadAsync(http, validator);
            return await monitors.SaveAsync(body.ToDto(), http.ActorOf(), http.RequestAborted);
        });
    }

    private static readonly System.Text.RegularExpressions.Regex FullIdPattern = new("^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

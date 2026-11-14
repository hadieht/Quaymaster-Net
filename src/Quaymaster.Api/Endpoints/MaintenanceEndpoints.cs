using FluentValidation;
using Quaymaster.Api.Http;
using Quaymaster.Application.Maintenance;
using Quaymaster.Application.Validation;
using Quaymaster.Domain.Identity;
using Quaymaster.Domain.Maintenance;

namespace Quaymaster.Api.Endpoints;

/// <summary>
/// Maintenance configuration and the dry-run planner: <c>/maintenance/{policies,windows,schedules,plan}</c>. Routes only validate and
/// delegate; the rules (scope, audit, DST-safe time math, plan hash) live in the services. Run lifecycle, approval, rollback,
/// restore and standalone updates are T4b/T4c.
/// </summary>
public static class MaintenanceEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var admin = api.MapGroup(string.Empty).RequireCapability(Capability.Write);

        // ---- policies ---------------------------------------------------------------------------------------------------------
        api.MapGet("/maintenance/policies", async (HttpContext http, PolicyService policies) => await policies.ListAsync(http.RequestAborted));

        api.MapGet("/maintenance/policies/effective", async (HttpContext http, IPolicyResolver policies) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var project = q.Text("project", 100);
            var service = q.Text("service", 100);
            var container = q.Text("container", 200);
            q.ThrowIfInvalid();
            return await policies.ResolveAsync(project, service, container, http.RequestAborted);
        });

        admin.MapPut("/maintenance/policies/{kind}/{key}", async (HttpContext http, string kind, string key, PolicyService policies) =>
        {
            var body = await StrictJsonBody.ParseAsync<System.Text.Json.Nodes.JsonObject>(http);
            return await policies.PutAsync(kind, key, body, http.ActorOf(), http.RequestAborted);
        });

        admin.MapDelete("/maintenance/policies/{kind}/{key}", async (HttpContext http, string kind, string key, PolicyService policies) =>
            await policies.DeleteAsync(kind, key, http.ActorOf(), http.RequestAborted));

        // ---- windows ----------------------------------------------------------------------------------------------------------
        api.MapGet("/maintenance/windows", async (HttpContext http, WindowService windows) => await windows.ListAsync(http.RequestAborted));

        admin.MapPost("/maintenance/windows", async (HttpContext http, IValidator<WindowBody> validator, WindowService windows) =>
        {
            var body = await StrictJsonBody.ReadAsync(http, validator);
            return Results.Json(await windows.CreateAsync(body, http.ActorOf(), http.RequestAborted), statusCode: StatusCodes.Status201Created);
        });

        admin.MapPut("/maintenance/windows/{id}", async (HttpContext http, string id, IValidator<WindowBody> validator, WindowService windows) =>
        {
            var body = await StrictJsonBody.ReadAsync(http, validator);
            return await windows.UpdateAsync(id, body, http.ActorOf(), http.RequestAborted);
        });

        admin.MapDelete("/maintenance/windows/{id}", async (HttpContext http, string id, WindowService windows) =>
            await windows.DeleteAsync(id, http.ActorOf(), http.RequestAborted));

        // ---- schedules --------------------------------------------------------------------------------------------------------
        api.MapGet("/maintenance/schedules", async (HttpContext http, ScheduleService schedules) => await schedules.ListAsync(http.RequestAborted));

        admin.MapPost("/maintenance/schedules", async (HttpContext http, IValidator<ScheduleBody> validator, ScheduleService schedules) =>
        {
            var body = await StrictJsonBody.ReadAsync(http, validator);
            return Results.Json(await schedules.CreateAsync(body, http.ActorOf(), http.RequestAborted), statusCode: StatusCodes.Status201Created);
        });

        admin.MapPut("/maintenance/schedules/{id}", async (HttpContext http, string id, IValidator<ScheduleBody> validator, ScheduleService schedules) =>
        {
            var body = await StrictJsonBody.ReadAsync(http, validator);
            return await schedules.UpdateAsync(id, body, http.ActorOf(), http.RequestAborted);
        });

        admin.MapDelete("/maintenance/schedules/{id}", async (HttpContext http, string id, ScheduleService schedules) =>
            await schedules.DeleteAsync(id, http.ActorOf(), http.RequestAborted));

        // ---- dry run ----------------------------------------------------------------------------------------------------------
        // Preflight, validate, check, impact. Stores the plan as a `dry_run` run; changes and pulls nothing.
        admin.MapPost("/maintenance/plan", async (HttpContext http, IValidator<PlanBody> validator, IPlanService planner) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return await planner.DryRunAsync(
                new PlanRequest(b.Project!, b.Services, ApplyDrift: b.ApplyDrift == true, AllowUnhealthy: b.AllowUnhealthy == true),
                http.ActorOf(),
                http.RequestAborted);
        });

        // ---- runs (T4b) -------------------------------------------------------------------------------------------------------
        api.MapGet("/maintenance/runs", async (HttpContext http, IRunService runs) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var limit = q.NumberInRange("limit", 1, 200, 50) ?? 50;
            var status = q.Text("status", 40);
            var target = q.Text("target", 200);
            q.ThrowIfInvalid();
            return await runs.ListAsync(limit, status, target, http.RequestAborted);
        });

        api.MapGet("/maintenance/runs/{id}", async (HttpContext http, string id, IRunService runs) => await runs.GetAsync(id, http.RequestAborted));

        // Creates an update run from a fresh plan. It waits for approval unless `startNow` (+ `confirm` = project) or the unattended truth table holds.
        admin.MapPost("/maintenance/runs", async (HttpContext http, IValidator<RunBody> validator, IRunService runs) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            var run = await runs.CreateAsync(
                new CreateRunRequest(b.Project!, b.Services, b.ApplyDrift == true, b.AllowUnhealthy == true, b.StartNow == true, b.Confirm, b.WindowId, b.IgnoreWindow == true),
                http.ActorOf(),
                null,
                http.RequestAborted);
            return Results.Json(run, statusCode: run.Status is RunStatuses.Approved or RunStatuses.Running ? StatusCodes.Status202Accepted : StatusCodes.Status201Created);
        });

        admin.MapPost("/maintenance/runs/{id}/approve", async (HttpContext http, string id, IValidator<ApproveBody> validator, IRunService runs) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return Results.Json(await runs.ApproveAsync(id, b.PlanHash!, http.ActorOf(), http.RequestAborted), statusCode: StatusCodes.Status202Accepted);
        });

        admin.MapPost("/maintenance/runs/{id}/reject", async (HttpContext http, string id, IRunService runs) =>
            await runs.RejectAsync(id, http.ActorOf(), http.RequestAborted));

        // One-click restore of the run's recovery points (confirm = the run's target).
        admin.MapPost("/maintenance/runs/{id}/restore", async (HttpContext http, string id, IValidator<RestoreBody> validator, IRunService runs) =>
        {
            var b = await StrictJsonBody.ReadAsync(http, validator);
            return Results.Json(await runs.RestoreAsync(id, b, http.ActorOf(), http.RequestAborted), statusCode: StatusCodes.Status202Accepted);
        });
    }
}

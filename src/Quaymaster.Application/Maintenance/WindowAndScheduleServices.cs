using System.Text.Json.Nodes;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;

namespace Quaymaster.Application.Maintenance;

/// <summary>Maintenance windows (RT-09: <c>days</c> refers to the day the window STARTS; <c>end &lt;= start</c> crosses midnight).</summary>
public sealed class WindowService(IWindowStore store, IMutationGateway gateway, TimeProvider time)
{
    public async Task<WindowListDto> ListAsync(CancellationToken ct) =>
        new([.. (await store.ListAsync(ct)).Select(View)]);

    public async Task<WindowDto> CreateAsync(WindowBody body, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var w = Validate(body);
        return await gateway.AuditedAsync(new AuditScope(actor, "maintenance.window.create", w.Name, body), async () =>
        {
            if (await store.FindByNameAsync(w.Name, ct) is not null)
            {
                throw AppException.Conflict($"A maintenance window named \"{w.Name}\" already exists");
            }

            return View(await store.CreateAsync(w, time.GetUtcNow().ToUnixTimeMilliseconds(), ct));
        }, ct);
    }

    public async Task<WindowDto> UpdateAsync(string id, WindowBody body, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (await store.GetAsync(id, ct) is null)
        {
            throw AppException.NotFound("Window");
        }

        var w = Validate(body);
        return await gateway.AuditedAsync(new AuditScope(actor, "maintenance.window.update", w.Name, new { id }), async () =>
        {
            if (await store.FindByNameAsync(w.Name, ct) is { } other && other.Id != id)
            {
                throw AppException.Conflict($"A maintenance window named \"{w.Name}\" already exists");
            }

            return View(await store.UpdateAsync(id, w, ct) ?? throw AppException.NotFound("Window"));
        }, ct);
    }

    public async Task<OkDto> DeleteAsync(string id, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var w = await store.GetAsync(id, ct) ?? throw AppException.NotFound("Window");
        return await gateway.AuditedAsync(new AuditScope(actor, "maintenance.window.delete", w.Name, new { id }), async () =>
        {
            if (await store.InUseAsync(id, ct))
            {
                throw AppException.Conflict("The window is used by a schedule");
            }

            await store.DeleteAsync(id, ct);
            return new OkDto(true);
        }, ct);
    }

    private static WindowWrite Validate(WindowBody b)
    {
        var days = b.Days!.Distinct().Order().ToList();
        var err = TimeMath.WindowError(new TimeMath.WindowDef(days, b.Start!, b.End!, b.Timezone!));
        if (err is not null)
        {
            throw AppException.BadRequest(err);
        }

        return new WindowWrite(b.Name!, days, b.Start!, b.End!, b.Timezone!, b.Enabled ?? true);
    }

    private WindowDto View(WindowRecord w)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var next = w.Enabled ? TimeMath.NextWindowStart(w.Def, now) : null;
        return new WindowDto(
            w.Id, w.Name, w.Days, w.Start, w.End, w.Timezone, w.Enabled, ImageReferences.Iso(w.CreatedAt),
            w.Enabled && TimeMath.WindowAt(w.Def, now) is not null,
            next is null ? null : ImageReferences.Iso(next.Start));
    }
}

/// <summary>Cron schedules (DST-safe via Cronos) with mode <c>manual</c> (needs approval) or <c>auto</c>.</summary>
public sealed class ScheduleService(IScheduleStore store, IWindowStore windows, IMutationGateway gateway, TimeProvider time)
{
    public async Task<ScheduleListDto> ListAsync(CancellationToken ct) =>
        new([.. (await store.ListAsync(ct)).Select(View)]);

    public async Task<ScheduleDto> CreateAsync(ScheduleBody body, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var w = await ValidateAsync(body, ct);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return await gateway.AuditedAsync(new AuditScope(actor, "maintenance.schedule.create", w.Name, new { cron = w.Cron, mode = w.Mode }), async () =>
        {
            await EnsureUniqueNameAsync(w.Name, null, ct);
            return View(await store.CreateAsync(w, now, now, ct)); // cursor = now: never fire for the past
        }, ct);
    }

    public async Task<ScheduleDto> UpdateAsync(string id, ScheduleBody body, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (await store.GetAsync(id, ct) is null)
        {
            throw AppException.NotFound("Schedule");
        }

        var w = await ValidateAsync(body, ct);
        return await gateway.AuditedAsync(new AuditScope(actor, "maintenance.schedule.update", w.Name, new { id }), async () =>
        {
            await EnsureUniqueNameAsync(w.Name, id, ct);
            return View(await store.UpdateAsync(id, w, ct) ?? throw AppException.NotFound("Schedule"));
        }, ct);
    }

    public async Task<OkDto> DeleteAsync(string id, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var s = await store.GetAsync(id, ct) ?? throw AppException.NotFound("Schedule");
        return await gateway.AuditedAsync(new AuditScope(actor, "maintenance.schedule.delete", s.Name, new { id }), async () =>
        {
            await store.DeleteAsync(id, ct);
            return new OkDto(true);
        }, ct);
    }

    private async Task EnsureUniqueNameAsync(string name, string? selfId, CancellationToken ct)
    {
        if (await store.FindByNameAsync(name, ct) is { } other && other.Id != selfId)
        {
            throw AppException.Conflict($"A schedule named \"{name}\" already exists");
        }
    }

    private async Task<ScheduleWrite> ValidateAsync(ScheduleBody b, CancellationToken ct)
    {
        if (TimeMath.CronError(b.Cron!) is { } cronErr)
        {
            throw AppException.BadRequest($"Invalid cron expression: {cronErr}");
        }

        if (!TimeMath.IsValidTimeZone(b.Timezone!))
        {
            throw AppException.BadRequest($"Unknown time zone \"{b.Timezone}\"");
        }

        if (b.WindowId is not null && await windows.GetAsync(b.WindowId, ct) is null)
        {
            throw AppException.BadRequest("Unknown maintenance window");
        }

        var sel = b.Selector;
        if (b.JobType == "update")
        {
            if (string.IsNullOrEmpty(sel?.Project) && string.IsNullOrEmpty(sel?.Container))
            {
                throw AppException.BadRequest("An update schedule needs selector.project or selector.container");
            }

            if (!string.IsNullOrEmpty(sel?.Container) && b.Mode == "auto")
            {
                throw AppException.BadRequest("Standalone containers are never updated automatically (RT-12); use mode \"manual\"");
            }
        }

        var selector = new JsonObject();
        if (sel?.Project is not null)
        {
            selector["project"] = sel.Project;
        }

        if (sel?.Container is not null)
        {
            selector["container"] = sel.Container;
        }

        if (sel?.Services is not null)
        {
            selector["services"] = new JsonArray([.. sel.Services.Select(s => (JsonNode?)JsonValue.Create(s))]);
        }

        return new ScheduleWrite(b.Name!, b.Cron!.Trim(), b.Timezone!, b.JobType!, selector, b.Mode!, b.WindowId, b.Enabled ?? true);
    }

    private ScheduleDto View(ScheduleRecord s)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return new ScheduleDto(
            s.Id, s.Name, s.Cron, s.Timezone, s.JobType, s.Selector, s.Mode, s.WindowId, s.Enabled, ImageReferences.Iso(s.CreatedAt),
            s.Enabled ? [.. TimeMath.NextOccurrences(s.Cron, s.Timezone, now, 3).Select(ImageReferences.Iso)] : []);
    }
}

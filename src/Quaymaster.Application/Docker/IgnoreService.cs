using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Compose;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Events;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Safety;

namespace Quaymaster.Application.Docker;

/// <summary><c>POST /ignored</c> body (strict): <c>kind</c> project|container, <c>name</c>, <c>confirm</c> (must equal name).</summary>
public sealed record IgnoreRequest(string? Kind, string? Name, string? Confirm);

public sealed record IgnoredListDto(IReadOnlyList<IgnoredEntryDto> Entries);

public interface IIgnoreService
{
    IgnoredListDto List();

    Task<IgnoredEntryDto> IgnoreAsync(IgnoreRequest request, Actor actor, CancellationToken ct);

    Task<IgnoredEntryDto> RestoreAsync(long id, Actor actor, CancellationToken ct);
}

/// <summary>
/// "Stop monitoring": a FILTER, nothing else. Never calls the Docker Engine, never runs Compose, never touches a file;
/// allowed in read-only mode and outside the managed scope (hiding something is not acting on it).
/// </summary>
public sealed class IgnoreService(
    IIgnoreStore store,
    IIgnoreList list,
    IComposeService compose,
    IContainerService containers,
    IAuditLog audit,
    EventWatcher events) : IIgnoreService
{
    public IgnoredListDto List() => new(list.All);

    private async Task ChangedAsync(CancellationToken ct)
    {
        await list.RefreshAsync(ct, force: true);
        compose.Invalidate();
        containers.Invalidate();
        try
        {
            await events.RebaselineAsync(ct);
        }
        catch (AppException)
        {
            // daemon unreachable: the baseline is rebuilt on the next snapshot
        }
    }

    public async Task<IgnoredEntryDto> IgnoreAsync(IgnoreRequest request, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var kind = request.Kind!;
        var name = request.Name!;
        var target = $"{kind}:{name}";
        try
        {
            var entry = await DoIgnoreAsync(kind, name, request.Confirm!, actor, ct);
            await audit.RecordAsync(new AuditRecord(actor.Name, "monitoring.stop", AuditOutcome.Success, actor.Ip, target,
                new { kind, name, workingDir = entry.WorkingDir, dockerResourcesTouched = false }), ct);
            return entry;
        }
        catch (AppException e)
        {
            await audit.RecordAsync(new AuditRecord(actor.Name, "monitoring.stop",
                e.Status is 403 or 409 ? AuditOutcome.Denied : AuditOutcome.Failure, actor.Ip, target, new { kind, name, error = e.Message }), ct);
            throw;
        }
    }

    private async Task<IgnoredEntryDto> DoIgnoreAsync(string kind, string name, string confirm, Actor actor, CancellationToken ct)
    {
        if (confirm != name)
        {
            throw new AppException(409, ErrorCode.ConfirmationRequired, $"Confirm by typing the name: \"{name}\"", new { expected = name });
        }

        if (kind == "project")
        {
            if (!ComposeArgv.IsValidProjectName(name))
            {
                throw AppException.BadRequest("Invalid project name");
            }

            var p = (await compose.DiscoverAllAsync(true, ct)).FirstOrDefault(x => x.Name == name) ?? throw AppException.NotFound("Compose project");
            if (containers.Policy.ProjectProtectedReason(p.Name) is { } prot)
            {
                throw new AppException(409, ErrorCode.SelfProtected, $"Refused: {prot}. Quaymaster does not hide its own project.");
            }

            var entry = await store.AddAsync("project", p.Name, p.WorkingDir ?? string.Empty, actor.Name, ct);
            await ChangedAsync(ct);
            return entry;
        }

        var all = await containers.ListRawAllAsync(true, ct);
        var c = all.FirstOrDefault(x => x.Name.TrimStart('/') == name) ?? throw AppException.NotFound("Container");
        if (c.Project is { } project)
        {
            throw new AppException(409, ErrorCode.Unsupported, $"\"{name}\" belongs to Compose project \"{project}\"; stop monitoring the project instead");
        }

        if (containers.Policy.ContainerProtectedReason(new ContainerRef(c.Id, name, null)) is { } reason)
        {
            throw new AppException(409, ErrorCode.SelfProtected, $"Refused: {reason}");
        }

        var added = await store.AddAsync("container", name, string.Empty, actor.Name, ct);
        await ChangedAsync(ct);
        return added;
    }

    public async Task<IgnoredEntryDto> RestoreAsync(long id, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var target = $"ignored:{id}";
        var entry = await store.GetAsync(id, ct);
        if (entry is null)
        {
            await audit.RecordAsync(new AuditRecord(actor.Name, "monitoring.restore", AuditOutcome.Failure, actor.Ip, target, new { error = "not found" }), ct);
            throw AppException.NotFound("Ignored entry");
        }

        await store.RemoveAsync(id, ct);
        await ChangedAsync(ct);
        await audit.RecordAsync(new AuditRecord(actor.Name, "monitoring.restore", AuditOutcome.Success, actor.Ip, $"{entry.Kind}:{entry.Name}",
            new { kind = entry.Kind, name = entry.Name, workingDir = entry.WorkingDir }), ct);
        return entry;
    }
}

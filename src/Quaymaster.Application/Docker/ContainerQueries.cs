using Quaymaster.Application.Compose;

namespace Quaymaster.Application.Docker;

/// <summary><c>GET /containers</c>: the container list with the capability level of each one's Compose project.</summary>
public interface IContainerQueries
{
    Task<IReadOnlyList<ContainerSummaryDto>> ListAsync(CancellationToken ct);
}

public sealed class ContainerQueries(IContainerService containers, IComposeService compose) : IContainerQueries
{
    public async Task<IReadOnlyList<ContainerSummaryDto>> ListAsync(CancellationToken ct)
    {
        var listTask = containers.ListAsync(ct);
        var projectsTask = compose.DiscoverAsync(false, ct);
        var list = await listTask;
        var level = (await projectsTask).ToDictionary(p => p.Name, p => p.Level, StringComparer.Ordinal);
        var extras = await containers.ExtrasAsync([.. list.Select(c => c.Id)], ct);
        return [.. list.Select(c => c with
        {
            RestartCount = extras.TryGetValue(c.Id, out var e) ? e.RestartCount : null,
            StartedAt = extras.TryGetValue(c.Id, out var x) ? x.StartedAt : null,
            Capability = c.ComposeProject is null ? "standalone" : level.GetValueOrDefault(c.ComposeProject, "limited"),
        })];
    }
}

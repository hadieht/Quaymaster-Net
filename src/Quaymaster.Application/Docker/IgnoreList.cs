using Quaymaster.Domain.Docker;

namespace Quaymaster.Application.Docker;

/// <summary>
/// In-memory view of the "stop monitoring" list with the matching rules in ONE place. Ignoring is a filter only:
/// nothing here ever calls Docker.
/// <list type="bullet">
/// <item>project entry: matches containers/projects with the same Compose project name AND the same recorded working
/// directory ('' matches only projects that have no working directory)</item>
/// <item>container entry: matches a standalone (non-Compose) container by name</item>
/// </list>
/// </summary>
public interface IIgnoreList
{
    IReadOnlyList<IgnoredEntryDto> All { get; }

    /// <summary>Reloads from the store when the cached copy is older than a couple of seconds (or <paramref name="force"/>).</summary>
    Task RefreshAsync(CancellationToken ct, bool force = false);

    bool IsProjectIgnored(string name, string? workingDir);

    bool HidesRecord(string? project, string? name);

    bool IsContainerIgnored(string name, IReadOnlyDictionary<string, string>? labels);
}

public sealed class IgnoreList(IIgnoreStore store, TimeProvider time) : IIgnoreList
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    private IReadOnlyList<IgnoredEntryDto> _entries = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public IReadOnlyList<IgnoredEntryDto> All => Volatile.Read(ref _entries);

    public async Task RefreshAsync(CancellationToken ct, bool force = false)
    {
        if (!force && time.GetUtcNow() - _loadedAt < Ttl)
        {
            return;
        }

        var list = await store.ListAsync(ct);
        Volatile.Write(ref _entries, list);
        _loadedAt = time.GetUtcNow();
    }

    public bool IsProjectIgnored(string name, string? workingDir)
    {
        var wd = workingDir ?? string.Empty;
        return All.Any(e => e.Kind == "project" && e.Name == name && e.WorkingDir == wd);
    }

    public bool HidesRecord(string? project, string? name) =>
        All.Any(e => (e.Kind == "project" && project is not null && e.Name == project) || (e.Kind == "container" && e.Name == name));

    public bool IsContainerIgnored(string name, IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is not null && labels.TryGetValue(ComposeLabels.Project, out var project) && project.Length > 0)
        {
            return IsProjectIgnored(project, labels.GetValueOrDefault(ComposeLabels.WorkingDir));
        }

        var n = name.TrimStart('/');
        return All.Any(e => e.Kind == "container" && e.Name == n);
    }
}

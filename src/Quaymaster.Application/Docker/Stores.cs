using Quaymaster.Domain.Stats;

namespace Quaymaster.Application.Docker;

public sealed record IgnoredEntryDto(long Id, string Kind, string Name, string WorkingDir, string IgnoredAt, string IgnoredBy);

public sealed record NewEvent(
    long Ts,
    string Type,
    string Action,
    string ActorId,
    string? Name,
    string? Image,
    string? Project,
    string? Service,
    IReadOnlyDictionary<string, string> Attributes,
    bool Synthetic);

public sealed record EventQuery(int Limit, long? BeforeId = null, long? SinceId = null, string? ContainerId = null, string? Project = null);

public interface IEventStore
{
    /// <summary>Inserts a batch in one transaction; returns the assigned ids in order.</summary>
    Task<IReadOnlyList<long>> InsertManyAsync(IReadOnlyList<NewEvent> events, CancellationToken ct);

    Task<IReadOnlyList<DockerEventRecord>> ListAsync(EventQuery query, CancellationToken ct);

    Task<long?> LastTimestampAsync(CancellationToken ct);

    Task<int> PruneAsync(long olderThanMs, CancellationToken ct);
}

public interface IStatsStore
{
    Task UpsertManyAsync(IReadOnlyList<RollupRow> rows, CancellationToken ct);

    Task<IReadOnlyList<RollupRow>> HistoryAsync(string containerId, long sinceMinute, int limit, CancellationToken ct, long? untilMinute = null);

    /// <summary>Streams every container's roll-ups in [since, until], ordered by container then minute (dashboard overview); never buffers the whole window.</summary>
    IAsyncEnumerable<RollupRow> StreamAsync(long sinceMinute, long untilMinute, CancellationToken ct);

    Task<int> PruneAsync(long olderThanMinute, CancellationToken ct);
}

public interface IKvStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);

    Task SetAsync(string key, string value, CancellationToken ct);
}

public interface IIgnoreStore
{
    Task<IReadOnlyList<IgnoredEntryDto>> ListAsync(CancellationToken ct);

    Task<IgnoredEntryDto> AddAsync(string kind, string name, string workingDir, string by, CancellationToken ct);

    Task<IgnoredEntryDto?> GetAsync(long id, CancellationToken ct);

    Task RemoveAsync(long id, CancellationToken ct);
}

public sealed record AuditEntryDto(long Id, string Time, string Actor, string? Ip, string Action, string? Target, object? Params, string Outcome, long? DurationMs);

public sealed record AuditQuery(int Limit, long? BeforeId, string? Action, string? Actor);

public interface IAuditReader
{
    Task<IReadOnlyList<AuditEntryDto>> ListAsync(AuditQuery query, CancellationToken ct);
}

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Quaymaster.Application.Docker;

// Wire shapes (shared/src/api.ts). camelCase on the wire; optional members are omitted when null where the original
// type marks them optional (`?`), and written as null where the original declares `T | null`.

public sealed record PortInfoDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ip,
    int PrivatePort,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PublicPort,
    string Type);

public sealed record ContainerSummaryDto
{
    public required string Id { get; init; }

    public required string ShortId { get; init; }

    public required string Name { get; init; }

    public required string Image { get; init; }

    public required string ImageId { get; init; }

    public required string State { get; init; }

    public required string Status { get; init; }

    public required string Health { get; init; }

    public required string CreatedAt { get; init; }

    public required IReadOnlyList<PortInfoDto> Ports { get; init; }

    public required IReadOnlyDictionary<string, string> Labels { get; init; }

    public string? ComposeProject { get; init; }

    public string? ComposeService { get; init; }

    public string? ProtectedReason { get; init; }

    public required bool Mutable { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Capability { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RestartCount { get; init; }

    /// <summary>Only present on the list endpoint (null when the container never started).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartedAt { get; init; }
}

public sealed record ContainerDetailDto(ContainerSummaryDto? Summary, JsonNode? Inspect);

public sealed record LogLineDto(string Stream, string? Ts, string Text);

public sealed record LogsDto(IReadOnlyList<LogLineDto> Lines);

public sealed record ActionResultDto
{
    public bool Ok { get; init; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

public sealed record OperationIdDto(string OperationId);

public sealed record DockerEventRecord(
    long Id,
    string Time,
    string Type,
    string Action,
    string ActorId,
    string? Name,
    string? Image,
    string? Project,
    string? Service,
    IReadOnlyDictionary<string, string> Attributes,
    bool Synthetic);

public sealed record EventsDto(IReadOnlyList<DockerEventRecord> Events);

public sealed record AuditListDto(IReadOnlyList<AuditEntryDto> Entries);

public sealed record OperationInfoDto(
    string Id,
    string Kind,
    string Target,
    string Status,
    string StartedAt,
    string? FinishedAt,
    string Actor,
    string? ComposeVersion,
    string Output,
    string? Error);

public sealed record OperationsDto(IReadOnlyList<OperationInfoDto> Operations);

public sealed record ModeInfoDto(
    bool ReadOnly,
    string? ReadOnlyReason,
    IReadOnlyList<string> ManagedScope,
    string? SelfContainerId,
    string? SelfProject,
    IReadOnlyList<string> StackPaths);

public sealed record HostRootDto(string Path, long Total, long Free);

public sealed record HostMetricsDto(
    double? CpuPercent,
    IReadOnlyList<double> Load,
    long MemTotal,
    long MemAvailable,
    double UptimeSeconds,
    HostRootDto? DockerRoot,
    string? DockerRootError);

public sealed record ImageContainerDto(string Id, string Name, string State, string? ComposeProject, string? ComposeService);

public sealed record ImageSummaryDto(
    string Id,
    string ShortId,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Digests,
    long Size,
    string CreatedAt,
    IReadOnlyList<ImageContainerDto> Containers,
    bool InUse);

public sealed record VolumeSummaryDto(
    string Name,
    string Driver,
    string Mountpoint,
    string? CreatedAt,
    IReadOnlyDictionary<string, string> Labels,
    string? ComposeProject,
    IReadOnlyList<string> UsedBy,
    bool Anonymous,
    long? Size);

public sealed record NetworkSummaryDto(
    string Id,
    string Name,
    string Driver,
    string Scope,
    bool Internal,
    string? ComposeProject,
    IReadOnlyList<string> Containers,
    IReadOnlyDictionary<string, string> Labels);

public sealed record DiskUsageImagesDto(int Count, long LayersSize, long TotalSize, int UnusedCount);

public sealed record ContainerDiskDto(long SizeRw, long SizeRootFs);

public sealed record DiskUsageDto(
    string RefreshedAt,
    DiskUsageImagesDto Images,
    IReadOnlyDictionary<string, ContainerDiskDto> Containers,
    IReadOnlyDictionary<string, long> Volumes);

public sealed record DiskUsageResponseDto(DiskUsageDto? Usage, bool Refreshing);

public sealed record RollupDto(
    [property: JsonPropertyName("container_id")] string ContainerId,
    [property: JsonPropertyName("minute")] long Minute,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("cpu_avg")] double CpuAvg,
    [property: JsonPropertyName("cpu_max")] double CpuMax,
    [property: JsonPropertyName("mem_avg")] double MemAvg,
    [property: JsonPropertyName("mem_max")] double MemMax,
    [property: JsonPropertyName("net_rx")] long NetRx,
    [property: JsonPropertyName("net_tx")] long NetTx,
    [property: JsonPropertyName("blk_read")] long BlkRead,
    [property: JsonPropertyName("blk_write")] long BlkWrite);

public sealed record MonitorSettingsDto(
    bool Enabled,
    double DiskFreePercentBelow,
    double DiskFreeBytesBelow,
    double CpuPercentAbove,
    double MemoryPercentAbove,
    int SustainSeconds,
    int RestartLoopCount,
    int RestartLoopWindowSeconds,
    int UpcomingMaintenanceMinutes);

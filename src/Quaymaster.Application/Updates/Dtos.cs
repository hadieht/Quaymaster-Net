using System.Text.Json.Serialization;
using Quaymaster.Domain.Storage;

namespace Quaymaster.Application.Updates;

// Wire shapes: shared/src/maintenance.ts (UpdateState/ImageUpdateStatus/…) and shared/src/storage.ts. camelCase on the wire.

public sealed record ImageUserDto(string ContainerId, string ContainerName, string? Project, string? Service, string ImageId);

public sealed record ImageUpdateStatusDto(
    string Ref,
    string State,
    string? Reason,
    string? LocalDigest,
    string? RemoteDigest,
    string? CheckedAt,
    string Source,
    bool Quarantined,
    IReadOnlyList<ImageUserDto> UsedBy);

public sealed record UpdatesDto(IReadOnlyList<ImageUpdateStatusDto> Images);

public sealed record QuarantineEntryDto(string Target, string Digest, string? RunId, string? Reason, string CreatedAt);

public sealed record QuarantineListDto(IReadOnlyList<QuarantineEntryDto> Entries);

public sealed record ClearedDto(int Cleared);

public sealed record StagedImageDto(string Tag, string Ref, string Digest, string? ImageId, string CreatedAt);

public sealed record RollbackPointDto(
    string Tag,
    string RunId,
    string? Project,
    string? Service,
    string Ref,
    string ImageId,
    string CreatedAt,
    string ExpiresAt,
    bool Released);

public sealed record StagedListDto(IReadOnlyList<StagedImageDto> Staged, IReadOnlyList<RollbackPointDto> RollbackPoints);

public sealed record RegistryCredentialDto(string Host, string Username, string UpdatedAt);

public sealed record RegistryCredentialsDto(IReadOnlyList<RegistryCredentialDto> Credentials);

public sealed record RegistryCredentialPutDto(string Host, string Username);

public sealed record OkDto(bool Ok);

// ---- image cleanup (shared/src/maintenance.ts)

public sealed record CleanupCandidateDto(
    string Id,
    string Category,
    string Name,
    long? Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? VirtualSize,
    int? AgeHours,
    string Note);

public sealed record CleanupProtectedDto(string Name, string Category, string Reason);

public sealed record ReportOnlyDto(IReadOnlyList<string> UnusedVolumes, IReadOnlyList<string> UnusedNetworks);

public sealed record CleanupPreviewDto(
    string PreviewId,
    string ExpiresAt,
    IReadOnlyList<string> Categories,
    int MinAgeHours,
    IReadOnlyList<CleanupCandidateDto> Candidates,
    IReadOnlyList<CleanupProtectedDto> Protected,
    ReportOnlyDto ReportOnly,
    long TotalBytes);

public sealed record RemovedItemDto(string Id, string Name);

public sealed record SkippedItemDto(string Id, string Name, string Reason);

public sealed record CleanupResultDto(
    IReadOnlyList<RemovedItemDto> Removed,
    IReadOnlyList<SkippedItemDto> Skipped,
    long ReclaimedBytes,
    ReclaimReport Reclaim);

// ---- storage cleanup (shared/src/storage.ts)

public sealed record StorageCandidateDto(
    string Id,
    string Kind,
    string Category,
    string Name,
    long? Size,
    string? CreatedAt,
    int? AgeHours,
    bool Anonymous,
    string? ComposeProject,
    string? LastKnownUser,
    string Note);

public sealed record StorageProtectedDto(string Name, string Kind, string Reason);

public sealed record StorageCleanupPreviewDto(
    string PreviewId,
    string ExpiresAt,
    IReadOnlyList<string> Categories,
    bool IncludeComposeProjectVolumes,
    int MinAgeHours,
    IReadOnlyList<StorageCandidateDto> Candidates,
    IReadOnlyList<StorageProtectedDto> Protected,
    long EstimatedBytes,
    string? ConfirmPhrase,
    bool DeletesData);

public sealed record StorageRemovedDto(string Id, string Name, string Kind, long Bytes);

public sealed record StorageCleanupResultDto(
    IReadOnlyList<StorageRemovedDto> Removed,
    IReadOnlyList<SkippedItemDto> Skipped,
    ReclaimReport Reclaim);

public sealed record ResourceRemoveResultDto(
    string Kind,
    string Name,
    string Action,
    IReadOnlyList<string> RemovedTags,
    IReadOnlyList<string> RemainingTags,
    ReclaimReport Reclaim);

// ---- request bodies (strict)

public sealed record ImagePullRequestBody(string? Ref);

public sealed record QuarantineClearBody(string? Target, string? Digest);

public sealed record CredentialBody(string? Host, string? Username, string? Password);

public sealed record CleanupPreviewBody(IReadOnlyList<string>? Categories, double? MinAgeHours, string? LabelFilter);

public sealed record ExecuteBody(string? PreviewId, string? Confirm);

public sealed record StoragePreviewBody(IReadOnlyList<string>? Categories, bool? IncludeComposeProjectVolumes, double? MinAgeHours);

public sealed record VolumeRemoveBody(string? Confirm, bool? AcknowledgeComposeProject);

public sealed record ConfirmBody(string? Confirm);

public sealed record ImageRemoveBody(string? Confirm, string? Tag);

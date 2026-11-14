namespace Quaymaster.Domain.Updates;

/// <summary>An image pulled by digest and held under <c>quaymaster-staged/…</c> (RT-07). Times are epoch ms.</summary>
public sealed record StagedImage(string Tag, string Ref, string Digest, string? ImageId, long CreatedAt);

/// <summary>A rollback point (RT-06): the image the running container had before a deploy. Times are epoch ms.</summary>
public sealed record RollbackPoint(
    string Tag,
    string RunId,
    string? Project,
    string? Service,
    string? ContainerName,
    string Ref,
    string ImageId,
    long CreatedAt,
    long ExpiresAt,
    bool Released);

/// <summary>A (target, remote digest) pair that failed and is skipped by unattended runs until cleared (RT-22).</summary>
public sealed record QuarantineEntry(string Target, string Digest, string? RunId, string? Reason, long CreatedAt);

/// <summary>A cached registry lookup. <see cref="State"/> is <c>ok</c> or <c>error</c>.</summary>
public sealed record ImageCheck(
    string Ref,
    string State,
    string? Reason,
    string? LocalDigest,
    string? RemoteDigest,
    string? Source,
    long CheckedAt,
    long ExpiresAt);

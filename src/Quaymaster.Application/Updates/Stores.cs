using Quaymaster.Domain.Updates;

namespace Quaymaster.Application.Updates;

/// <summary>Cached registry lookups, keyed by canonical <c>registry/repo:tag</c>.</summary>
public interface IImageChecksStore
{
    Task<ImageCheck?> GetAsync(string key, CancellationToken ct);

    Task PutAsync(ImageCheck check, CancellationToken ct);

    /// <summary>Deletes one entry, or all of them when <paramref name="key"/> is null.</summary>
    Task InvalidateAsync(string? key, CancellationToken ct);
}

public interface IStagedStore
{
    Task PutAsync(StagedImage image, CancellationToken ct);

    Task<StagedImage?> GetAsync(string tag, CancellationToken ct);

    Task<IReadOnlyList<StagedImage>> ListAsync(CancellationToken ct);

    Task DeleteAsync(string tag, CancellationToken ct);
}

public interface IRollbackStore
{
    Task PutAsync(RollbackPoint point, CancellationToken ct);

    Task<RollbackPoint?> GetAsync(string tag, CancellationToken ct);

    /// <summary>Not released: protected from cleanup until <c>ExpiresAt</c>.</summary>
    Task<IReadOnlyList<RollbackPoint>> ActiveAsync(CancellationToken ct);

    Task ReleaseAsync(string tag, CancellationToken ct);

    Task<IReadOnlyList<RollbackPoint>> ListAsync(int limit, CancellationToken ct);
}

public interface IQuarantineStore
{
    Task<QuarantineRow?> FindAsync(string target, string digest, CancellationToken ct);

    Task UpsertAsync(QuarantineRow row, CancellationToken ct);

    Task<IReadOnlyList<QuarantineRow>> ListActiveAsync(CancellationToken ct);

    /// <summary>Marks matching active entries cleared (all digests of the target when <paramref name="digest"/> is null).</summary>
    Task<int> ClearAsync(string target, string? digest, long now, CancellationToken ct);
}

/// <summary>A quarantine row; <see cref="ClearedAt"/> null = active.</summary>
public sealed record QuarantineRow(string Target, string Digest, string? RunId, string? Reason, long CreatedAt, long? ClearedAt);

/// <summary>Registry credentials: metadata in a table, the password encrypted in the secret store. Never returns a password except to the pull/lookup code.</summary>
public interface IRegistryCredentialStore
{
    Task PutAsync(string host, string username, string password, long now, CancellationToken ct);

    Task<Registry.RegistryCredential?> GetAsync(string host, CancellationToken ct);

    Task<IReadOnlyList<(string Host, string Username, long UpdatedAt)>> ListAsync(CancellationToken ct);

    Task<bool> DeleteAsync(string host, CancellationToken ct);
}

using Quaymaster.Domain.Registry;

namespace Quaymaster.Application.Registry;

/// <param name="Kind"><c>unauthorized</c> | <c>not_found</c> | <c>rate_limited</c> | <c>network</c> | <c>invalid</c> | <c>other</c>.</param>
public sealed class RegistryException(string kind, string message, int? status = null) : Exception(message)
{
    public string Kind { get; } = kind;

    public int? Status { get; } = status;
}

public sealed record ManifestHead(string Digest, string? MediaType);

public sealed record ManifestPlatform(string Digest, string? Os, string? Architecture);

/// <summary>A manifest or index. <see cref="Manifests"/> is non-empty for an index.</summary>
public sealed record ManifestDoc(string Digest, string MediaType, IReadOnlyList<ManifestPlatform> Manifests, long ConfigSize, long LayersSize, long Size);

/// <summary>
/// Minimal Docker Registry v2 client: manifest digest by HEAD with token authentication. Implementations throttle per host,
/// honour <c>Retry-After</c>, never send credentials to an unlisted token realm and never over plain http (RT-20).
/// </summary>
public interface IRegistryClient
{
    /// <summary>HEAD the manifest of <paramref name="reference"/> (tag or digest; default: the ref's own) and return the digest served.</summary>
    Task<ManifestHead> HeadManifestAsync(ImageRef image, string? reference, CancellationToken ct);

    Task<ManifestDoc> GetManifestAsync(ImageRef image, string? reference, CancellationToken ct);

    /// <summary>Sum of compressed layer sizes for the platform manifest behind <paramref name="digest"/> (disk preflight).</summary>
    Task<long> CompressedSizeAsync(ImageRef image, string digest, CancellationToken ct);

    /// <summary>Until when a rate-limited host is left alone; null when it is not backed off.</summary>
    DateTimeOffset? BackedOffUntil(ImageRef image);
}

public sealed record RegistryCredential(string Username, string Password);

public sealed record RegistryCredentialInfo(string Host, string Username, string UpdatedAt);

/// <summary>Encrypts secrets at rest (AES-256-GCM, key derived from QM_SECRET_KEY). <paramref name="aad"/> binds a ciphertext to its row.</summary>
public interface ISecretBox
{
    string Encrypt(string plaintext, string aad);

    string Decrypt(string payload, string aad);
}

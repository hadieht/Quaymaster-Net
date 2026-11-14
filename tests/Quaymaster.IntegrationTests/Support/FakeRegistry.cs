using Microsoft.Extensions.DependencyInjection;
using Quaymaster.Application.Registry;
using Quaymaster.Domain.Registry;

namespace Quaymaster.IntegrationTests.Support;

/// <summary>An in-memory registry: tag reference (<c>registry/repo:tag</c>) → digest, or an error. Counts the calls.</summary>
public sealed class FakeRegistry : IRegistryClient
{
    public Dictionary<string, string> Digests { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, RegistryException> Errors { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, long> Sizes { get; } = new(StringComparer.Ordinal);

    public List<string> Heads { get; } = [];

    public DateTimeOffset? BackedOff { get; set; }

    public Task<ManifestHead> HeadManifestAsync(ImageRef image, string? reference, CancellationToken ct)
    {
        var key = ImageReferences.CacheKey(image);
        lock (Heads)
        {
            Heads.Add(key);
        }

        if (Errors.TryGetValue(key, out var e))
        {
            throw e;
        }

        return Digests.TryGetValue(key, out var d)
            ? Task.FromResult(new ManifestHead(d, "application/vnd.oci.image.index.v1+json"))
            : throw new RegistryException("not_found", $"{image.Registry}: manifest not found", 404);
    }

    public Task<ManifestDoc> GetManifestAsync(ImageRef image, string? reference, CancellationToken ct) =>
        Task.FromResult(new ManifestDoc(reference ?? "sha256:" + new string('0', 64), "application/vnd.oci.image.manifest.v1+json", [], 10, Sizes.GetValueOrDefault(ImageReferences.CacheKey(image), 1000), 100));

    public Task<long> CompressedSizeAsync(ImageRef image, string digest, CancellationToken ct) =>
        Task.FromResult(Sizes.GetValueOrDefault(ImageReferences.CacheKey(image), 1000));

    public DateTimeOffset? BackedOffUntil(ImageRef image) => BackedOff;

    public void Install(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IRegistryClient>(this);
        services.AddSingleton(this);
    }
}

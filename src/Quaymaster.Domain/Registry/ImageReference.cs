using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Registry;

/// <summary>A parsed image reference (docker/distribution grammar, simplified).</summary>
/// <param name="Registry">Registry host in canonical form: <c>docker.io</c>, <c>ghcr.io</c>, <c>127.0.0.1:5055</c>.</param>
/// <param name="Repo">Repository path without the registry, e.g. <c>library/nginx</c>.</param>
public sealed record ImageRef(string Registry, string Repo, string? Tag, string? Digest);

/// <summary>Port of <c>registry/reference.ts</c>: parsing, registry host resolution, cache keys and the internal tag names.</summary>
public static partial class ImageReferences
{
    public const string StagedPrefix = "quaymaster-staged/";
    public const string RollbackPrefix = "quaymaster-rollback/";

    private static readonly HashSet<string> HubAliases = new(StringComparer.Ordinal)
    {
        "docker.io", "index.docker.io", "registry-1.docker.io", "registry.hub.docker.com",
    };

    public static bool IsDigest(string s) => DigestRe().IsMatch(s);

    /// <summary>Parses <c>[registry/]repo[:tag][@sha256:digest]</c>. Null for anything that is not a valid reference.</summary>
    public static ImageRef? Parse(string? input)
    {
        var rest = (input ?? string.Empty).Trim();
        if (rest.Length == 0 || rest.Length > 400 || rest.Any(char.IsWhiteSpace))
        {
            return null;
        }

        string? digest = null;
        var at = rest.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            digest = rest[(at + 1)..];
            rest = rest[..at];
            if (!IsDigest(digest))
            {
                return null;
            }
        }

        string? tag = null;
        var lastSlash = rest.LastIndexOf('/');
        var colon = rest.LastIndexOf(':');
        if (colon > lastSlash)
        {
            tag = rest[(colon + 1)..];
            rest = rest[..colon];
            if (!TagRe().IsMatch(tag))
            {
                return null;
            }
        }

        var parts = rest.Split('/').ToList();
        var registry = "docker.io";
        var first = parts[0];
        if (parts.Count > 1 && (first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost"))
        {
            registry = first.ToLowerInvariant();
            parts.RemoveAt(0);
        }

        if (HubAliases.Contains(registry))
        {
            registry = "docker.io";
        }

        var repo = string.Join('/', parts);
        if (repo.Length == 0 || !RepoRe().IsMatch(repo))
        {
            return null;
        }

        if (registry == "docker.io" && !repo.Contains('/', StringComparison.Ordinal))
        {
            repo = $"library/{repo}";
        }

        if (tag is null && digest is null)
        {
            tag = "latest";
        }

        return new ImageRef(registry, repo, tag, digest);
    }

    /// <summary>The host a client connects to (Docker Hub's API lives on registry-1.docker.io).</summary>
    public static string ApiHost(ImageRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Registry == "docker.io" ? "registry-1.docker.io" : r.Registry;
    }

    /// <summary><c>registry/repo</c>: the identity used to match RepoDigests of the same repository.</summary>
    public static string CanonicalRepo(ImageRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return $"{r.Registry}/{r.Repo}";
    }

    /// <summary>Name usable with the Engine API (Docker Hub library images stay short).</summary>
    public static string EngineName(ImageRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Registry == "docker.io")
        {
            return r.Repo.StartsWith("library/", StringComparison.Ordinal) ? r.Repo[8..] : r.Repo;
        }

        return $"{r.Registry}/{r.Repo}";
    }

    public static string Format(ImageRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return $"{EngineName(r)}{(r.Tag is null ? string.Empty : ":" + r.Tag)}{(r.Digest is null ? string.Empty : "@" + r.Digest)}";
    }

    /// <summary>Canonical cache key for a tag reference.</summary>
    public static string CacheKey(ImageRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return $"{CanonicalRepo(r)}:{r.Tag ?? "latest"}";
    }

    /// <summary>Digests of the <c>repo@sha256:...</c> entries in <paramref name="repoDigests"/> that belong to the repository of <paramref name="r"/>.</summary>
    public static IReadOnlyList<string> DigestsForRepo(IEnumerable<string> repoDigests, ImageRef r)
    {
        ArgumentNullException.ThrowIfNull(repoDigests);
        var want = CanonicalRepo(r);
        var output = new List<string>();
        foreach (var rd in repoDigests)
        {
            var p = Parse(rd);
            if (p?.Digest is not null && CanonicalRepo(p) == want)
            {
                output.Add(p.Digest);
            }
        }

        return output;
    }

    private static string ShortHash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];

    /// <summary><c>quaymaster-staged/&lt;hash(repo)&gt;:&lt;digest12&gt;</c> (RT-07): keeps a pulled image from being dangling or moving a tag.</summary>
    public static string StagedTag(ImageRef r, string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var bare = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : digest;
        return $"{StagedPrefix}{ShortHash(CanonicalRepo(r))}:{bare[..Math.Min(12, bare.Length)]}";
    }

    /// <summary><c>quaymaster-rollback/&lt;hash(project/service)&gt;:&lt;run&gt;</c> (RT-06): always a valid lower-case repository name.</summary>
    public static string RollbackTag(string project, string service, string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        var run = NonTagCharsRe().Replace(runId.ToLowerInvariant(), string.Empty);
        return $"{RollbackPrefix}{ShortHash($"{project}/{service}")}:{run}";
    }

    public static bool IsInternalTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.StartsWith(StagedPrefix, StringComparison.Ordinal) || tag.StartsWith(RollbackPrefix, StringComparison.Ordinal);
    }

    /// <summary>Last path segment of the repository: what QM_MANAGED_SCOPE globs are matched against for images.</summary>
    public static string RepoBaseName(ImageRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r.Repo.Split('/')[^1];
    }

    /// <summary>Splits <c>repo:tag</c> at the last colon (used to tag images through the Engine API).</summary>
    public static (string Repo, string Tag) SplitRepoTag(string repoTag)
    {
        ArgumentNullException.ThrowIfNull(repoTag);
        var i = repoTag.LastIndexOf(':');
        return (repoTag[..i], repoTag[(i + 1)..]);
    }

    public static string Iso(long epochMs) => DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestRe();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagRe();

    [GeneratedRegex(@"^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoRe();

    [GeneratedRegex("[^a-z0-9_.-]", RegexOptions.CultureInvariant)]
    private static partial Regex NonTagCharsRe();
}

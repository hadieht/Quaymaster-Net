using System.Text.RegularExpressions;
using Quaymaster.Domain.Registry;
using Quaymaster.Domain.Safety;
using Quaymaster.Domain.Updates;

namespace Quaymaster.Domain.Storage;

/// <summary>A <c>labelFilter</c> of the cleanup preview: <c>key</c> or <c>key=value</c>.</summary>
public sealed partial record LabelFilter(string Key, string? Value)
{
    public static LabelFilter? Parse(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        if (!FilterRe().IsMatch(raw))
        {
            throw new FormatException("labelFilter must be key or key=value");
        }

        var eq = raw.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? new LabelFilter(raw, null) : new LabelFilter(raw[..eq], raw[(eq + 1)..]);
    }

    public bool Matches(IReadOnlyDictionary<string, string>? labels) =>
        labels is not null && labels.TryGetValue(Key, out var v) && (Value is null || v == Value);

    [GeneratedRegex("^[A-Za-z0-9_.-]+(=[A-Za-z0-9_.-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FilterRe();
}

public sealed record ImageFacts(
    string Id,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Labels,
    long ReferenceTimeMs);

public sealed record ImageContext(
    SafetyPolicy Policy,
    long NowMs,
    long MinAgeMs,
    LabelFilter? Filter,
    /// <summary>Image id → name of a container (any state, even invisible ones) that uses it.</summary>
    IReadOnlyDictionary<string, string> UsedBy,
    IReadOnlySet<string> RollbackImageIds,
    /// <summary>References (as written and as cache keys) named by discovered Compose projects.</summary>
    IReadOnlySet<string> ProjectRefs);

/// <summary>Image cleanup candidate selection (age / label / category / protection rules). Pure.</summary>
public static class ImageCleanupRules
{
    /// <summary>Why this image is outside the managed scope (null = in scope or no scope configured).</summary>
    public static string? ScopeProblem(SafetyPolicy policy, IReadOnlyList<string> tags, LabelFilter? filter)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(tags);
        if (policy.Scope.Count == 0)
        {
            return null;
        }

        var user = tags.Where(t => !ImageReferences.IsInternalTag(t)).ToList();
        if (user.Count == 0)
        {
            // an untagged image cannot be attributed to a name; only an explicit label filter proves it is ours
            return filter is not null ? null : "untagged image, and QM_MANAGED_SCOPE is set without a label filter";
        }

        foreach (var t in user)
        {
            var p = ImageReferences.Parse(t);
            if (p is null)
            {
                return $"unparseable tag {t}";
            }

            if (!(policy.InScope(ImageReferences.RepoBaseName(p)) || policy.InScope($"{p.Registry}/{p.Repo}")))
            {
                return $"{t} is outside QM_MANAGED_SCOPE";
            }
        }

        return null;
    }

    /// <summary>True when a discovered Compose project names this tag.</summary>
    public static bool NamedByProject(IReadOnlyCollection<string> tags, IReadOnlySet<string> projectRefs)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(projectRefs);
        return tags.Any(t =>
        {
            var p = ImageReferences.Parse(t);
            return projectRefs.Contains(t) || (p is not null && projectRefs.Contains(ImageReferences.CacheKey(p)));
        });
    }

    /// <summary>Protection reason for a normal (non-internal-only) image, or null when it is a cleanup candidate.</summary>
    public static string? ProtectReason(ImageFacts image, ImageContext ctx)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.UsedBy.TryGetValue(image.Id, out var user))
        {
            return $"in use by container {user}";
        }

        if (ctx.RollbackImageIds.Contains(image.Id))
        {
            return "protected as a rollback point within retention";
        }

        var userTags = image.Tags.Where(t => !ImageReferences.IsInternalTag(t)).ToList();
        if (NamedByProject(userTags, ctx.ProjectRefs))
        {
            return "referenced by a discovered Compose project";
        }

        if (image.Tags.Any(ImageReferences.IsInternalTag))
        {
            return "also carries a staged/rollback tag";
        }

        if (ScopeProblem(ctx.Policy, image.Tags, ctx.Filter) is { } sp)
        {
            return sp;
        }

        if (ctx.Filter is not null && !ctx.Filter.Matches(image.Labels))
        {
            return $"does not match label filter {ctx.Filter.Key}{(ctx.Filter.Value is null ? string.Empty : "=" + ctx.Filter.Value)}";
        }

        if (ctx.NowMs - image.ReferenceTimeMs < ctx.MinAgeMs)
        {
            return $"younger than the minimum age ({(long)Math.Round(ctx.MinAgeMs / 3_600_000.0, MidpointRounding.AwayFromZero)} h)";
        }

        return null;
    }

    /// <summary>
    /// An internal (staged / rollback) tag: <c>null</c> reason + age when expired and removable, otherwise why it is kept.
    /// </summary>
    public static (string? Protect, int AgeHours) ExpiredTag(
        string tag,
        RollbackPoint? rollback,
        StagedImage? staged,
        long retentionMs,
        long nowMs,
        SafetyPolicy policy,
        LabelFilter? filter)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(policy);
        var isRollback = tag.StartsWith(ImageReferences.RollbackPrefix, StringComparison.Ordinal);
        if (isRollback && rollback is null)
        {
            return ("no rollback record; kept", 0);
        }

        var expiresAt = rollback is not null ? rollback.ExpiresAt : staged is not null ? staged.CreatedAt + retentionMs : 0;
        if (expiresAt > nowMs)
        {
            return ($"{(isRollback ? "rollback point" : "staged image")} within retention", 0);
        }

        var outOfScope = policy.Scope.Count > 0 && (rollback?.Project is { } p ? !policy.InScope(p) : filter is null);
        if (outOfScope)
        {
            return ("outside QM_MANAGED_SCOPE", 0);
        }

        var created = rollback?.CreatedAt ?? staged?.CreatedAt ?? nowMs;
        return (null, (int)Math.Round((nowMs - created) / 3_600_000.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>Protection reason for removing one tag (single image removal): internal tags in retention, Compose-named tags.</summary>
    public static string? ProtectedTag(string tag, RollbackPoint? rollback, StagedImage? staged, long retentionMs, long nowMs, IReadOnlySet<string> projectRefs)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(projectRefs);
        if (!ImageReferences.IsInternalTag(tag))
        {
            return NamedByProject([tag], projectRefs) ? $"{tag} is named by a discovered Compose project" : null;
        }

        if (rollback is not null && rollback.ExpiresAt > nowMs)
        {
            return $"{tag} is a rollback point within retention";
        }

        if (staged is not null && staged.CreatedAt + retentionMs > nowMs)
        {
            return $"{tag} is a staged image within retention";
        }

        return tag.StartsWith(ImageReferences.RollbackPrefix, StringComparison.Ordinal) && rollback is null ? $"{tag} has no rollback record; kept" : null;
    }

    /// <summary>Bytes only this image owns: reported size minus layers shared with other images (unknown = fallback).</summary>
    public static long UniqueSize(long? size, long? shared, long fallback) =>
        size is null ? fallback : Math.Max(size.Value - (shared ?? 0), 0);
}

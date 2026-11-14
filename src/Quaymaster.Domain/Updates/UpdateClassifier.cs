using Quaymaster.Domain.Registry;

namespace Quaymaster.Domain.Updates;

/// <summary>The six states of an image reference (wire values of <c>UpdateState</c>).</summary>
public static class UpdateStates
{
    public const string UpToDate = "up_to_date";
    public const string UpdateAvailable = "update_available";
    public const string UpdateStaged = "update_staged";
    public const string LocalOnly = "local_only";
    public const string PinnedDigest = "pinned_digest";
    public const string Unknown = "unknown";
}

public sealed record RemoteError(string Kind, string Message);

/// <param name="Source"><c>registry</c> | <c>engine</c> | <c>cache</c> | <c>none</c>.</param>
public sealed record RemoteLookup(string? Digest, string Source, RemoteError? Error, long CheckedAt);

public sealed record Classification(string State, string? Reason, string? LocalDigest);

public sealed record ClassifyInput(
    ImageRef? Ref,
    string RawRef,
    IReadOnlyList<string> LocalRepoDigests,
    RemoteLookup Remote,
    IReadOnlyList<string> RemotePlatformDigests,
    bool StagedPresent,
    bool ImageIsId);

/// <summary>The digest state machine (pure): compares what the running image recorded with what the registry serves now.</summary>
public static class UpdateClassifier
{
    public static Classification Classify(ClassifyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var r = input.Ref;
        if (input.ImageIsId)
        {
            return new Classification(UpdateStates.Unknown, "the container references an image ID, not a tag", null);
        }

        if (r is null)
        {
            return new Classification(UpdateStates.Unknown, $"cannot parse image reference \"{input.RawRef}\"", null);
        }

        if (r.Digest is not null)
        {
            return new Classification(UpdateStates.PinnedDigest, "the reference is pinned to a digest; there is nothing to update", r.Digest);
        }

        var local = ImageReferences.DigestsForRepo(input.LocalRepoDigests, r);
        var remote = input.Remote;
        if (local.Count == 0 && remote.Error?.Kind == "not_found")
        {
            return new Classification(UpdateStates.LocalOnly, "not found in the registry and no registry digest recorded locally (built locally?)", null);
        }

        if (local.Count == 0 && remote.Digest is null && remote.Error is not null)
        {
            return new Classification(UpdateStates.LocalOnly, $"no registry digest recorded locally (built locally or never pulled) — {remote.Error.Message}", null);
        }

        if (remote.Error is not null || remote.Digest is null)
        {
            return new Classification(UpdateStates.Unknown, remote.Error?.Message ?? "registry did not answer", local.Count > 0 ? local[0] : null);
        }

        if (local.Count == 0)
        {
            return new Classification(UpdateStates.LocalOnly, "the local image has no digest from this repository (built locally or retagged)", null);
        }

        if (local.Contains(remote.Digest) || input.RemotePlatformDigests.Any(local.Contains))
        {
            return new Classification(UpdateStates.UpToDate, null, local.Contains(remote.Digest) ? remote.Digest : local[0]);
        }

        return input.StagedPresent
            ? new Classification(UpdateStates.UpdateStaged, "the new image is pulled and staged; the tag has not moved yet", local[0])
            : new Classification(UpdateStates.UpdateAvailable, null, local[0]);
    }
}

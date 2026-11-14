namespace Quaymaster.Domain.Storage;

/// <summary>What was measured at one moment: what <c>/system/df</c> accounts for (layers + volumes) and free bytes of the Docker root.</summary>
public sealed record UsageSnapshot(long? DfBytes, long? FreeBytes);

/// <summary>Wire shape of the reclaim report (<c>ReclaimReport</c> in shared/src/storage.ts).</summary>
public sealed record ReclaimReport(
    long EstimatedBytes,
    long? FreeSpaceDeltaBytes,
    long? DfDeltaBytes,
    long ReclaimedBytes,
    string Basis,
    string Note,
    string? Caveat);

/// <summary>The reclaimed-storage arithmetic of <c>measureReclaim</c> (pure).</summary>
public static class ReclaimMath
{
    public const string Note =
        "Reclaimed = what Docker accounts for (image layers + volumes) before minus after, capped at the size of "
        + "what was actually removed. Free-space change on the Docker root is shown too, but other processes writing "
        + "to the disk make it noisy. Layers shared with other images are freed only when the last image using them "
        + "is removed.";

    /// <param name="maxBytes">Most the run can have freed (RT3-06); null when unknown.</param>
    public static ReclaimReport Compute(UsageSnapshot before, UsageSnapshot after, long estimatedBytes, long? maxBytes)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        long? dfDelta = before.DfBytes is { } b && after.DfBytes is { } a ? Math.Max(b - a, 0) : null;
        long? freeDelta = before.FreeBytes is { } fb && after.FreeBytes is { } fa ? fa - fb : null;
        var basis = dfDelta is not null ? "df" : freeDelta is > 0 ? "statfs" : "estimate";
        var measured = basis switch
        {
            "df" => dfDelta ?? 0,
            "statfs" => freeDelta ?? 0,
            _ => estimatedBytes,
        };

        // The deltas are host-wide: another prune or a finished pull would otherwise be credited to this run.
        var capped = maxBytes is { } cap && measured > cap;
        var low = basis != "estimate" && !capped && measured < estimatedBytes;
        return new ReclaimReport(
            estimatedBytes,
            freeDelta,
            dfDelta,
            capped ? maxBytes ?? 0 : measured,
            basis,
            Note,
            capped
                ? "Docker usage dropped by more than the removed items occupied: other activity ran meanwhile, so the figure is capped."
                : low
                    ? "Docker usage grew meanwhile (for example a pull), so the measured figure is lower than the estimate."
                    : null);
    }
}

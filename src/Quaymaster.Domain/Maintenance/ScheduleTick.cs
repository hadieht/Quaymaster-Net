namespace Quaymaster.Domain.Maintenance;

public enum OccurrenceAction
{
    Fire,
    SkipMissed,
    SkipWindow,
}

/// <summary>
/// What the scheduler does with each due cron occurrence (RT-09), pure so it is testable without a clock:
/// an occurrence older than the grace period is skipped unless it is the latest one AND (for update jobs) a window is set and open;
/// an update job outside its window is skipped.
/// </summary>
public static class ScheduleTick
{
    public const long MissedGraceMs = 5 * 60_000;

    public static IReadOnlyList<OccurrenceAction> Decide(IReadOnlyList<long> due, long now, bool isUpdateJob, bool hasWindow, bool inWindow)
    {
        ArgumentNullException.ThrowIfNull(due);
        var result = new List<OccurrenceAction>(due.Count);
        for (var i = 0; i < due.Count; i++)
        {
            var missed = now - due[i] > MissedGraceMs;
            var isLatest = i == due.Count - 1;
            if (missed && !(isLatest && (isUpdateJob ? hasWindow && inWindow : true)))
            {
                result.Add(OccurrenceAction.SkipMissed);
            }
            else if (isUpdateJob && !inWindow)
            {
                result.Add(OccurrenceAction.SkipWindow);
            }
            else
            {
                result.Add(OccurrenceAction.Fire);
            }
        }

        return result;
    }
}

/// <summary>
/// Restart-loop counting: every <c>die</c> counts, deliberate or not, inside a sliding window. Pure so the monitor's rule can be unit tested.
/// </summary>
public static class RestartLoop
{
    public static (IReadOnlyList<DateTimeOffset> Window, bool IsLoop) Record(IEnumerable<DateTimeOffset> previous, DateTimeOffset now, TimeSpan window, int threshold)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var list = previous.Where(t => now - t < window).ToList();
        list.Add(now);
        return (list, list.Count >= threshold);
    }
}

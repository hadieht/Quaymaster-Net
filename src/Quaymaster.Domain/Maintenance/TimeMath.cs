using System.Globalization;
using System.Text.RegularExpressions;
using Cronos;

namespace Quaymaster.Domain.Maintenance;

/// <summary>
/// Time-zone aware cron and maintenance-window arithmetic with explicit, tested DST rules (RT-09):
/// a local time that does not exist (spring-forward gap) fires at the first valid instant after the gap; an ambiguous local time
/// (fall-back overlap) fires ONCE, at its first occurrence; window membership is computed in UTC (zoned start instant + duration), so a
/// window that spans a transition is 1 h shorter / longer in elapsed time, never ambiguous; a window's day-of-week is the day it starts and
/// <c>end &lt;= start</c> crosses midnight. All instants are Unix epoch milliseconds.
/// </summary>
public static partial class TimeMath
{
    private const long Day = 86_400_000;

    public static bool TryZone(string tz, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(tz))
        {
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    public static bool IsValidTimeZone(string tz) => TryZone(tz, out _);

    private static TimeZoneInfo Zone(string tz) => TryZone(tz, out var z) ? z : throw new ArgumentException($"unknown time zone \"{tz}\"", nameof(tz));

    public sealed record ZonedParts(int Year, int Month, int Day, int Hour, int Minute, int Second, int Dow);

    /// <summary>The wall-clock reading of instant <paramref name="utcMs"/> in <paramref name="tz"/>. <c>Dow</c>: 0 = Sunday.</summary>
    public static ZonedParts PartsOf(string tz, long utcMs)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(utcMs), Zone(tz));
        return new ZonedParts(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second, (int)local.DayOfWeek);
    }

    /// <summary>Wall clock of <paramref name="utcMs"/> in <paramref name="tz"/>, encoded as if it were UTC ("floating" time).</summary>
    public static long Floating(string tz, long utcMs)
    {
        var z = PartsOf(tz, utcMs);
        return new DateTimeOffset(z.Year, z.Month, z.Day, z.Hour, z.Minute, z.Second, TimeSpan.Zero).ToUnixTimeMilliseconds() + (((utcMs % 1000) + 1000) % 1000);
    }

    /// <summary>Offset of <paramref name="tz"/> from UTC at <paramref name="utcMs"/>, in ms (positive east of Greenwich).</summary>
    public static long OffsetAt(string tz, long utcMs)
    {
        var t = (long)Math.Floor(utcMs / 1000.0) * 1000;
        return Floating(tz, t) - t;
    }

    public enum WallKind
    {
        Normal,
        Gap,
        Ambiguous,
    }

    public sealed record Resolved(long Instant, WallKind Kind);

    /// <summary>Resolves a floating wall time to an instant under the DST rules above.</summary>
    public static Resolved WallToInstant(string tz, long wallMs)
    {
        var offs = new[] { OffsetAt(tz, wallMs - Day), OffsetAt(tz, wallMs + Day) }.Distinct().ToList();
        var valid = offs.Select(o => wallMs - o).Where(t => OffsetAt(tz, t) == wallMs - t).ToList();
        if (valid.Count == 1)
        {
            return new Resolved(valid[0], WallKind.Normal);
        }

        if (valid.Count >= 2)
        {
            return new Resolved(valid.Min(), WallKind.Ambiguous);
        }

        // gap: the first valid instant after it is the transition itself
        var guess = wallMs - offs[0];
        var preOffset = OffsetAt(tz, guess - Day);
        var lo = (long)Math.Floor(guess / 1000.0) * 1000 - Day; // offset == preOffset here
        var hi = (long)Math.Floor(guess / 1000.0) * 1000 + Day; // offset != preOffset here
        while (hi - lo > 1000)
        {
            var mid = (long)Math.Floor((lo + hi) / 2000.0) * 1000;
            if (OffsetAt(tz, mid) == preOffset)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return new Resolved(hi, WallKind.Gap);
    }

    // ---- cron ---------------------------------------------------------------------------------------------------

    /// <summary>Validates a 5-field cron expression (or an <c>@daily</c>-style alias). Returns an error message or null.</summary>
    public static string? CronError(string expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        var e = expr.Trim();
        if (!e.StartsWith('@') && WhitespaceRe().Split(e).Length != 5)
        {
            return "cron must have 5 fields: minute hour day-of-month month day-of-week";
        }

        try
        {
            _ = CronExpression.Parse(e);
            return null;
        }
        catch (CronFormatException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Next <paramref name="count"/> occurrences (UTC ms) strictly after <paramref name="afterMs"/>, using the DST rules of this class.</summary>
    public static List<long> NextOccurrences(string expr, string tz, long afterMs, int count, long untilMs = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(expr);
        var cron = CronExpression.Parse(expr.Trim());
        var output = new List<long>();
        var from = DateTimeOffset.FromUnixTimeMilliseconds(Floating(tz, afterMs) - (26 * 3_600_000L)).UtcDateTime;
        for (var guard = 0; output.Count < count && guard < 5000; guard++)
        {
            var n = cron.GetNextOccurrence(DateTime.SpecifyKind(from, DateTimeKind.Utc), TimeZoneInfo.Utc, inclusive: false);
            if (n is null)
            {
                break;
            }

            from = n.Value;
            var wall = new DateTimeOffset(DateTime.SpecifyKind(n.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var instant = WallToInstant(tz, wall).Instant;
            if (instant <= afterMs)
            {
                continue;
            }

            if (instant > untilMs)
            {
                break;
            }

            if (output.Count > 0 && output[^1] == instant)
            {
                continue; // several wall times collapsed into one gap instant
            }

            output.Add(instant);
        }

        return output;
    }

    // ---- windows ------------------------------------------------------------------------------------------------

    /// <param name="Days">0 = Sunday … 6 = Saturday, the day the window starts.</param>
    public sealed record WindowDef(IReadOnlyList<int> Days, string Start, string End, string Timezone);

    public sealed record WindowSpan(long Start, long End);

    public static int? ParseHhmm(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var m = HhmmRe().Match(s);
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60) + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : null;
    }

    public static string? WindowError(WindowDef w)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (!IsValidTimeZone(w.Timezone))
        {
            return $"unknown time zone \"{w.Timezone}\"";
        }

        var s = ParseHhmm(w.Start);
        var e = ParseHhmm(w.End);
        if (s is null || e is null)
        {
            return "start and end must be HH:MM (24 h)";
        }

        if (s == e)
        {
            return "start and end must differ (a window cannot be empty or 24 h long)";
        }

        if (w.Days.Count == 0 || w.Days.Any(d => d is < 0 or > 6))
        {
            return "days must be a non-empty list of 0 (Sunday) … 6 (Saturday)";
        }

        return null;
    }

    /// <summary>The occurrence of <paramref name="w"/> that STARTS on local calendar date (y, m, d).</summary>
    private static WindowSpan? OccurrenceOn(WindowDef w, int y, int m, int d)
    {
        var dow = (int)new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc).DayOfWeek;
        if (!w.Days.Contains(dow))
        {
            return null;
        }

        var s = ParseHhmm(w.Start)!.Value;
        var e = ParseHhmm(w.End)!.Value;
        var durMin = e > s ? e - s : e + 1440 - s;
        var wall = new DateTimeOffset(y, m, d, s / 60, s % 60, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var start = WallToInstant(w.Timezone, wall).Instant;
        return new WindowSpan(start, start + (durMin * 60_000L));
    }

    /// <summary>The window occurrence containing <paramref name="atMs"/>, or null.</summary>
    public static WindowSpan? WindowAt(WindowDef w, long atMs)
    {
        ArgumentNullException.ThrowIfNull(w);
        var z = PartsOf(w.Timezone, atMs);
        foreach (var back in new[] { 0, 1 })
        {
            var day = new DateTime(z.Year, z.Month, z.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-back);
            var occ = OccurrenceOn(w, day.Year, day.Month, day.Day);
            if (occ is not null && occ.Start <= atMs && atMs < occ.End)
            {
                return occ;
            }
        }

        return null;
    }

    /// <summary>First window occurrence beginning strictly after <paramref name="atMs"/> (searches ~8 days).</summary>
    public static WindowSpan? NextWindowStart(WindowDef w, long atMs)
    {
        ArgumentNullException.ThrowIfNull(w);
        var z = PartsOf(w.Timezone, atMs);
        for (var fwd = -1; fwd <= 8; fwd++)
        {
            var day = new DateTime(z.Year, z.Month, z.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(fwd);
            var occ = OccurrenceOn(w, day.Year, day.Month, day.Day);
            if (occ is not null && occ.Start > atMs)
            {
                return occ;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):([0-5]\d)$", RegexOptions.CultureInvariant)]
    private static partial Regex HhmmRe();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRe();
}

using FluentAssertions;
using Quaymaster.Domain.Maintenance;
using Xunit;

namespace Quaymaster.UnitTests.Maintenance;

/// <summary>Cron / window arithmetic with the explicit DST rules of RT-09 (Europe/Berlin and Australia/Sydney pinned).</summary>
public class TimeMathTests
{
    private const string Ber = "Europe/Berlin";
    private const string Syd = "Australia/Sydney";

    private static long Utc(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static long WallMs(int y, int m, int d, int h, int min) => new DateTimeOffset(y, m, d, h, min, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void Validates_iana_names_and_reads_offsets()
    {
        TimeMath.IsValidTimeZone(Ber).Should().BeTrue();
        TimeMath.IsValidTimeZone("Mars/Olympus").Should().BeFalse();
        TimeMath.OffsetAt(Ber, Utc("2026-01-15T12:00:00Z")).Should().Be(3_600_000);
        TimeMath.OffsetAt(Ber, Utc("2026-07-15T12:00:00Z")).Should().Be(7_200_000);
        TimeMath.PartsOf(Ber, Utc("2026-07-15T12:00:00Z")).Hour.Should().Be(14);
    }

    [Fact]
    public void WallToInstant_handles_normal_gap_and_ambiguous_local_times()
    {
        var gap = TimeMath.WallToInstant(Ber, WallMs(2026, 3, 29, 2, 30));
        gap.Kind.Should().Be(TimeMath.WallKind.Gap);
        Iso(gap.Instant).Should().Be("2026-03-29T01:00:00.000Z");

        var amb = TimeMath.WallToInstant(Ber, WallMs(2026, 10, 25, 2, 30));
        amb.Kind.Should().Be(TimeMath.WallKind.Ambiguous);
        Iso(amb.Instant).Should().Be("2026-10-25T00:30:00.000Z");

        var normal = TimeMath.WallToInstant(Ber, WallMs(2026, 6, 1, 2, 30));
        normal.Kind.Should().Be(TimeMath.WallKind.Normal);
        Iso(normal.Instant).Should().Be("2026-06-01T00:30:00.000Z");
    }

    [Fact]
    public void Cron_rejects_malformed_expressions()
    {
        TimeMath.CronError("30 2 * * *").Should().BeNull();
        TimeMath.CronError("@daily").Should().BeNull();
        TimeMath.CronError("* * * *").Should().Contain("5 fields");
        TimeMath.CronError("61 * * * *").Should().NotBeNull();
    }

    [Fact]
    public void Spring_forward_fires_once_at_the_first_valid_instant()
    {
        var occ = TimeMath.NextOccurrences("30 2 * * *", Ber, Utc("2026-03-28T00:00:00Z"), 3);
        occ.Select(Iso).Should().Equal("2026-03-28T01:30:00.000Z", "2026-03-29T01:00:00.000Z", "2026-03-30T00:30:00.000Z");
    }

    [Fact]
    public void Fall_back_fires_exactly_once_at_the_first_occurrence()
    {
        var occ = TimeMath.NextOccurrences("30 2 * * *", Ber, Utc("2026-10-24T00:00:00Z"), 3);
        occ.Select(Iso).Should().Equal("2026-10-24T00:30:00.000Z", "2026-10-25T00:30:00.000Z", "2026-10-26T01:30:00.000Z");
    }

    [Fact]
    public void A_frequent_job_inside_the_gap_does_not_fire_twice_at_the_same_instant()
    {
        var occ = TimeMath.NextOccurrences("*/15 2 * * *", Ber, Utc("2026-03-29T00:00:00Z"), 10, Utc("2026-03-29T02:00:00Z"));
        occ.Distinct().Count().Should().Be(occ.Count);
        occ.Count(t => t == Utc("2026-03-29T01:00:00Z")).Should().Be(1);
    }

    [Fact]
    public void Southern_hemisphere_transitions()
    {
        var start = TimeMath.NextOccurrences("30 2 * * *", Syd, Utc("2026-10-03T00:00:00Z"), 2);
        start.Select(Iso).Should().Equal("2026-10-03T16:00:00.000Z", "2026-10-04T15:30:00.000Z");
        var amb = TimeMath.WallToInstant(Syd, WallMs(2026, 4, 5, 2, 30));
        amb.Kind.Should().Be(TimeMath.WallKind.Ambiguous);
        Iso(amb.Instant).Should().Be("2026-04-04T15:30:00.000Z");
        TimeMath.NextOccurrences("30 2 5 4 *", Syd, Utc("2026-04-01T00:00:00Z"), 1).Select(Iso).Should().Equal("2026-04-04T15:30:00.000Z");
    }

    [Fact]
    public void Utc_zones_and_day_of_week()
    {
        TimeMath.NextOccurrences("0 3 * * 0", "UTC", Utc("2026-11-11T00:00:00Z"), 2).Select(Iso).Should().Equal("2026-11-15T03:00:00.000Z", "2026-11-22T03:00:00.000Z");
    }

    [Fact]
    public void An_occurrence_is_strictly_after_the_reference_instant()
    {
        TimeMath.NextOccurrences("0 3 * * *", "UTC", Utc("2026-11-15T03:00:00Z"), 1).Select(Iso).Should().Equal("2026-11-16T03:00:00.000Z");
    }
}

public class MaintenanceWindowTests
{
    private const string Ber = "Europe/Berlin";

    private static long Utc(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static TimeMath.WindowDef Win(IReadOnlyList<int>? days = null, string start = "02:00", string end = "04:00", string tz = Ber) =>
        new(days ?? [0, 1, 2, 3, 4, 5, 6], start, end, tz);

    [Fact]
    public void Validation_rejects_empty_windows_bad_zones_and_bad_days()
    {
        TimeMath.WindowError(Win()).Should().BeNull();
        TimeMath.WindowError(Win(end: "02:00")).Should().Contain("differ");
        TimeMath.WindowError(Win(tz: "Nope/Zone")).Should().Contain("time zone");
        TimeMath.WindowError(Win(days: [])).Should().Contain("days");
        TimeMath.WindowError(Win(days: [7])).Should().Contain("days");
        TimeMath.WindowError(Win(start: "2:00")).Should().Contain("HH:MM");
    }

    [Fact]
    public void Contains_instants_inside_and_excludes_the_end_instant()
    {
        var w = Win();
        TimeMath.WindowAt(w, Utc("2026-06-01T00:30:00Z")).Should().NotBeNull();
        TimeMath.WindowAt(w, Utc("2026-06-01T00:00:00Z")).Should().NotBeNull(); // 02:00 CEST exactly: open
        TimeMath.WindowAt(w, Utc("2026-06-01T02:00:00Z")).Should().BeNull(); // 04:00 CEST exactly: closed
        TimeMath.WindowAt(w, Utc("2026-06-01T12:00:00Z")).Should().BeNull();
    }

    [Fact]
    public void Midnight_crossing_day_of_week_is_the_start_day()
    {
        var w = Win(days: [6], start: "23:00", end: "02:00", tz: "UTC");
        TimeMath.WindowAt(w, Utc("2026-11-14T23:30:00Z")).Should().NotBeNull();
        TimeMath.WindowAt(w, Utc("2026-11-15T01:00:00Z")).Should().NotBeNull();
        TimeMath.WindowAt(w, Utc("2026-11-15T02:00:00Z")).Should().BeNull();
        TimeMath.WindowAt(w, Utc("2026-11-15T23:30:00Z")).Should().BeNull();
        TimeMath.WindowAt(w, Utc("2026-11-16T01:00:00Z")).Should().BeNull();
    }

    [Fact]
    public void Dst_window_is_zoned_start_plus_duration()
    {
        var w = Win();
        var spring = TimeMath.WindowAt(w, Utc("2026-03-29T01:30:00Z"));
        spring.Should().NotBeNull();
        Iso(spring!.Start).Should().Be("2026-03-29T01:00:00.000Z");
        Iso(spring.End).Should().Be("2026-03-29T03:00:00.000Z");
        var fall = TimeMath.WindowAt(w, Utc("2026-10-25T01:30:00Z"));
        fall.Should().NotBeNull();
        Iso(fall!.Start).Should().Be("2026-10-25T00:00:00.000Z");
        ((fall.End - fall.Start) / 3_600_000).Should().Be(2);
    }

    [Fact]
    public void Finds_the_next_start()
    {
        var w = Win(days: [1], tz: "UTC");
        var next = TimeMath.NextWindowStart(w, Utc("2026-11-11T10:00:00Z"));
        Iso(next!.Start).Should().Be("2026-11-16T02:00:00.000Z");
        Iso(next.End).Should().Be("2026-11-16T04:00:00.000Z");
        TimeMath.NextWindowStart(w, Utc("2026-11-16T02:00:00Z"))!.Start.Should().Be(Utc("2026-11-23T02:00:00Z"));
    }
}

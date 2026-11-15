using FluentAssertions;
using Quaymaster.Domain.Stats;
using Xunit;

namespace Quaymaster.UnitTests.Docker;

public class OverviewAggregatorTests
{
    private static RollupRow Row(string id, long minute, double cpu, double mem, long rx = 0, long tx = 0) =>
        new(id, minute, id, 6, cpu, cpu, mem, mem, rx, tx, 0, 0);

    [Fact]
    public void Empty_input_gives_no_points()
    {
        new OverviewAggregator().Build(1).Should().BeEmpty();
        new OverviewAggregator().Build(60).Should().BeEmpty();
    }

    [Fact]
    public void Sums_containers_per_minute_and_derives_network_rate()
    {
        var a = new OverviewAggregator();
        a.Add(Row("a", 10, 1, 100, 0, 0));
        a.Add(Row("a", 11, 2, 100, 600, 1200));
        a.Add(Row("b", 10, 3, 50, 0, 0));
        a.Add(Row("b", 11, 4, 50, 6000, 0));

        var pts = a.Build(1);

        pts.Should().HaveCount(2);
        pts[0].Should().Be(new OverviewPoint(10 * 60_000, 4, 150, 0, 0));
        pts[1].T.Should().Be(11 * 60_000);
        pts[1].Cpu.Should().Be(6);
        pts[1].NetRx.Should().BeApproximately(110, 1e-9);
        pts[1].NetTx.Should().BeApproximately(20, 1e-9);
    }

    [Fact]
    public void Counter_reset_is_skipped_and_gaps_use_the_real_interval()
    {
        var a = new OverviewAggregator();
        a.Add(Row("a", 10, 0, 0, 1000, 0));
        a.Add(Row("a", 12, 0, 0, 1240, 0)); // 240 B over 120 s
        a.Add(Row("a", 13, 0, 0, 5, 0)); // restart

        var pts = a.Build(1);

        pts[1].NetRx.Should().BeApproximately(2, 1e-9);
        pts[2].NetRx.Should().Be(0);
    }

    [Fact]
    public void Buckets_average_the_per_minute_totals_and_align_to_the_bucket_size()
    {
        var a = new OverviewAggregator();
        for (var m = 98; m < 104; m++)
        {
            a.Add(Row("a", m, m - 98, 10, (m - 98) * 60L));
            a.Add(Row("b", m, 1, 20));
        }

        var pts = a.Build(5);

        pts.Should().HaveCount(2);
        pts[0].T.Should().Be(95 * 60_000);
        pts[0].Cpu.Should().BeApproximately(1.5, 1e-9); // minutes 98 and 99: (0+1) and (1+1) -> (1+2)/2
        pts[1].T.Should().Be(100 * 60_000);
        pts[1].Cpu.Should().BeApproximately(((2 + 1) + (3 + 1) + (4 + 1) + (5 + 1)) / 4.0, 1e-9);
    }

    [Fact]
    public void Rate_is_computed_per_minute_before_bucketing_so_boundaries_do_not_leak()
    {
        var a = new OverviewAggregator();
        for (var m = 0; m < 10; m++)
        {
            a.Add(Row("a", m, 0, 0, m * 60L)); // exactly 1 B/s every minute
        }

        var pts = a.Build(5);

        pts.Should().HaveCount(2);
        pts[1].NetRx.Should().BeApproximately(1, 1e-9); // the bucket starting at 5 includes the 4->5 delta, no dip
        pts[0].NetRx.Should().BeApproximately(0.8, 1e-9); // first minute has no predecessor: 4 of 5 minutes carry 1 B/s
    }
}

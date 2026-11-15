using FluentAssertions;
using Quaymaster.Domain.Stats;
using Xunit;

namespace Quaymaster.UnitTests.Docker;

public class RollupBucketingTests
{
    private static RollupRow Row(long minute, int samples, double cpu, double cpuMax, double mem, double memMax, long io = 1) =>
        new("c1", minute, "web", samples, cpu, cpuMax, mem, memMax, io, io * 2, io * 3, io * 4);

    [Fact]
    public void Bucket_of_one_returns_input_unchanged()
    {
        var rows = new[] { Row(100, 6, 1, 2, 3, 4), Row(101, 6, 1, 2, 3, 4) };
        RollupBucketing.Downsample(rows, 1).Should().BeSameAs(rows);
    }

    [Fact]
    public void Empty_input_is_returned_as_is()
    {
        RollupBucketing.Downsample([], 5).Should().BeEmpty();
    }

    [Fact]
    public void Merges_into_aligned_buckets_with_weighted_average_max_and_last_cumulative_counters()
    {
        var rows = new[]
        {
            Row(100, 6, 10, 20, 100, 150),
            Row(101, 2, 50, 90, 200, 300, io: 5),
            Row(104, 4, 20, 30, 400, 450, io: 7),
            Row(105, 6, 5, 7, 10, 20),
        };

        var result = RollupBucketing.Downsample(rows, 5);

        result.Should().HaveCount(2);
        var a = result[0];
        a.Minute.Should().Be(100);
        a.Samples.Should().Be(12);
        a.CpuAvg.Should().BeApproximately((6 * 10 + 2 * 50 + 4 * 20) / 12.0, 1e-9);
        a.CpuMax.Should().Be(90);
        a.MemAvg.Should().BeApproximately((6 * 100 + 2 * 200 + 4 * 400) / 12.0, 1e-9);
        a.MemMax.Should().Be(450);
        a.NetRx.Should().Be(7);
        a.NetTx.Should().Be(14);
        a.BlkRead.Should().Be(21);
        a.BlkWrite.Should().Be(28);
        result[1].Minute.Should().Be(105);
        result[1].CpuAvg.Should().Be(5);
    }

    [Fact]
    public void Zero_sample_rows_do_not_divide_by_zero()
    {
        var result = RollupBucketing.Downsample([Row(10, 0, 4, 4, 8, 8)], 5);
        result[0].CpuAvg.Should().Be(4);
        result[0].Samples.Should().Be(0);
    }

    [Theory]
    [InlineData(60, 1)]
    [InlineData(1440, 1)]
    [InlineData(4320, 5)]
    [InlineData(10080, 15)]
    [InlineData(15000, 15)]
    [InlineData(43200, 60)]
    public void AutoBucket_keeps_short_windows_raw_and_long_ones_under_target(int window, int expected)
    {
        RollupBucketing.AutoBucket(window).Should().Be(expected);
        if (window > RollupBucketing.RawWindowMinutes)
        {
            (window / expected).Should().BeLessThanOrEqualTo(RollupBucketing.TargetPoints);
        }
    }
}

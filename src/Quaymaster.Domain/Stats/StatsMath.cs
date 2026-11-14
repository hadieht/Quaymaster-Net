namespace Quaymaster.Domain.Stats;

/// <summary>The subset of an Engine <c>stats</c> payload we read, already summed by the adapter.</summary>
public sealed record RawStats(
    DateTimeOffset Read,
    long CpuTotalNs,
    long MemoryUsage,
    long MemoryLimit,
    long MemoryInactiveFile,
    long NetRx,
    long NetTx,
    long BlkRead,
    long BlkWrite);

/// <summary>One point as streamed and stored in the ring buffer. <c>t</c> is epoch milliseconds.</summary>
public sealed record StatsPoint(long T, double Cpu, long MemUsage, long MemLimit, long NetRx, long NetTx, long BlkRead, long BlkWrite);

/// <summary>A point plus the cumulative CPU counter needed to compute the next point's CPU percentage.</summary>
public sealed record Sample(StatsPoint Point, long CpuTotalNs);

/// <summary>One finished (or in-flight) minute of samples, exactly the stats_rollups columns.</summary>
public sealed record RollupRow(
    string ContainerId,
    long Minute,
    string Name,
    int Samples,
    double CpuAvg,
    double CpuMax,
    double MemAvg,
    double MemMax,
    long NetRx,
    long NetTx,
    long BlkRead,
    long BlkWrite);

public static class StatsMath
{
    /// <summary>Memory excluding reclaimable page cache (inactive file), as <c>docker stats</c> reports it.</summary>
    public static long MemoryUsage(RawStats s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Math.Max(0, s.MemoryUsage - s.MemoryInactiveFile);
    }

    /// <summary>
    /// One-shot stats carry no <c>precpu</c>, so CPU % comes from our own previous sample of the same container (RT-26):
    /// delta(container CPU ns) / delta(wall ns) x 100 - percent of one core.
    /// </summary>
    public static Sample ToSample(RawStats s, Sample? prev)
    {
        ArgumentNullException.ThrowIfNull(s);
        var t = s.Read.ToUnixTimeMilliseconds();
        double cpu = 0;
        if (prev is not null && t > prev.Point.T && s.CpuTotalNs >= prev.CpuTotalNs)
        {
            cpu = (s.CpuTotalNs - prev.CpuTotalNs) / ((t - prev.Point.T) * 1e6) * 100;
        }

        var point = new StatsPoint(t, Math.Round(cpu * 100, MidpointRounding.AwayFromZero) / 100, MemoryUsage(s), s.MemoryLimit, s.NetRx, s.NetTx, s.BlkRead, s.BlkWrite);
        return new Sample(point, s.CpuTotalNs);
    }
}

/// <summary>Accumulates samples of one container within one minute.</summary>
public sealed class MinuteAccumulator(long minute, string name, Sample first)
{
    private int _n;
    private double _cpuSum;
    private double _cpuMax;
    private double _memSum;
    private double _memMax;
    private Sample _last = first;

    public long Minute { get; } = minute;

    public string Name { get; } = name;

    public void Add(Sample s)
    {
        ArgumentNullException.ThrowIfNull(s);
        _n++;
        _cpuSum += s.Point.Cpu;
        _cpuMax = Math.Max(_cpuMax, s.Point.Cpu);
        _memSum += s.Point.MemUsage;
        _memMax = Math.Max(_memMax, s.Point.MemUsage);
        _last = s;
    }

    public RollupRow ToRow(string containerId) => new(
        containerId,
        Minute,
        Name,
        _n,
        _n == 0 ? 0 : _cpuSum / _n,
        _cpuMax,
        _n == 0 ? 0 : _memSum / _n,
        _memMax,
        _last.Point.NetRx,
        _last.Point.NetTx,
        _last.Point.BlkRead,
        _last.Point.BlkWrite);
}

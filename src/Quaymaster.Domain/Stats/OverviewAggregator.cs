namespace Quaymaster.Domain.Stats;

/// <summary>One point of the host-wide overview: totals over all containers, network as bytes per second.</summary>
public sealed record OverviewPoint(long T, double Cpu, double Mem, double NetRx, double NetTx);

/// <summary>
/// Streaming aggregation of per-container roll-ups into the host-wide overview. Feed rows ordered by container, then minute;
/// memory stays proportional to the number of minutes in the window, never to the number of rows.
/// </summary>
public sealed class OverviewAggregator
{
    private readonly SortedDictionary<long, Totals> _byMinute = [];
    private readonly Dictionary<string, (long Minute, long Rx, long Tx)> _previous = new(StringComparer.Ordinal);

    /// <summary>Adds one roll-up. Rows of one container must arrive in ascending minute order.</summary>
    public void Add(RollupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!_byMinute.TryGetValue(row.Minute, out var t))
        {
            t = new Totals();
            _byMinute[row.Minute] = t;
        }

        t.Cpu += row.CpuAvg;
        t.Mem += row.MemAvg;
        // Network counters are cumulative: a rate needs two samples, and a drop means a restart (skipped).
        if (_previous.TryGetValue(row.ContainerId, out var before) && row.Minute > before.Minute && row.NetRx >= before.Rx && row.NetTx >= before.Tx)
        {
            var secs = (row.Minute - before.Minute) * 60.0;
            t.NetRx += (row.NetRx - before.Rx) / secs;
            t.NetTx += (row.NetTx - before.Tx) / secs;
        }

        _previous[row.ContainerId] = (row.Minute, row.NetRx, row.NetTx);
    }

    /// <summary>
    /// The points: one per minute for a bucket of 1, otherwise one per aligned bucket (<c>T</c> = bucket start) holding the
    /// average of the per-minute totals inside it. Rates are computed per minute before bucketing, so they stay correct across bucket borders.
    /// </summary>
    public IReadOnlyList<OverviewPoint> Build(int bucketMinutes)
    {
        var size = Math.Max(bucketMinutes, 1);
        var result = new List<OverviewPoint>();
        long current = long.MinValue;
        double cpu = 0, mem = 0, rx = 0, tx = 0;
        var count = 0;
        foreach (var (minute, t) in _byMinute)
        {
            var start = minute / size * size;
            if (start != current)
            {
                Flush();
                current = start;
            }

            cpu += t.Cpu;
            mem += t.Mem;
            rx += t.NetRx;
            tx += t.NetTx;
            count++;
        }

        Flush();
        return result;

        void Flush()
        {
            if (count > 0)
            {
                result.Add(new OverviewPoint(current * 60_000, cpu / count, mem / count, rx / count, tx / count));
            }

            cpu = mem = rx = tx = 0;
            count = 0;
        }
    }

    private sealed class Totals
    {
        public double Cpu { get; set; }

        public double Mem { get; set; }

        public double NetRx { get; set; }

        public double NetTx { get; set; }
    }
}

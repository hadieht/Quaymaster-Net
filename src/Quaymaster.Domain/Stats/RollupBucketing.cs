namespace Quaymaster.Domain.Stats;

/// <summary>Downsamples 1-minute roll-ups into coarser buckets so long chart ranges stay small.</summary>
public static class RollupBucketing
{
    /// <summary>Windows up to this many minutes are served as raw 1-minute roll-ups unless a bucket is requested.</summary>
    public const int RawWindowMinutes = 24 * 60;

    /// <summary>Target maximum number of points per series for automatic bucketing.</summary>
    public const int TargetPoints = 1000;

    private static readonly int[] Steps = [1, 2, 5, 10, 15, 30, 60, 120, 240, 360, 720, 1440];

    /// <summary>Bucket size used when the caller did not ask for one: 1 for windows up to 24 h, otherwise the smallest step giving at most <see cref="TargetPoints"/> points.</summary>
    public static int AutoBucket(long windowMinutes)
    {
        if (windowMinutes <= RawWindowMinutes)
        {
            return 1;
        }

        foreach (var step in Steps)
        {
            if (windowMinutes <= (long)step * TargetPoints)
            {
                return step;
            }
        }

        return Steps[^1];
    }

    /// <summary>
    /// Merges roll-ups (one container, ordered by minute) into buckets aligned to <paramref name="bucketMinutes"/> (epoch minutes).
    /// CPU/memory averages are weighted by sample count, maxima are maxima, network and block columns are cumulative counters (last value of each minute), so a bucket keeps the last one and the consumer derives rates from consecutive buckets.
    /// <c>Minute</c> of a result row is the bucket start. A bucket of 1 returns the input unchanged.
    /// </summary>
    public static IReadOnlyList<RollupRow> Downsample(IReadOnlyList<RollupRow> rows, int bucketMinutes)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (bucketMinutes <= 1 || rows.Count == 0)
        {
            return rows;
        }

        var result = new List<RollupRow>();
        var i = 0;
        while (i < rows.Count)
        {
            var first = rows[i];
            var start = first.Minute / bucketMinutes * bucketMinutes;
            double weight = 0, cpu = 0, mem = 0, cpuMax = 0, memMax = 0;
            long rx = 0, tx = 0, br = 0, bw = 0;
            var samples = 0;
            var last = first;
            while (i < rows.Count && rows[i].Minute < start + bucketMinutes)
            {
                var r = rows[i];
                var w = Math.Max(r.Samples, 1);
                weight += w;
                cpu += r.CpuAvg * w;
                mem += r.MemAvg * w;
                cpuMax = Math.Max(cpuMax, r.CpuMax);
                memMax = Math.Max(memMax, r.MemMax);
                samples += r.Samples;
                rx = r.NetRx;
                tx = r.NetTx;
                br = r.BlkRead;
                bw = r.BlkWrite;
                last = r;
                i++;
            }

            result.Add(new RollupRow(first.ContainerId, start, last.Name, samples, cpu / weight, cpuMax, mem / weight, memMax, rx, tx, br, bw));
        }

        return result;
    }
}

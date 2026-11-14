using Microsoft.Extensions.Logging;
using Quaymaster.Application.Background;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Streaming;
using Quaymaster.Domain.Stats;

namespace Quaymaster.Application.Stats;

/// <summary>A new point for a container, as streamed on <c>/stream/stats</c> (<c>stats</c> event).</summary>
public sealed record StatsSampleEvent(string Id, string Name, StatsPoint Point);

public interface IStatsSampler
{
    IReadOnlyList<StatsPoint> History(string containerId);

    IReadOnlyDictionary<string, StatsPoint> Latest();
}

/// <summary>
/// Collects one stats sample per running container every <c>QM_STATS_INTERVAL_SECONDS</c> (one-shot, two at a time),
/// keeps a ring buffer of the last 360 points per container and 1-minute roll-ups in the database. Port of
/// <c>docker/stats.ts</c>.
/// </summary>
public sealed partial class StatsSampler(
    IDockerEngine engine,
    IContainerService containers,
    IStatsStore store,
    QmConfig config,
    Broadcaster<StatsSampleEvent> feed,
    TimeProvider time,
    ILogger<StatsSampler> log) : IQMBackgroundTask, IStatsSampler
{
    public const int RingSize = 360;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Sample> _prev = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<StatsPoint>> _ring = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MinuteAccumulator> _acc = new(StringComparer.Ordinal);
    private int _running;

    public string Name => "stats-sampler";

    public IReadOnlyList<StatsPoint> History(string containerId)
    {
        lock (_gate)
        {
            return _ring.TryGetValue(containerId, out var pts) ? [.. pts] : [];
        }
    }

    public IReadOnlyDictionary<string, StatsPoint> Latest()
    {
        lock (_gate)
        {
            return _ring.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value[^1], StringComparer.Ordinal);
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(config.Stats.Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            await FlushRollupsAsync(true);
        }
    }

    /// <summary>Samples all running containers once. Exposed for tests.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var list = (await containers.ListRawAsync(true, ct)).Where(c => c.State == "running").ToList();
            var alive = list.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            lock (_gate)
            {
                foreach (var id in _prev.Keys.Where(k => !alive.Contains(k)).ToList())
                {
                    _prev.Remove(id);
                }

                foreach (var id in _ring.Keys.Where(k => !alive.Contains(k)).ToList())
                {
                    _ring.Remove(id);
                }
            }

            var queue = new Queue<ContainerInfo>(list);
            async Task WorkerAsync()
            {
                while (true)
                {
                    ContainerInfo? c;
                    lock (queue)
                    {
                        if (!queue.TryDequeue(out c))
                        {
                            return;
                        }
                    }

                    try
                    {
                        var raw = await engine.StatsOnceAsync(c.Id, ct);
                        if (raw is not null)
                        {
                            Record(c.Id, c.Name, raw);
                        }
                    }
                    catch (Errors.AppException e)
                    {
                        Log.SampleFailed(log, c.Id[..12], e.Message);
                    }
                }
            }

            await Task.WhenAll(WorkerAsync(), WorkerAsync());
            await FlushRollupsAsync(false);
        }
        catch (Errors.AppException e)
        {
            Log.TickFailed(log, e.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Records one raw sample (CPU % needs two samples: the first only primes the baseline).</summary>
    public void Record(string id, string name, RawStats raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        StatsPoint point;
        MinuteAccumulator? finished = null;
        lock (_gate)
        {
            var first = !_prev.TryGetValue(id, out var prev);
            var sample = StatsMath.ToSample(raw, prev);
            _prev[id] = sample;
            if (first)
            {
                return;
            }

            point = sample.Point;
            if (!_ring.TryGetValue(id, out var pts))
            {
                pts = [];
                _ring[id] = pts;
            }

            pts.Add(point);
            if (pts.Count > RingSize)
            {
                pts.RemoveAt(0);
            }

            var minute = sample.Point.T / 60_000;
            if (_acc.TryGetValue(id, out var acc) && acc.Minute != minute)
            {
                finished = acc;
                _acc.Remove(id);
            }

            if (!_acc.TryGetValue(id, out acc))
            {
                acc = new MinuteAccumulator(minute, name, sample);
                _acc[id] = acc;
            }

            acc.Add(sample);
        }

        if (finished is not null)
        {
            PersistFireAndForget([finished.ToRow(id)]);
        }

        feed.Publish(new StatsSampleEvent(id, name, point));
    }

    private void PersistFireAndForget(IReadOnlyList<RollupRow> rows) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await store.UpsertManyAsync(rows, CancellationToken.None);
            }
#pragma warning disable CA1031
            catch (Exception e)
#pragma warning restore CA1031
            {
                Log.PersistFailed(log, e);
            }
        });

    /// <summary>Persists accumulators of finished minutes (or all of them when <paramref name="all"/>).</summary>
    public async Task FlushRollupsAsync(bool all)
    {
        var nowMin = time.GetUtcNow().ToUnixTimeMilliseconds() / 60_000;
        var rows = new List<RollupRow>();
        lock (_gate)
        {
            foreach (var (id, a) in _acc.ToList())
            {
                if (all || a.Minute < nowMin)
                {
                    rows.Add(a.ToRow(id));
                    _acc.Remove(id);
                }
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            await store.UpsertManyAsync(rows, CancellationToken.None);
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            Log.PersistFailed(log, e);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 130, Level = LogLevel.Debug, Message = "Stats sample failed for {Id}: {Reason}")]
        public static partial void SampleFailed(ILogger logger, string id, string reason);

        [LoggerMessage(EventId = 131, Level = LogLevel.Warning, Message = "Stats tick failed: {Reason}")]
        public static partial void TickFailed(ILogger logger, string reason);

        [LoggerMessage(EventId = 132, Level = LogLevel.Error, Message = "Failed to persist stats roll-ups")]
        public static partial void PersistFailed(ILogger logger, Exception exception);
    }
}

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Background;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Streaming;
using Quaymaster.Domain.Docker;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.Application.Events;

/// <summary>
/// One long-lived <c>/events</c> stream with reconnect + backoff. After every reconnect a full inventory re-sync
/// synthesises state-change records for events the daemon's small replay buffer lost. Persistence is batched; fan-out
/// happens through the <see cref="Broadcaster{T}"/>. Port of <c>docker/events.ts</c>.
/// </summary>
public sealed partial class EventWatcher(
    IDockerEngine engine,
    IContainerService containers,
    IEventStore store,
    IIgnoreList ignore,
    Scrubber scrubber,
    Broadcaster<DockerEventRecord> feed,
    TimeProvider time,
    ILogger<EventWatcher> log) : IQMBackgroundTask, IEventStreamStatus
{
    private static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly Channel<NewEvent> _pending = Channel.CreateUnbounded<NewEvent>();
    private readonly HashSet<string> _recent = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentOrder = new();
    private Dictionary<string, KnownContainer> _known = [];
    private long _lastSeenSec = time.GetUtcNow().ToUnixTimeSeconds();
    private volatile bool _connected;

    public string Name => "docker-events";

    public bool Connected => _connected;

    /// <summary>Backoff schedule; tests shorten it.</summary>
    public TimeSpan[] Backoff { get; init; } = DefaultBackoff;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var last = await store.LastTimestampAsync(stoppingToken);
        if (last is { } l)
        {
            _lastSeenSec = l / 1000;
        }

        await TrySnapshotAsync(false, stoppingToken);
        var flusher = FlushLoopAsync(stoppingToken);
        var attempt = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReadStreamAsync(() => attempt = 0, stoppingToken);
                }
                catch (AppException e)
                {
                    Log.StreamUnavailable(log, e.Message);
                }
                catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    Log.StreamUnavailable(log, e.Message);
                }

                _connected = false;
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                var wait = Backoff[Math.Min(attempt++, Backoff.Length - 1)];
                await Task.Delay(wait, time, stoppingToken);
                await TrySnapshotAsync(true, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            _connected = false;
            _pending.Writer.TryComplete();
            await flusher;
        }
    }

    /// <summary>Re-reads the inventory without emitting "missed event" records (used when the ignore list changes).</summary>
    public Task RebaselineAsync(CancellationToken ct) => TrySnapshotAsync(false, ct);

    private async Task ReadStreamAsync(Action onConnected, CancellationToken ct)
    {
        await foreach (var raw in engine.EventsAsync(DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, _lastSeenSec - 1)), ct))
        {
            if (!_connected)
            {
                _connected = true;
                onConnected();
            }

            await HandleAsync(raw, ct);
        }
    }

    private async Task TrySnapshotAsync(bool emitDiff, CancellationToken ct)
    {
        try
        {
            var list = await containers.ListRawAsync(true, ct);
            var after = list.ToDictionary(c => c.Id, c => new KnownContainer(c.State, c.Name, c.Project, c.Service, c.Image), StringComparer.Ordinal);
            if (emitDiff)
            {
                foreach (var e in InventoryDiff.Diff(_known, after, time.GetUtcNow().ToUnixTimeMilliseconds()))
                {
                    _pending.Writer.TryWrite(e);
                }
            }

            _known = after;
        }
        catch (AppException e)
        {
            Log.SnapshotFailed(log, e.Message);
        }
    }

    /// <summary>Processes one raw event (public for tests).</summary>
    public async Task HandleAsync(System.Text.Json.Nodes.JsonObject raw, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Num("time") is { } sec)
        {
            _lastSeenSec = Math.Max(_lastSeenSec, sec);
        }

        var type = raw.Str("Type");
        if (type == "container")
        {
            containers.Invalidate();
        }

        var key = $"{raw.Num("timeNano") ?? raw.Num("time")}|{raw.Obj("Actor")?.Str("ID")}|{raw.Str("Action") ?? raw.Str("status")}";
        if (!_recent.Add(key))
        {
            return; // overlap from `since` on reconnect
        }

        _recentOrder.Enqueue(key);
        if (_recentOrder.Count > 2000)
        {
            _recent.Remove(_recentOrder.Dequeue());
        }

        // containers on the "stopped monitoring" list produce no events, history or notifications
        if (type == "container")
        {
            await ignore.RefreshAsync(ct);
            var attrs = raw.Obj("Actor")?.Obj("Attributes").StringMap() ?? [];
            if (ignore.IsContainerIgnored(attrs.GetValueOrDefault("name") ?? string.Empty, attrs))
            {
                return;
            }
        }

        var ev = EventSanitizer.Sanitize(raw, scrubber, time.GetUtcNow().ToUnixTimeMilliseconds());
        var policy = containers.Policy;
        if (ev is not null && (!policy.VisibilityRestricted || (ev.Name is not null && policy.Visible(ev.Name))))
        {
            _pending.Writer.TryWrite(ev);
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var reader = _pending.Reader;
        var batch = new List<NewEvent>();
        try
        {
            while (await reader.WaitToReadAsync(CancellationToken.None))
            {
                batch.Clear();
                var deadline = time.GetTimestamp();
                while (batch.Count < 100)
                {
                    if (reader.TryRead(out var e))
                    {
                        batch.Add(e);
                        continue;
                    }

                    if (time.GetElapsedTime(deadline) >= TimeSpan.FromSeconds(1) || ct.IsCancellationRequested)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), time, CancellationToken.None);
                }

                await FlushAsync(batch);
            }
        }
        catch (ChannelClosedException)
        {
            // completed
        }
    }

    private async Task FlushAsync(List<NewEvent> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            var ids = await store.InsertManyAsync(batch, CancellationToken.None);
            for (var i = 0; i < batch.Count; i++)
            {
                var b = batch[i];
                feed.Publish(new DockerEventRecord(
                    i < ids.Count ? ids[i] : 0,
                    Auth.Iso.Format(DateTimeOffset.FromUnixTimeMilliseconds(b.Ts)),
                    b.Type,
                    b.Action,
                    b.ActorId,
                    b.Name,
                    b.Image,
                    b.Project,
                    b.Service,
                    b.Attributes,
                    b.Synthetic));
            }
        }
#pragma warning disable CA1031 // persistence problems must not stop the watcher
        catch (Exception e)
#pragma warning restore CA1031
        {
            Log.PersistFailed(log, e);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 120, Level = LogLevel.Warning, Message = "Docker event stream unavailable: {Reason}")]
        public static partial void StreamUnavailable(ILogger logger, string reason);

        [LoggerMessage(EventId = 121, Level = LogLevel.Warning, Message = "Inventory snapshot for events failed: {Reason}")]
        public static partial void SnapshotFailed(ILogger logger, string reason);

        [LoggerMessage(EventId = 122, Level = LogLevel.Error, Message = "Failed to persist events")]
        public static partial void PersistFailed(ILogger logger, Exception exception);
    }
}

/// <summary><c>GET /events</c> and <c>GET /audit</c> reads.</summary>
public interface IEventQueries
{
    Task<IReadOnlyList<DockerEventRecord>> ListAsync(EventQuery query, CancellationToken ct);
}

public sealed class EventQueries(IEventStore store, IIgnoreList ignore) : IEventQueries
{
    public async Task<IReadOnlyList<DockerEventRecord>> ListAsync(EventQuery query, CancellationToken ct)
    {
        await ignore.RefreshAsync(ct);
        return (await store.ListAsync(query, ct)).Where(e => !ignore.HidesRecord(e.Project, e.Name)).ToList();
    }
}

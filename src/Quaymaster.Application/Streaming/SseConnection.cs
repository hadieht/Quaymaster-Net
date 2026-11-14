using System.Text.Json;
using System.Threading.Channels;

namespace Quaymaster.Application.Streaming;

/// <summary>One line group on the wire: <c>event: name\ndata: json\n\n</c>, or a comment (<c>: text</c>) when <see cref="Event"/> is null.</summary>
public sealed record SseFrame(string? Event, string Data)
{
    public int Size => (Event?.Length ?? 0) + Data.Length + 16;

    public string ToWire() => Event is null ? $": {Data}\n\n" : $"event: {Event}\ndata: {Data}\n\n";
}

public sealed record SseLimits(int MaxBufferBytes, TimeSpan Heartbeat)
{
    public static readonly SseLimits Default = new(1024 * 1024, TimeSpan.FromSeconds(15));
}

/// <summary>A live stream to one client. The HTTP layer drains <see cref="Frames"/> into the response.</summary>
public interface ISseConnection
{
    string Id { get; }

    ChannelReader<SseFrame> Frames { get; }

    /// <summary>Cancelled when the connection closes for any reason.</summary>
    CancellationToken Closed { get; }

    /// <summary>Queues an event. False when the connection is closed or the client cannot keep up (which closes it).</summary>
    bool Send(string eventName, object? data);

    /// <summary>Sends the final <c>end {reason}</c> event and completes the stream.</summary>
    void Close(string reason);

    void OnClose(Action callback);

    /// <summary>Called by the writer after a frame left the process, releasing its share of the buffer budget.</summary>
    void Written(SseFrame frame);
}

public interface ISseHub
{
    int Count { get; }

    /// <summary>Opens a stream bound to the caller's session; throws 429 when a per-session or total limit is reached.</summary>
    ISseConnection Open(Auth.AuthContext auth);
}

/// <summary>
/// Frame queue with back-pressure: a slow client must not make the server buffer without bound. When more than
/// <see cref="SseLimits.MaxBufferBytes"/> are waiting the connection is closed (<c>end</c> reason "client too slow; reconnect").
/// Heartbeats are comment frames every <see cref="SseLimits.Heartbeat"/>.
/// </summary>
public sealed class SseConnection : ISseConnection, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Channel<SseFrame> _channel = Channel.CreateUnbounded<SseFrame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _closed = new();
    private readonly SseLimits _limits;
    private readonly List<Action> _closers = [];
    private readonly Lock _gate = new();
    private readonly ITimer _heartbeat;
    private long _queuedBytes;
    private bool _isClosed;

    public SseConnection(SseLimits limits, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);
        _limits = limits;
        _heartbeat = time.CreateTimer(_ => Enqueue(new SseFrame(null, "ping"), force: false), null, limits.Heartbeat, limits.Heartbeat);
    }

    public string Id { get; } = Guid.NewGuid().ToString();

    public ChannelReader<SseFrame> Frames => _channel.Reader;

    public CancellationToken Closed => _closed.Token;

    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _isClosed;
            }
        }
    }

    /// <summary>Called by the writer after it wrote a frame, to release its share of the buffer budget.</summary>
    public void Written(SseFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Interlocked.Add(ref _queuedBytes, -frame.Size);
    }

    public bool Send(string eventName, object? data)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        return Enqueue(new SseFrame(eventName, JsonSerializer.Serialize(data, Json)), force: false);
    }

    private bool Enqueue(SseFrame frame, bool force)
    {
        lock (_gate)
        {
            if (_isClosed)
            {
                return false;
            }
        }

        var queued = Interlocked.Add(ref _queuedBytes, frame.Size);
        if (!force && queued > _limits.MaxBufferBytes)
        {
            Close("client too slow; reconnect");
            return false;
        }

        return _channel.Writer.TryWrite(frame);
    }

    public void Close(string reason)
    {
        List<Action> closers;
        lock (_gate)
        {
            if (_isClosed)
            {
                return;
            }

            _isClosed = true;
            closers = [.. _closers];
            _closers.Clear();
        }

        _heartbeat.Dispose();
        _channel.Writer.TryWrite(new SseFrame("end", JsonSerializer.Serialize(new { reason }, Json)));
        _channel.Writer.TryComplete();
        _closed.Cancel();
        foreach (var c in closers)
        {
#pragma warning disable CA1031 // a failing close callback must not stop the others
            try
            {
                c();
            }
            catch (Exception)
            {
                // ignore
            }
#pragma warning restore CA1031
        }
    }

    public void OnClose(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var runNow = false;
        lock (_gate)
        {
            if (_isClosed)
            {
                runNow = true;
            }
            else
            {
                _closers.Add(callback);
            }
        }

        if (runNow)
        {
            callback();
        }
    }

    public void Dispose()
    {
        Close("closed");
        _closed.Dispose();
    }
}

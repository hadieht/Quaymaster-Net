namespace Quaymaster.Application.Streaming;

/// <summary>Tiny in-process pub/sub. A throwing subscriber never affects the others or the publisher.</summary>
public sealed class Broadcaster<T>
{
    private readonly Lock _gate = new();
    private Action<T>[] _handlers = [];

    public int SubscriberCount => Volatile.Read(ref _handlers).Length;

    public IDisposable Subscribe(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers = [.. _handlers, handler];
        }

        return new Subscription(this, handler);
    }

    public void Publish(T value)
    {
        foreach (var h in Volatile.Read(ref _handlers))
        {
#pragma warning disable CA1031 // isolation between subscribers is the point
            try
            {
                h(value);
            }
            catch (Exception)
            {
                // a failing subscriber (e.g. a closed SSE connection) must not break the feed
            }
#pragma warning restore CA1031
        }
    }

    private sealed class Subscription(Broadcaster<T> owner, Action<T> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._handlers = [.. owner._handlers.Where(h => h != handler)];
            }
        }
    }
}

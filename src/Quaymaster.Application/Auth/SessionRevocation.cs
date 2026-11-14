namespace Quaymaster.Application.Auth;

/// <summary>
/// Raised when sessions die early (logout, privilege change, password change, expiry sweep). Live streams (SSE, later
/// phases) subscribe to close themselves. An event with neither property set means "re-validate everything".
/// </summary>
public sealed record SessionRevocation(IReadOnlyList<string>? SessionIdHashes = null, long? UserId = null);

public interface ISessionRevocationBus
{
    void Publish(SessionRevocation revocation);

    IDisposable Subscribe(Action<SessionRevocation> handler);
}

public sealed class SessionRevocationBus : ISessionRevocationBus
{
    private readonly Lock _gate = new();
    private List<Action<SessionRevocation>> _handlers = [];

    public void Publish(SessionRevocation revocation)
    {
        Action<SessionRevocation>[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _handlers];
        }
        foreach (var handler in snapshot)
        {
            handler(revocation);
        }
    }

    public IDisposable Subscribe(Action<SessionRevocation> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers = [.. _handlers, handler];
        }
        return new Subscription(this, handler);
    }

    private sealed class Subscription(SessionRevocationBus bus, Action<SessionRevocation> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (bus._gate)
            {
                bus._handlers = bus._handlers.Where(h => h != handler).ToList();
            }
        }
    }
}

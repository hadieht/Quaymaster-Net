using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Streaming;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Infrastructure.Streaming;

/// <summary>
/// Registry of live SSE streams (RT-15). Every stream belongs to a session: logout, expiry, password or role change close
/// it; it re-validates every 60 s, is capped per session and in total, and is dropped when the client cannot keep up.
/// </summary>
public sealed class SseHub : ISseHub, IStreamCounter, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _streams = new(StringComparer.Ordinal);
    private readonly ISessionService _sessions;
    private readonly SseOptions _options;
    private readonly SseLimits _limits;
    private readonly TimeProvider _time;
    private readonly TimeSpan _revalidate;
    private readonly IDisposable _subscription;

    public SseHub(ISessionService sessions, ISessionRevocationBus bus, SseOptions options, TimeProvider time, SseLimits? limits = null, TimeSpan? revalidateEvery = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(bus);
        _sessions = sessions;
        _options = options;
        _time = time;
        _limits = limits ?? SseLimits.Default;
        _revalidate = revalidateEvery ?? TimeSpan.FromSeconds(60);
        _subscription = bus.Subscribe(Revoke);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _streams.Count;
            }
        }
    }

    /// <summary>Opens a connection for the session or throws 429 when a limit is reached.</summary>
    public ISseConnection Open(AuthContext auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        SseConnection conn;
        lock (_gate)
        {
            var perSession = _streams.Values.Count(s => s.SessionIdHash == auth.Session.IdHash);
            if (perSession >= _options.MaxPerSession)
            {
                throw new AppException(429, ErrorCode.RateLimited, $"At most {_options.MaxPerSession} live streams per session");
            }

            if (_streams.Count >= _options.MaxTotal)
            {
                throw new AppException(429, ErrorCode.RateLimited, "Too many live streams on this server");
            }

            conn = new SseConnection(_limits, _time);
            _streams[conn.Id] = new Entry(conn, auth.Session.IdHash, auth.User.Id, auth.User.Role);
        }

        // periodic re-validation and the absolute session lifetime
        var timer = _time.CreateTimer(_ => _ = RevalidateAsync(conn, auth), null, _revalidate, _revalidate);
        var remaining = auth.Session.ExpiresAt - _time.GetUtcNow();
        var expiry = _time.CreateTimer(_ => conn.Close("session expired"), null, remaining < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining, Timeout.InfiniteTimeSpan);
        conn.OnClose(() =>
        {
            timer.Dispose();
            expiry.Dispose();
            lock (_gate)
            {
                _streams.Remove(conn.Id);
            }
        });
        return conn;
    }

    private async Task RevalidateAsync(SseConnection conn, AuthContext auth)
    {
        try
        {
            if (!await _sessions.IsStillValidAsync(auth.Session.IdHash, auth.User.Role, CancellationToken.None))
            {
                conn.Close("session ended");
            }
        }
#pragma warning disable CA1031 // a failing check must not leave an unvalidated stream open: fail closed
        catch (Exception)
#pragma warning restore CA1031
        {
            conn.Close("session ended");
        }
    }

    private void Revoke(SessionRevocation e)
    {
        List<Entry> all;
        lock (_gate)
        {
            all = [.. _streams.Values];
        }

        var sweep = e.SessionIdHashes is null && e.UserId is null;
        foreach (var entry in all)
        {
            var bySession = e.SessionIdHashes?.Contains(entry.SessionIdHash) == true;
            var byUser = e.UserId is { } uid && entry.UserId == uid;
            if (bySession || byUser)
            {
                entry.Conn.Close("session revoked");
            }
            else if (sweep)
            {
                _ = RevalidateSweepAsync(entry);
            }
        }
    }

    private async Task RevalidateSweepAsync(Entry entry)
    {
        try
        {
            if (!await _sessions.IsStillValidAsync(entry.SessionIdHash, entry.Role, CancellationToken.None))
            {
                entry.Conn.Close("session expired");
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            entry.Conn.Close("session expired");
        }
    }

    public void CloseAll(string reason)
    {
        List<Entry> all;
        lock (_gate)
        {
            all = [.. _streams.Values];
        }

        foreach (var e in all)
        {
            e.Conn.Close(reason);
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        CloseAll("server stopping");
    }

    private sealed record Entry(SseConnection Conn, string SessionIdHash, long UserId, Role Role);
}

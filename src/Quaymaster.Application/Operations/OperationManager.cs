using Microsoft.Extensions.Logging;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Domain.Redaction;

namespace Quaymaster.Application.Operations;

public sealed record NewOperation(string Id, string Kind, string Target, string Actor, object? Params, string? ComposeVersion);

public interface IOperationStore
{
    Task CreateAsync(NewOperation operation, CancellationToken ct);

    /// <summary>Appends output, keeping only the newest 256 KiB.</summary>
    Task AppendOutputAsync(string id, string text, CancellationToken ct);

    Task FinishAsync(string id, string status, string? failure, CancellationToken ct);

    Task<OperationInfoDto?> GetAsync(string id, CancellationToken ct);

    Task<IReadOnlyList<OperationInfoDto>> ListAsync(int limit, CancellationToken ct);

    /// <summary>Startup: operations left <c>running</c> by a crash are marked failed/interrupted (never resumed).</summary>
    Task<int> MarkInterruptedAsync(CancellationToken ct);
}

/// <summary>Live event of one operation: output text, or the final (or initial) status.</summary>
public abstract record OperationEvent
{
    public sealed record Output(string Text) : OperationEvent;

    public sealed record Status(string State, string? Error) : OperationEvent;
}

public interface IOperationContext
{
    string Id { get; }

    /// <summary>Appends scrubbed output to the operation record and its live stream.</summary>
    void Log(string text);

    /// <summary>True once a graceful shutdown started: long runs must not begin new steps (RT-14).</summary>
    bool ShuttingDown { get; }
}

public sealed record OperationStart(
    string Kind,
    string Target,
    string Actor,
    object? Params,
    string LockKey,
    Func<IOperationContext, Task> Run,
    string? ComposeVersion = null,
    string? Id = null);

public interface IOperationManager
{
    bool Busy { get; }

    bool IsLocked(string key);

    Task<int> RecoverAsync(CancellationToken ct);

    /// <summary>Starts the work in the background and returns the operation id at once. A second operation on the same lock key is a 409.</summary>
    Task<string> StartAsync(OperationStart operation, CancellationToken ct);

    Task<OperationInfoDto?> GetAsync(string id, CancellationToken ct);

    Task<IReadOnlyList<OperationInfoDto>> ListAsync(int limit, CancellationToken ct);

    /// <summary>Subscribes to live events (buffered output is replayed first). Null when the operation does not exist.</summary>
    Task<IDisposable?> SubscribeAsync(string id, Action<OperationEvent> handler, CancellationToken ct);

    Task DrainAsync(TimeSpan timeout);
}

/// <summary>
/// Tracks long-running operations (Compose runs): persisted record, live output for SSE, and a per-key lock so two
/// operations on the same project never overlap. Port of <c>operations/manager.ts</c>.
/// </summary>
public sealed partial class OperationManager(IOperationStore store, Scrubber scrubber, TimeProvider time, ILogger<OperationManager> log) : IOperationManager
{
    private const int BufferCap = 256 * 1024;

    private readonly IOperationStore _store = store;
    private readonly Scrubber _scrubber = scrubber;
    private readonly ILogger<OperationManager> _log = log;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _locks = new(StringComparer.Ordinal);
    private volatile bool _shuttingDown;

    public bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _locks.Count > 0;
            }
        }
    }

    public bool IsLocked(string key)
    {
        lock (_gate)
        {
            return _locks.ContainsKey(key);
        }
    }

    public Task<int> RecoverAsync(CancellationToken ct) => _store.MarkInterruptedAsync(ct);

    public async Task<string> StartAsync(OperationStart operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_shuttingDown)
        {
            throw AppException.Conflict("Quaymaster is shutting down");
        }

        var id = operation.Id ?? Guid.NewGuid().ToString();
        var live = new Live();
        lock (_gate)
        {
            if (_locks.TryGetValue(operation.LockKey, out var holder))
            {
                throw AppException.Conflict($"Another operation ({holder}) is already running on {operation.LockKey}");
            }

            _locks[operation.LockKey] = id;
            _live[id] = live;
        }

        try
        {
            await _store.CreateAsync(new NewOperation(id, operation.Kind, operation.Target, operation.Actor, operation.Params, operation.ComposeVersion), ct);
        }
        catch
        {
            lock (_gate)
            {
                _locks.Remove(operation.LockKey);
                _live.Remove(id);
            }

            throw;
        }

        var context = new Context(this, id, live);
        _ = Task.Run(() => RunAsync(operation, context, live), CancellationToken.None);
        return id;
    }

    private async Task RunAsync(OperationStart operation, Context context, Live live)
    {
        string? error = null;
        try
        {
            await operation.Run(context);
        }
#pragma warning disable CA1031 // any failure of the work becomes the operation's recorded error
        catch (Exception e)
#pragma warning restore CA1031
        {
            error = Redactor.RedactText(_scrubber.Scrub(e.Message));
            context.Log($"\nERROR: {error}\n");
        }

        // Release the lock as soon as the work is done, so other operations on the same key can start.
        // The operation result is persisted afterwards; the lock only serialises the work itself.
        lock (_gate)
        {
            _locks.Remove(operation.LockKey);
        }

        try
        {
            await _store.FinishAsync(context.Id, error is null ? "succeeded" : "failed", error, CancellationToken.None);
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            Log.FinishFailed(_log, e, context.Id);
        }

        live.Publish(new OperationEvent.Status(error is null ? "succeeded" : "failed", error), done: true);

        // keep the replay buffer for a minute for late subscribers, then fall back to the database
        _ = Task.Delay(TimeSpan.FromSeconds(60), time, CancellationToken.None).ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    _live.Remove(context.Id);
                }
            },
            TaskScheduler.Default);
    }

    public Task<OperationInfoDto?> GetAsync(string id, CancellationToken ct) => _store.GetAsync(id, ct);

    public Task<IReadOnlyList<OperationInfoDto>> ListAsync(int limit, CancellationToken ct) => _store.ListAsync(limit, ct);

    public async Task<IDisposable?> SubscribeAsync(string id, Action<OperationEvent> handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var info = await _store.GetAsync(id, ct);
        if (info is null)
        {
            return null;
        }

        Live? live;
        lock (_gate)
        {
            _live.TryGetValue(id, out live);
        }

        if (live is null)
        {
            // finished earlier (or before a restart): replay from the database
            if (info.Output.Length > 0)
            {
                handler(new OperationEvent.Output(info.Output));
            }

            handler(new OperationEvent.Status(info.Status, info.Error));
            return NullDisposable.Instance;
        }

        return live.Subscribe(handler);
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        _shuttingDown = true;
        var started = time.GetTimestamp();
        while (Busy && time.GetElapsedTime(started) < timeout)
        {
            await Task.Delay(200);
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Context(OperationManager owner, string id, Live live) : IOperationContext
    {
        public string Id { get; } = id;

        public bool ShuttingDown => owner._shuttingDown;

        public void Log(string text)
        {
            var clean = Redactor.RedactText(owner._scrubber.Scrub(text));
            if (clean.Length == 0)
            {
                return;
            }

            try
            {
                owner._store.AppendOutputAsync(Id, clean, CancellationToken.None).GetAwaiter().GetResult();
            }
#pragma warning disable CA1031
            catch (Exception e)
#pragma warning restore CA1031
            {
                OperationManager.Log.PersistFailed(owner._log, e);
            }

            live.Publish(new OperationEvent.Output(clean), done: false);
        }
    }

    private sealed class Live
    {
        private readonly Lock _lock = new();
        private readonly List<Action<OperationEvent>> _handlers = [];
        private string _buffer = string.Empty;
        private OperationEvent.Status? _final;

        public void Publish(OperationEvent e, bool done)
        {
            Action<OperationEvent>[] snapshot;
            lock (_lock)
            {
                if (e is OperationEvent.Output o)
                {
                    var joined = _buffer + o.Text;
                    _buffer = joined.Length > BufferCap ? joined[^BufferCap..] : joined;
                }

                if (done && e is OperationEvent.Status s)
                {
                    _final = s;
                }

                snapshot = [.. _handlers];
            }

            foreach (var h in snapshot)
            {
                h(e);
            }
        }

        public IDisposable Subscribe(Action<OperationEvent> handler)
        {
            lock (_lock)
            {
                if (_buffer.Length > 0)
                {
                    handler(new OperationEvent.Output(_buffer));
                }

                if (_final is not null)
                {
                    handler(_final);
                    return NullDisposable.Instance;
                }

                _handlers.Add(handler);
            }

            return new Unsubscribe(this, handler);
        }

        private sealed class Unsubscribe(Live live, Action<OperationEvent> handler) : IDisposable
        {
            public void Dispose()
            {
                lock (live._lock)
                {
                    live._handlers.Remove(handler);
                }
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 110, Level = LogLevel.Error, Message = "Could not persist the result of operation {Id}")]
        public static partial void FinishFailed(ILogger logger, Exception exception, string id);

        [LoggerMessage(EventId = 111, Level = LogLevel.Error, Message = "Could not persist operation output")]
        public static partial void PersistFailed(ILogger logger, Exception exception);
    }
}

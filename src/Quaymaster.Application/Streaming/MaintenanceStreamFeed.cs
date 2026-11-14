using Quaymaster.Application.Maintenance;
using Quaymaster.Domain.Maintenance;

namespace Quaymaster.Application.Streaming;

/// <summary>Feeds <c>GET /stream/maintenance</c>: a snapshot of the latest runs, then a <c>run</c> event whenever a run changes.</summary>
public interface IMaintenanceStreamFeed
{
    /// <summary>Sends the snapshot (one <c>snapshot</c> event per run, oldest first) and starts polling for changes until the connection closes.</summary>
    Task AttachAsync(ISseConnection connection, CancellationToken ct);
}

/// <summary>
/// Run progress for the live view (port of the original, which polls every 2 s): a run is re-sent when its status, finish time or number
/// of steps changed. The poll runs on the host's <see cref="TimeProvider"/> so tests drive it with a fake clock.
/// </summary>
public sealed class MaintenanceStreamFeed(IRunStore runs, TimeProvider time) : IMaintenanceStreamFeed
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public const int RunsInSnapshot = 20;

    public async Task AttachAsync(ISseConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var gate = new SemaphoreSlim(1, 1);
        await PushAsync(connection, seen, first: true, ct);
        var timer = time.CreateTimer(
            _ => _ = TickAsync(connection, seen, gate),
            null,
            PollInterval,
            PollInterval);
        connection.OnClose(() =>
        {
            timer.Dispose();
            gate.Dispose();
        });
    }

    private async Task TickAsync(ISseConnection connection, Dictionary<string, string> seen, SemaphoreSlim gate)
    {
        if (connection.Closed.IsCancellationRequested || !await gate.WaitAsync(0, CancellationToken.None))
        {
            return;
        }

        try
        {
            await PushAsync(connection, seen, first: false, connection.Closed);
        }
        catch (Errors.AppException)
        {
            // a failing poll must not end the stream; the next tick retries
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        catch (ObjectDisposedException)
        {
            // closed while polling
        }
        finally
        {
            try
            {
                _ = gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // closed while polling
            }
        }
    }

    private async Task PushAsync(ISseConnection connection, Dictionary<string, string> seen, bool first, CancellationToken ct)
    {
        var latest = await runs.ListAsync(RunsInSnapshot, null, null, ct);
        foreach (var r in latest.Reverse())
        {
            // a running run gains steps without changing status: its step count is part of the signature
            var steps = r.Status == RunStatuses.Running ? (await runs.GetAsync(r.Id, ct))?.Steps.Count ?? 0 : -1;
            var signature = $"{r.Status}|{r.FinishedAt}|{steps}";
            if (seen.TryGetValue(r.Id, out var old) && old == signature)
            {
                continue;
            }

            seen[r.Id] = signature;
            if (!connection.Send(first ? "snapshot" : "run", r))
            {
                return;
            }
        }
    }
}

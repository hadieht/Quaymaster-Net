using Quaymaster.Api.Http;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Events;
using Quaymaster.Application.Operations;
using Quaymaster.Application.Stats;
using Quaymaster.Application.Streaming;
using Quaymaster.Application.Validation;
using Quaymaster.Domain.Identity;


namespace Quaymaster.Api.Endpoints;

/// <summary>
/// Server-Sent Events. Each stream is registered in the <see cref="SseHub"/> and bound to the caller's session (closed on
/// logout, expiry, role or password change; re-validated every 60 s; capped per session and in total; heartbeats; closed
/// when the client cannot keep up). Final event: <c>end {reason}</c>.
/// </summary>
public static class StreamEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var logs = api.MapGroup(string.Empty).RequireCapability(Capability.Logs);

        api.MapGet("/stream/events", async (HttpContext http, ISseHub hub, IEventQueries events, Application.Streaming.Broadcaster<DockerEventRecord> feed) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var sinceId = q.Number64InRange("sinceId", 0, long.MaxValue);
            q.ThrowIfInvalid();
            var conn = hub.Open(http.RequireAuth());
            if (sinceId is { } since)
            {
                var replay = await events.ListAsync(new EventQuery(200, null, since), http.RequestAborted);
                foreach (var e in replay.Reverse())
                {
                    conn.Send("docker-event", e);
                }
            }

            var sub = feed.Subscribe(e => conn.Send("docker-event", e));
            conn.OnClose(sub.Dispose);
            return new SseResult(conn);
        });

        api.MapGet("/stream/stats", (HttpContext http, ISseHub hub, IStatsSampler stats, Application.Streaming.Broadcaster<StatsSampleEvent> feed) =>
        {
            var conn = hub.Open(http.RequireAuth());
            conn.Send("snapshot", stats.Latest());
            var sub = feed.Subscribe(e => conn.Send("stats", e));
            conn.OnClose(sub.Dispose);
            return new SseResult(conn);
        });

        logs.MapGet("/stream/logs/{reference}", async (HttpContext http, string reference, ISseHub hub, IContainerService containers) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var tail = q.NumberInRange("tail", 0, 2000, 100) ?? 100;
            var timestamps = q.Enum("timestamps", "true", "false");
            q.ThrowIfInvalid();

            // resolve first so 404/400 are reported as normal JSON errors, before the stream is opened
            var container = await containers.ResolveAsync(reference, http.RequestAborted);
            var conn = hub.Open(http.RequireAuth());
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var line in containers.LogsFollowAsync(container, tail, timestamps != "false", conn.Closed))
                    {
                        if (!conn.Send("log", line))
                        {
                            return;
                        }
                    }

                    conn.Close("container log stream ended");
                }
                catch (OperationCanceledException)
                {
                    // connection closed
                }
                catch (AppException e)
                {
                    conn.Send("error", new { message = e.Message });
                    conn.Close("error");
                }
            }, CancellationToken.None);
            return new SseResult(conn);
        });

        api.MapGet("/stream/operations/{id}", async (HttpContext http, string id, ISseHub hub, IOperationManager operations) =>
        {
            if (await operations.GetAsync(id, http.RequestAborted) is null)
            {
                throw AppException.NotFound("Operation");
            }

            var conn = hub.Open(http.RequireAuth());
            var sub = await operations.SubscribeAsync(
                id,
                e =>
                {
                    switch (e)
                    {
                        case OperationEvent.Output o:
                            conn.Send("output", new { text = o.Text });
                            break;
                        case OperationEvent.Status s:
                            conn.Send("status", new { status = s.State, error = s.Error });
                            if (s.State != "running")
                            {
                                _ = Task.Delay(50).ContinueWith(_ => conn.Close("operation finished"), TaskScheduler.Default);
                            }

                            break;
                    }
                },
                http.RequestAborted);
            if (sub is not null)
            {
                conn.OnClose(sub.Dispose);
            }

            return new SseResult(conn);
        });

        // Run progress: `snapshot` (one per recent run, oldest first), then `run` whenever a run changes (polled every 2 s).
        api.MapGet("/stream/maintenance", async (HttpContext http, ISseHub hub, Application.Streaming.IMaintenanceStreamFeed feed) =>
        {
            var conn = hub.Open(http.RequireAuth());
            await feed.AttachAsync(conn, http.RequestAborted);
            return new SseResult(conn);
        });

        // Live output of one run. A run id is also its operation id, so this is /stream/operations/:id with the run checked first;
        // a run whose operation record is gone (older than the operation history, or from before a restart) answers with its final status.
        api.MapGet("/stream/maintenance/runs/{id}", async (HttpContext http, string id, ISseHub hub, IOperationManager operations, Application.Maintenance.IRunService runs) =>
        {
            var run = await runs.GetAsync(id, http.RequestAborted);
            var conn = hub.Open(http.RequireAuth());
            if (await operations.GetAsync(id, http.RequestAborted) is null)
            {
                conn.Send("status", new { status = run.Status, error = run.Reason });
                conn.Close("operation finished");
                return new SseResult(conn);
            }

            var sub = await operations.SubscribeAsync(
                id,
                e =>
                {
                    switch (e)
                    {
                        case OperationEvent.Output o:
                            conn.Send("output", new { text = o.Text });
                            break;
                        case OperationEvent.Status s:
                            conn.Send("status", new { status = s.State, error = s.Error });
                            if (s.State != "running")
                            {
                                _ = Task.Delay(50).ContinueWith(_ => conn.Close("operation finished"), TaskScheduler.Default);
                            }

                            break;
                    }
                },
                http.RequestAborted);
            if (sub is not null)
            {
                conn.OnClose(sub.Dispose);
            }

            return new SseResult(conn);
        });
    }
}

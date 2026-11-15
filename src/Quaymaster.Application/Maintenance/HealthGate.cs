using Quaymaster.Domain.Maintenance;

namespace Quaymaster.Application.Maintenance;

/// <summary>HTTP probe used by the health gate: resolves to the status code, throws on a network error.</summary>
public interface IHttpProbe
{
    Task<int> StatusAsync(string url, CancellationToken ct);
}

/// <summary>Poll interval and sleep seam of the health gate (tests advance a fake clock instead of waiting).</summary>
public sealed class HealthTimings
{
    public int PollMs { get; init; } = 1000;

    /// <summary>Null = <c>Task.Delay</c> on the host's <see cref="TimeProvider"/>.</summary>
    public Func<int, CancellationToken, Task>? Sleep { get; init; }
}

/// <summary>
/// The health gate (spec 7.5 step 8 + RT-10):
/// healthcheck present - wait for <c>healthy</c>; <c>unhealthy</c>, an exit or a restart loop fail at once;
/// no healthcheck - running, not restarting, restart count and <c>StartedAt</c> stable for the stabilisation period;
/// one-shot - success = exited with code 0 within the timeout; then the optional HTTP probe.
/// Every container must run the expected image (RT-06: a mismatch is a health failure).
/// </summary>
public sealed class HealthGate(IRunEnvironment env, IHttpProbe probe, HealthTimings timings, TimeProvider time)
{
    public async Task<HealthResult> WaitAsync(IReadOnlyList<string> containerIds, HealthSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(containerIds);
        ArgumentNullException.ThrowIfNull(spec);
        long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();
        Task Sleep() => timings.Sleep is { } s ? s(timings.PollMs, ct) : Task.Delay(TimeSpan.FromMilliseconds(timings.PollMs), time, ct);
        var t0 = Now();
        var deadline = t0 + spec.TimeoutMs;
        HealthResult Fail(string reason) => new(false, reason, Now() - t0);
        if (containerIds.Count == 0)
        {
            return Fail("no container to check");
        }

        var baseline = new Dictionary<string, (int Restarts, string StartedAt)>(StringComparer.Ordinal);
        var stableSince = new Dictionary<string, long>(StringComparer.Ordinal);
        var last = "no data";
        while (true)
        {
            var allOk = true;
            foreach (var id in containerIds)
            {
                InspectLite? c;
                try
                {
                    c = await env.InspectAsync(id, ct);
                }
                catch (Exception e) when (e is Errors.AppException or HttpRequestException or IOException or TimeoutException)
                {
                    // F-06: a Docker blip is "no data yet" and retried until the deadline - then a timeout failure (rollback + guidance)
                    allOk = false;
                    last = $"Docker error while inspecting: {e.Message}";
                    continue;
                }

                if (c is null)
                {
                    return Fail($"container {Short(id)} disappeared");
                }

                if (c.Image != spec.ExpectedImageId)
                {
                    return Fail($"container {Short(id)} runs image {Short19(c.Image)}, expected {Short19(spec.ExpectedImageId)}");
                }

                if (!baseline.TryGetValue(id, out var b))
                {
                    b = (c.RestartCount, c.StartedAt);
                    baseline[id] = b;
                }

                if (spec.Kind == "oneshot")
                {
                    if (c.Status is "exited" or "dead")
                    {
                        if (c.ExitCode != 0)
                        {
                            return Fail($"one-shot container exited with code {c.ExitCode}");
                        }

                        continue;
                    }

                    allOk = false;
                    last = $"one-shot container is {c.Status}";
                    continue;
                }

                if (c.Status is "exited" or "dead")
                {
                    return Fail($"container exited with code {c.ExitCode}");
                }

                if (c.RestartCount > b.Restarts)
                {
                    return Fail($"restart loop: restart count went {b.Restarts} → {c.RestartCount}");
                }

                if (c.Restarting)
                {
                    allOk = false;
                    last = "container is restarting";
                    stableSince.Remove(id);
                    continue;
                }

                if (spec.Kind == "healthcheck" && c.Health is { } h && h != "none")
                {
                    if (h == "unhealthy")
                    {
                        return Fail("the healthcheck reports unhealthy");
                    }

                    if (h != "healthy")
                    {
                        allOk = false;
                        last = $"health is {h}";
                    }

                    continue;
                }

                // stability check (also the fallback when a healthcheck was expected but the image defines none)
                if (!c.Running)
                {
                    allOk = false;
                    last = $"container is {c.Status}";
                    stableSince.Remove(id);
                    continue;
                }

                if (c.StartedAt != b.StartedAt)
                {
                    return Fail("container was restarted during the stabilisation period");
                }

                if (!stableSince.TryGetValue(id, out var since))
                {
                    since = Now();
                    stableSince[id] = since;
                }

                if (Now() - since < spec.StabilizationMs)
                {
                    allOk = false;
                    last = $"stabilising ({(Now() - since + 500) / 1000}s of {(spec.StabilizationMs + 500) / 1000}s)";
                }
            }

            if (allOk)
            {
                break;
            }

            if (Now() >= deadline)
            {
                return Fail($"timed out after {(spec.TimeoutMs + 500) / 1000}s: {last}");
            }

            await Sleep();
        }

        if (spec.Probe is { } p)
        {
            while (true)
            {
                string lastErr;
                try
                {
                    var status = await probe.StatusAsync(p.Url, ct);
                    if (status == p.ExpectStatus)
                    {
                        return new HealthResult(true, "healthy", Now() - t0);
                    }

                    lastErr = $"probe answered HTTP {status}, expected {p.ExpectStatus}";
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    lastErr = $"probe failed: {e.Message}";
                }

                if (Now() >= deadline)
                {
                    return Fail($"HTTP probe did not pass within the timeout ({lastErr})");
                }

                await Sleep();
            }
        }

        return new HealthResult(true, "healthy", Now() - t0);
    }

    private static string Short(string id) => id[..Math.Min(12, id.Length)];

    private static string Short19(string id) => id[..Math.Min(19, id.Length)];
}

using Quaymaster.Api.Http;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Validation;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Maintenance;

namespace Quaymaster.Api.Endpoints;

/// <summary>
/// Endpoints the web UI added on top of the core API (port of the original routes/webapi.ts): read-only settings
/// overview, the caller's own sessions, cron preview and the host-wide stats overview.
/// </summary>
public static class WebApiEndpoints
{
    /// <summary>Reported as <c>version</c> in <c>/system/settings</c> (the root package.json version).</summary>
    public const string Version = "0.1.0";

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        // Operational, non-secret configuration (read-only; changed through environment variables).
        api.MapGet("/system/settings", (QmConfig c) => new
        {
            version = Version,
            retention = new
            {
                eventDays = c.EventRetentionDays,
                auditDays = c.AuditRetentionDays,
                statsHours = c.Stats.RetentionHours,
                rollbackDays = (int)Math.Round(c.RollbackRetention.TotalDays, MidpointRounding.AwayFromZero),
                cleanupMinAgeHours = (int)Math.Round(c.CleanupMinAge.TotalHours, MidpointRounding.AwayFromZero),
                approvalExpiryHours = (int)Math.Round(c.ApprovalExpiry.TotalHours, MidpointRounding.AwayFromZero),
            },
            session = new
            {
                idleMinutes = (int)Math.Round(c.Session.IdleTimeout.TotalMinutes, MidpointRounding.AwayFromZero),
                absoluteHours = (int)Math.Round(c.Session.AbsoluteLifetime.TotalHours, MidpointRounding.AwayFromZero),
            },
            limits = new { sseMaxPerSession = c.Sse.MaxPerSession, sseMaxTotal = c.Sse.MaxTotal, diskFloorBytes = c.DiskFloorBytes },
            security = new { cookieSecure = c.CookieSecure, publicOrigins = c.PublicOrigins, redactAllEnv = c.RedactAllEnv, protectedNames = c.ProtectedNames },
        });

        // The caller's own sessions (never anyone else's).
        api.MapGet("/auth/sessions", async (HttpContext http, ISessionRepository sessions) =>
        {
            var me = http.GetAuth();
            if (me is null)
            {
                return new { sessions = Array.Empty<object>() };
            }

            var rows = await sessions.ListForUserAsync(me.User.Id, http.RequestAborted);
            return new
            {
                sessions = rows.Select(s => (object)new
                {
                    id = s.IdHash,
                    createdAt = Iso.Format(s.CreatedAt),
                    lastSeenAt = Iso.Format(s.LastSeen),
                    expiresAt = Iso.Format(s.ExpiresAt),
                    ip = s.Ip,
                    userAgent = s.UserAgent is { Length: > 200 } ua ? ua[..200] : s.UserAgent,
                    current = s.IdHash == me.Session.IdHash,
                }).ToArray(),
            };
        });

        // Revokes one of the caller's own sessions (not the current one); anything else is a 404.
        api.MapDelete("/auth/sessions/{id}", async (HttpContext http, string id, ISessionRepository sessions, ISessionRevocationBus bus, IAuditLog audit) =>
        {
            var me = http.RequireAuth();
            var row = await sessions.FindAsync(id, CancellationToken.None);
            if (row is null || row.UserId != me.User.Id || row.IdHash == me.Session.IdHash)
            {
                throw AppException.NotFound("Session");
            }

            await sessions.DeleteAsync(id, CancellationToken.None);
            bus.Publish(new SessionRevocation([id]));
            await audit.RecordAsync(new AuditRecord(me.User.Username, "auth.session_revoke", AuditOutcome.Success, http.ClientIp()), CancellationToken.None);
            return new OkDto();
        });

        // Validates a cron expression and lists the next runs (same DST rules the scheduler uses).
        api.MapGet("/maintenance/cron/preview", (HttpContext http, TimeProvider time) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var cron = q.Text("cron", 200);
            var tz = q.Text("timezone", 100);
            q.ThrowIfInvalid();
            if (string.IsNullOrEmpty(cron) || string.IsNullOrEmpty(tz))
            {
                throw AppException.BadRequest("cron and timezone are required");
            }

            if (!TimeMath.IsValidTimeZone(tz))
            {
                return new { valid = false, error = (string?)$"Unknown time zone \"{tz}\"", nextRuns = Array.Empty<string>() };
            }

            var err = TimeMath.CronError(cron);
            if (err is not null)
            {
                return new { valid = false, error = (string?)err, nextRuns = Array.Empty<string>() };
            }

            var runs = TimeMath.NextOccurrences(cron, tz, time.GetUtcNow().ToUnixTimeMilliseconds(), 5)
                .Select(t => Iso.Format(DateTimeOffset.FromUnixTimeMilliseconds(t))).ToArray();
            return new { valid = true, error = (string?)null, nextRuns = runs };
        });

        // Host-wide container resource history (1-minute roll-ups, summed over all containers).
        api.MapGet("/stats/overview", async (HttpContext http, IStatsStore store, TimeProvider time) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            var minutes = q.NumberInRange("minutes", 5, 1440, 60) ?? 60;
            q.ThrowIfInvalid();
            var nowMin = time.GetUtcNow().ToUnixTimeMilliseconds() / 60_000;
            var rows = await store.SinceAsync(nowMin - minutes, 100_000, http.RequestAborted);
            var byMinute = new SortedDictionary<long, OverviewPoint>();
            var prev = new Dictionary<string, (long Minute, long Rx, long Tx)>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (!byMinute.TryGetValue(r.Minute, out var p))
                {
                    p = new OverviewPoint { T = r.Minute * 60_000 };
                    byMinute[r.Minute] = p;
                }

                p.Cpu += r.CpuAvg;
                p.Mem += r.MemAvg;
                // Network counters are cumulative: a rate needs two samples, and a drop means a restart (skipped).
                if (prev.TryGetValue(r.ContainerId, out var before) && r.Minute > before.Minute && r.NetRx >= before.Rx && r.NetTx >= before.Tx)
                {
                    var secs = (r.Minute - before.Minute) * 60.0;
                    p.NetRx += (r.NetRx - before.Rx) / secs;
                    p.NetTx += (r.NetTx - before.Tx) / secs;
                }

                prev[r.ContainerId] = (r.Minute, r.NetRx, r.NetTx);
            }

            return new { points = byMinute.Values.Select(p => new { t = p.T, cpu = p.Cpu, mem = p.Mem, netRx = p.NetRx, netTx = p.NetTx }).ToArray() };
        });
    }

    private sealed class OverviewPoint
    {
        public long T { get; init; }

        public double Cpu { get; set; }

        public double Mem { get; set; }

        public double NetRx { get; set; }

        public double NetTx { get; set; }
    }
}

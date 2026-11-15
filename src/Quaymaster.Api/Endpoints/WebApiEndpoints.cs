using Quaymaster.Api.Http;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Configuration;
using Quaymaster.Application.Docker;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Validation;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Stats;

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

        // Host-wide container resource history (1-minute roll-ups, summed over all containers; long windows are bucketed).
        api.MapGet("/stats/overview", async (HttpContext http, IStatsStore store, TimeProvider time) =>
        {
            var q = new QueryReader(k => http.Request.Query[k].FirstOrDefault());
            const int maxMinutes = 30 * 24 * 60; // = the QM_STATS_RETENTION_HOURS ceiling (720 h)
            var minutes = q.NumberInRange("minutes", 5, maxMinutes, 60) ?? 60;
            var bucket = q.NumberInRange("bucket", 1, 1440);
            var sinceParam = q.Number64InRange("since", 0, long.MaxValue);
            var untilParam = q.Number64InRange("until", 0, long.MaxValue);
            q.ThrowIfInvalid();
            var nowMinute = time.GetUtcNow().ToUnixTimeMilliseconds() / 60_000;
            var until = untilParam ?? nowMinute;
            var since = sinceParam ?? (until - minutes);
            if (until <= since || until - since > maxMinutes)
            {
                throw AppException.BadRequest($"since/until must span 1..{maxMinutes} minutes (epoch minutes)");
            }

            var aggregator = new OverviewAggregator();
            await foreach (var row in store.StreamAsync(since, until, http.RequestAborted))
            {
                aggregator.Add(row);
            }

            var points = aggregator.Build(bucket ?? RollupBucketing.AutoBucket(until - since));
            return new { points = points.Select(p => new { t = p.T, cpu = p.Cpu, mem = p.Mem, netRx = p.NetRx, netTx = p.NetTx }).ToArray() };
        });
    }
}

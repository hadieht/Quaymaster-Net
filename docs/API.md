# Quaymaster HTTP API

Base path `/api/v1`. JSON in, JSON out. This is the API of Quaymaster **(.NET)**, regenerated from the implementation
(`src/Quaymaster.Api/Endpoints/*` and the DTOs in `src/Quaymaster.Application`). The TypeScript types the web UI uses are
in `shared/src/api.ts`, `maintenance.ts`, `notifications.ts` and `storage.ts`.

> **Compatibility.** The port is contract-compatible with the original Node.js Quaymaster: the same routes under
> `/api/v1`, the same JSON shapes, the same error envelope and codes, the same cookie + CSRF scheme and the same SSE event
> names. The unmodified React frontend runs against it. Sections marked **= original** are identical to the original's
> `docs/API.md`; sections marked **extra** exist in the original's code (the web UI uses them) but were not in its
> documentation, or are new here. [Differences from the original's API document](#differences-from-the-originals-api-document)
> lists what changed in the text.

## Conventions

**Auth.** A session cookie, `qm_session` (or `__Host-qm_session` when `QM_COOKIE_SECURE=true`), `HttpOnly; SameSite=Strict`.
Everything under `/api/` needs it except `GET /api/v1/auth/status`, `POST /api/v1/auth/login`, `POST /api/v1/auth/setup`
(and `GET /healthz`, which is outside `/api/`).

**State-changing requests** (anything except `GET`, `HEAD`, `OPTIONS`) need an `Origin` header that exactly matches a
configured origin (`QM_PUBLIC_ORIGIN`; default `http://<host>:<port>` plus the loopback aliases) and, once logged in, the header
`X-QM-CSRF: <csrfToken>` returned by login, setup and `GET /auth/me`. A browser sends `Origin` itself; for `curl` add
`-H "Origin: http://127.0.0.1:8787"`.

**Roles.** `viewer` = read; `admin` = everything. Container logs (and the log stream) need the separate `logs:read`
permission: admins always, viewers only when `logsRead` is granted (default off). "Admin" below means the `Capability.Write`
check, answered with 403 `forbidden` for others; "logs" means the logs check.

**Bodies** are strict: unknown properties are rejected with 400 `validation`; at most 64 KiB. **Queries** are validated too
(unknown or malformed values: 400).

**Errors** always use

```json
{
  "error": {
    "code": "validation",
    "message": "Invalid request",
    "details": [{ "path": "", "message": "Unrecognized key(s) in object: 'x'" }]
  }
}
```

`details` is optional (validation issues, `expected` for confirmations, `requires` for volumes ...). Status and code:

| Code                    | HTTP                             | Meaning                                                                                                  |
| ----------------------- | -------------------------------- | -------------------------------------------------------------------------------------------------------- |
| `validation`            | 400                              | Bad body, query or path value; unknown action.                                                           |
| `unauthorized`          | 401                              | No or expired session (also for unknown `/api/` paths when not logged in).                               |
| `forbidden`             | 403                              | Role or permission missing; invalid setup token; other admin's preview.                                  |
| `csrf`                  | 403                              | `X-QM-CSRF` missing or wrong.                                                                            |
| `origin`                | 403                              | `Origin` missing or not an allowed origin.                                                               |
| `not_found`             | 404                              | Unknown resource, or unknown route (`Route not found`, also for a wrong method on a known route).        |
| `conflict`              | 409                              | State conflict (name taken, preview missing/expired, nothing to update, another operation running, ...). |
| `read_only`             | 409                              | `QM_READ_ONLY` is set (or Quaymaster could not identify its own container).                              |
| `out_of_scope`          | 409 (403 on `POST /images/pull`) | Target outside `QM_MANAGED_SCOPE`.                                                                       |
| `self_protected`        | 409                              | Quaymaster itself, its project/volumes/networks, or a `QM_PROTECTED` name.                               |
| `confirmation_required` | 409                              | Typed confirmation missing or wrong; `details.expected` holds the expected text.                         |
| `blocked_drift`         | 409                              | Compose files differ from the running containers (redacted diff in `details`).                           |
| `limited_capability`    | 409                              | The project's files are not trusted/readable; only start/stop/restart work.                              |
| `ignored`               | 409                              | The target is on the "stopped monitoring" list.                                                          |
| `unsupported`           | 409                              | For example standalone `recreate`, or a Compose-managed container sent to the standalone updater.        |
| `approval_stale`        | 409                              | The plan changed since it was approved.                                                                  |
| `window_closed`         | 409                              | The maintenance window is closed.                                                                        |
| `insufficient_disk`     | 409 or 507                       | Not enough (or unknown) free space before/while pulling.                                                 |
| `rate_limited`          | 429                              | Login/setup budget, hash queue, or SSE stream limit; `Retry-After` header where applicable.              |
| `compose_failed`        | 422 or 502                       | `docker compose` failed or returned unusable output.                                                     |
| `registry_error`        | 502                              | The registry or the daemon's pull failed.                                                                |
| `docker_unavailable`    | 503                              | The Docker daemon is not reachable.                                                                      |
| `setup_required`        | n/a                              | Defined in the contract; not currently returned by any route.                                            |
| `internal`              | 500                              | Unexpected error (message is generic; see the logs).                                                     |

Unauthenticated requests to **unknown** `/api/` paths get 401 (authentication runs first); authenticated ones get 404.

**Times** are ISO-8601 UTC strings. **Sizes** are bytes.

## Public (= original)

| Route                     | Body / response                                                                                                              |
| ------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `GET /healthz`            | `{"status":"ok"}` only. Outside `/api/`; no session. Answers after the database is ready.                                    |
| `GET /api/v1/auth/status` | `{setupRequired, authenticated}`                                                                                             |
| `POST /api/v1/auth/setup` | `{token, username, password}` → `SessionInfo` + cookie. Creates the first admin with the one-time token; 409 if users exist. |
| `POST /api/v1/auth/login` | `{username, password}` → `SessionInfo` + cookie. 401 on bad credentials, 429 when limited.                                   |

`SessionInfo` = `{user: {id, username, role, logsRead, disabled, createdAt}, csrfToken, expiresAt, idleExpiresAt}`.

## Session and users (= original)

- `POST /auth/logout`, `POST /auth/logout-all` → `{ok:true}`; the cookie is cleared.
- `GET /auth/me` → `SessionInfo`.
- `POST /auth/password {current, next}` → revokes the user's _other_ sessions. `next`: 12 to 1024 characters.
- Admin: `GET /users` → `UserInfo[]`; `POST /users {username, password, role, logsRead?}` → 201 `UserInfo`;
  `PATCH /users/:id {role?, logsRead?, disabled?}` (revokes that user's sessions and live streams);
  `POST /users/:id/password {password}`; `DELETE /users/:id`. The last active admin cannot be demoted, disabled or deleted (409).
- **extra:** `GET /auth/sessions` → `{sessions:[{id, createdAt, lastSeenAt, expiresAt, ip, userAgent, current}]}` (your own
  sessions); `DELETE /auth/sessions/:id` revokes one of them.

## System (= original, plus extra)

| Route                                                        | Response                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| ------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GET /system/mode`                                           | `{readOnly, readOnlyReason, managedScope[], selfContainerId, selfProject, stackPaths[]}`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| `GET /system/health`                                         | `{status, docker, apiVersion, daemonApiVersion, engineVersion, composeVersion, eventStream, uptimeSeconds, streams}`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `GET /system/info`                                           | Redacted `docker info` (Docker's own PascalCase keys).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| `GET /system/host`                                           | `{cpuPercent, load[3], memTotal, memAvailable, uptimeSeconds, dockerRoot:{path,total,free}, dockerRootError}`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `GET /system/df[?refresh=1]`                                 | `{usage, refreshing}`: `usage` is the per-type `system df` summary (null until the first measurement finishes; `refreshing` is then true), cached for `QM_DF_MIN_INTERVAL_SECONDS`, single-flight; `refresh=1` forces a measurement.                                                                                                                                                                                                                                                                                                                                                                             |
| **extra** `GET /system/settings`                             | Read-only operational settings for the UI: `{version, retention{...}, session{...}, limits{...}, security{cookieSecure, publicOrigins, redactAllEnv, protectedNames}}`.                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| **extra** `GET /stats/overview[?minutes&since&until&bucket]` | `{points:[{t, cpu, mem, netRx, netTx}]}` host-wide totals over all containers. `minutes` 5-43200 (default 60); `since`/`until` epoch-minutes window (max 43200 minutes wide, overrides `minutes`; `until` defaults to now); `bucket` 1-1440 minutes. Windows up to 24 h return one point per minute; longer windows are bucketed automatically to at most ~1000 points (`t` = bucket start; `cpu`/`mem`/`netRx`/`netTx` are the average of the per-minute totals; network rates are derived per minute before bucketing). Streamed and aggregated server-side, so the whole retention window is never truncated. |

## Containers (= original)

- `GET /containers` → `ContainerSummary[]`: id, name, image, state, health, ports, labels, `composeProject`, `composeService`,
  `capability` (`full|limited|standalone`), `mutable`, `protectedReason`, `restartCount`, `startedAt`.
- `GET /containers/:ref` → redacted inspect. `:ref` is a name, short or full id.
- `GET /containers/:ref/logs?tail&since&timestamps` (logs) → tail 1-5000.
- `GET /containers/:ref/stats` → `{points}` (ring buffer). `GET /containers/:ref/stats/history` → `{rollups}`. Params (all optional): `minutes` 1-43200 (default 60); `since`/`until` epoch-minutes window (max 43200 minutes wide, overrides `minutes`; `until` defaults to now); `bucket` 1-1440 minutes. Windows up to 24 h return raw one-minute roll-ups; longer windows are downsampled automatically to at most ~1000 points (`minute` = bucket start, CPU/memory averages weighted by samples, maxima kept, network/block counters are cumulative so the last value is kept). Data exists only as far back as `QM_STATS_RETENTION_HOURS`.
- `POST /containers/:ref/actions/:action` (admin), `:action` ∈ `start|stop|restart|kill|pause|unpause|remove|recreate`.
  Body (strict): `{confirm?, applyDrift?, confirmNamespaceProvider?, signal?, timeoutSeconds?}`. `remove` and `recreate`
  require `confirm` = container name; `remove` never forces and never removes volumes. `recreate` works for Compose-managed
  containers only (via Compose, hash-gated) and answers **200** `{ok:true, operationId}`; for a standalone container it is
  409 `unsupported`. `:ref` is resolved to the full ID, the scope/read-only/self checks run on the inspected object, and the
  action is sent by ID.

## Compose (= original, without `pull`)

- `GET /compose/projects` → projects with `level` (`full|limited`), `reasons`, `reasonDetails[{code,message,hint}]`, services.
- `GET /compose/projects/:name` (services, dependency graph, namespace providers) · `GET …/graph` · `GET …/config` (redacted
  resolved configuration; full level only) · `GET …/validate` (`config --quiet`; unset variables are errors; file references
  checked against the allowed roots).
- `POST /compose/projects/:name/actions/:action` (admin), `:action` ∈ `up|stop|start|restart|down|redeploy`, body
  `{service?, forceRecreate?, confirm?, applyDrift?, confirmNamespaceProvider?}` → **202** `{operationId}`. `down` and
  `redeploy` with `forceRecreate` need `confirm` = project name; `down` never uses `-v`. Before `up`/`redeploy`,
  `compose config --hash` is compared with the running containers' `com.docker.compose.config-hash`; a mismatch is 409
  `blocked_drift` (with the redacted config of the drifted services) unless `applyDrift:true`. Recreating a service that
  others share a namespace or volumes with needs `confirmNamespaceProvider:true`. Limited projects support only
  `start|stop|restart`. While a maintenance run is active on the project, actions are refused (409).
- Project names are lower-case letters, digits, `_` and `-`, starting with a letter or digit (Compose's own rule); others are 400 `validation`.

## Images, volumes, networks (= original)

`GET /images`, `GET /images/:ref` (refs containing `/` must be percent-encoded: `reg.io%2Fteam%2Fapp:tag`), `GET /volumes`,
`GET /volumes/:name`, `GET /networks`, `GET /networks/:ref`. Lists are bare JSON arrays.

## Events, audit, operations (= original)

- `GET /events?limit&beforeId&container&project` → `{events}` (allow-listed actions only; limit 1-500, default 100).
- Admin: `GET /audit?limit&beforeId&action&actor` → `{entries:[{id,time,actor,ip,action,target,params,outcome,durationMs}]}`.
  There is no write API.
- `GET /operations` → `{operations}` (newest 50). `GET /operations/:id` → one operation (status, kind, target, output).

## Stop monitoring (= original)

Admin: `GET /ignored` → `{entries}`; `POST /ignored {kind: "project"|"container", name, confirm}` (`confirm` = `name`) → 201 entry;
`DELETE /ignored/:id` → the removed entry. Never touches Docker, Compose or files; allowed in read-only mode. Anything about
an ignored project answers 409 `ignored`.

## Updates, images and storage (= original)

All mutations are admin only.

- `GET /updates` → `{images:[{ref, state, reason, localDigest, remoteDigest, checkedAt, source, quarantined, usedBy[]}]}`; states
  `up_to_date|update_available|update_staged|local_only|pinned_digest|unknown`.
- `POST /updates/check` → fresh registry lookups (allowed in read-only mode). `GET /updates/quarantine` → `{entries}`;
  `POST /updates/quarantine/clear {target, digest?}`.
- `POST /images/pull {ref}` → **202** `{operationId}`: pull by digest into a staged tag; never restarts or retags. Out of scope: 403 `out_of_scope`.
- `GET /images/staged` → `{staged, rollbackPoints}`.
- `POST /images/cleanup/preview {categories?, minAgeHours?, labelFilter?}` → preview with `previewId`;
  `POST /images/cleanup/execute {previewId, confirm: previewId}`. Previews live in memory for 10 minutes, belong to the admin
  who made them and are lost on restart; all candidates are re-verified at execution.
- `POST /images/:ref/remove {confirm, tag?}` → `{kind, name, action: "deleted"|"untagged", removedTags, remainingTags, reclaim}`
  (`confirm` = that tag, or the first tag / short id).
- `POST /volumes/:name/remove {confirm: <volume name>, acknowledgeComposeProject?}` → 409 `conflict` with
  `details.requires: "acknowledgeComposeProject"` for Compose-owned volumes. `POST /networks/:ref/remove {confirm: <network name>}`.
- `POST /storage/cleanup/preview {categories: ["unused_networks"|"anonymous_volumes"|"named_volumes"], includeComposeProjectVolumes?, minAgeHours?}`
  → preview with sizes, `confirmPhrase` (non-null when volumes are included, `delete N volume(s)`) and `previewId`;
  `POST /storage/cleanup/execute {previewId, confirm: confirmPhrase ?? previewId}` → `{removed, skipped, reclaim}`.
  Previews are allowed in read-only mode (they change nothing).
- `reclaim` = `{estimatedBytes, freeSpaceDeltaBytes, dfDeltaBytes, reclaimedBytes, basis: "df"|"statfs"|"estimate", note, caveat}`.
- `GET /registries/credentials` (admin, the read too) → `{credentials:[{host, username, updatedAt}]}`;
  `PUT /registries/credentials {host, username, password}`; `DELETE /registries/credentials/:host` → `{ok:true}`. Passwords are
  encrypted at rest and never returned. For Docker Hub use the host `registry-1.docker.io`.

## Maintenance (= original, plus `cron/preview`)

Reads: any signed-in user. Writes: admin.

- **Policies.** `GET /maintenance/policies` → `{policies:[{scopeKind, scopeKey, policy, updatedAt}]}`;
  `GET /maintenance/policies/effective?project&service&container` → `EffectivePolicy` (with `sources`);
  `PUT /maintenance/policies/:kind/:key` (`kind` ∈ `global|project|service|container`; `:key` is URL-decoded, a service key is
  `project/service` → `shop%2Fweb`; the global policy is keyed `*`) → `{scopeKind, scopeKey, policy}`;
  `DELETE …` → `{ok:true}`. A policy with `health.probe` is only saved if the probe returns the expected status now (5 s, no
  redirects), otherwise 400.
- **Windows.** `GET /maintenance/windows` → `{windows:[{id,name,days,start,end,timezone,enabled,openNow,nextStart,...}]}`;
  `POST` (201) `{name, days[0-6], start "HH:MM", end, timezone, enabled?}`; `PUT /:id`; `DELETE /:id` (409 while a schedule uses it).
- **Schedules.** `GET /maintenance/schedules` → `{schedules:[{..., nextRuns[3]}]}`; `POST` (201)
  `{name, cron, timezone, jobType: "image_check"|"image_pull"|"update"|"cleanup_report", selector?: {project?|container?, services?}, mode: "manual"|"auto", windowId?, enabled?}`;
  `PUT /:id`; `DELETE /:id`. Window and schedule names are unique (409 `conflict`).
- **extra** `GET /maintenance/cron/preview?cron&timezone` → next run times for a cron expression in a time zone (both required).
- **Plan (dry run).** `POST /maintenance/plan {project, services?, applyDrift?, allowUnhealthy?}` → `RunDetail`: run fields
  flattened + `plan` + `steps`, stored as a `planned` run. Changes nothing. A plan that cannot proceed still answers 200 with
  `plan.blocked = {code, message}` (codes: `refused`, `limited_capability`, `validation`, `cycle`, `out_of_scope`,
  `self_protected`, `read_only`, `preflight`, `blocked_drift`, `nothing_to_do`). Unknown project: 404.
- **Runs.** `POST /maintenance/runs {project, services?, applyDrift?, allowUnhealthy?, startNow?, confirm?, windowId?, ignoreWindow?}`
  → **201** (awaiting approval) or **202** (started); `startNow` needs `confirm` = project. `GET /maintenance/runs[?status&target&limit]`
  → `{runs}` (limit 1-200, default 50); `GET /maintenance/runs/:id` → plan + persisted steps.
  `POST /maintenance/runs/:id/approve {planHash}` → **202** (409 `approval_stale` if the plan changed; a fresh request is then
  created); `POST …/reject`; `POST …/restore {confirm, services?}` → **202** `{operationId}` (`confirm` = the run's target).
  Create, approve, reject and restore keep running if the client disconnects.
- **Standalone.** `POST /maintenance/standalone {container, confirm}` → **202**, body = the run (flattened `RunDetail`) **plus
  `runId`**. 409 for `confirmation_required`, `read_only`, `unsupported` (Compose-managed, `container:` network mode, `--rm`),
  `conflict` (nothing to update, policy `ignore`, another run active), `out_of_scope`. There is no restore endpoint for
  standalone runs (the old container is kept; see OPERATIONS).
- **Monitors.** `GET /monitors/settings` → `{enabled, diskFreePercentBelow, diskFreeBytesBelow, cpuPercentAbove, memoryPercentAbove, sustainSeconds, restartLoopCount, restartLoopWindowSeconds, upcomingMaintenanceMinutes}`;
  `PUT /monitors/settings` (admin) with the same shape.

Run status values: `planned`, `awaiting_approval`, `approved`, `running`, `completed`, `failed`, `partial`, `rolled_back`,
`skipped`, `interrupted`, `approval_stale`, `blocked`, `rejected`. Step names: `preflight`, `validate`, `check`,
`recovery_point`, `pull`, `impact`, `deploy`, `health`, `dependents`, `rollback`, `record`.

## Notifications (= original)

Reads: any signed-in user (secrets masked). Writes and tests: admin. Lists are bare arrays.

- `GET /notifications/types` → `ChannelTypeInfo[]` (`webhook|ntfy|gotify|discord|slack|telegram|smtp`, each with field specs).
- `GET /notifications/channels` · `GET /notifications/channels/:id` · `POST /notifications/channels` (201)
  `{type, name, enabled?, minSeverity?, events?, config}` · `PATCH /notifications/channels/:id {name?, enabled?, minSeverity?, events?, config?}` ·
  `DELETE …/:id` · `POST …/:id/test` (sends a `test` event).
- `GET /notifications/deliveries?limit&channelId&beforeId` → `DeliveryRecord[]` (limit 1-500, default 50).
- Secret fields in `config` are returned masked (`••••••`; URLs masked to scheme and host); a `PATCH` that sends the mask back keeps the stored value.

## Streams (Server-Sent Events) (= original, plus `/stream/maintenance/runs/:id`)

`Content-Type: text/event-stream`. Each stream is bound to the session: closed on logout, expiry, password or role change,
re-validated every 60 s, limited per session (`QM_SSE_MAX_PER_SESSION`) and in total (`QM_SSE_MAX_TOTAL`; 429 `rate_limited`
when exceeded), heartbeat comment `: ping` every 15 s, closed when the client cannot keep up. The final event is
`end {reason}`. Frames look like `event: <name>\ndata: <json>\n\n`.

| Route                                            | Events                                                                                                                                                                                     |
| ------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `GET /stream/events[?sinceId]`                   | `docker-event` (a stored event record). With `sinceId`, up to 200 missed events are replayed first.                                                                                        |
| `GET /stream/stats`                              | `snapshot` (latest samples once), then `stats` per sampling interval.                                                                                                                      |
| `GET /stream/logs/:ref[?tail&timestamps]` (logs) | `log` per line; `error {message}`; `end` when the container's log stream ends.                                                                                                             |
| `GET /stream/operations/:id`                     | `output {text}`, `status {status, error}`; `end {reason: "operation finished"}` after a final status.                                                                                      |
| `GET /stream/maintenance`                        | `snapshot` (one per recent run, oldest first), then `run` whenever a run changes (**polled every 2 s**, not pushed).                                                                       |
| `GET /stream/maintenance/runs/:id`               | Live output of a run: `output`, `status`, `end`. A run id is also its operation id. 404 for an unknown run; a finished run whose operation record is gone answers with its final `status`. |

The web UI does not use `/stream/maintenance` yet.

## Differences from the original's API document

- `docs/API.md` of the original lists a compose action `pull`. The original's route has never had it, and neither does this
  one: `up|stop|start|restart|down|redeploy` only; anything else is 400 `validation`.
- Documented here but not in the original document: `/ignored`, `/auth/sessions`, `/system/settings`, `/stats/overview`,
  `/maintenance/cron/preview`, `/stream/maintenance/runs/:id`, the `runId` field of `POST /maintenance/standalone`, the
  full list of error codes with HTTP status, and the response/list shapes.
- Behaviour notes of the port (not visible to the web UI): a wrong HTTP method on a known route is a 404 envelope (as in the
  original's Fastify), not 405; only the first unknown JSON key is named in a strict-body error (the original lists all); the
  OpenAPI document `/openapi/v1.json` is served in the Development environment only.

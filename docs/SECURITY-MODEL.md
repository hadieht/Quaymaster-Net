# Security model

What Quaymaster (.NET) protects, how, and what it cannot do. Every statement below was checked against the code of this
repository; where a statement is a limit rather than a guarantee, it says so. The model is the same as the original
Quaymaster's; the implementation is new. The red-team review of this implementation is in
[reviews/redteam-2026-11.md](reviews/redteam-2026-11.md) (internal) and its dispositions are summarised under
[Review history and accepted findings](#review-history-and-accepted-findings). To report a vulnerability see
[../SECURITY.md](../SECURITY.md).

## The central fact: the Docker socket

Anyone who can talk to the Docker socket can start a privileged container that mounts the host's root filesystem. So
**whoever controls Quaymaster, or the account it runs as, controls the host.** Quaymaster reduces what _it_ will do and
records what it did. It does not and cannot sandbox the socket.

Consequences:

- Do not expose Quaymaster to the internet. It has no two-factor authentication. Use a trusted LAN or a VPN, and a TLS
  reverse proxy.
- Keep it bound to `127.0.0.1` unless a proxy on another host must reach it.
- If you only need monitoring, set `QM_READ_ONLY=true`. (A read-only socket proxy via `QM_DOCKER_SOCKET=tcp://...` is
  supported by the code, but no example ships and it was not exercised for this documentation.)
- Use `QM_MANAGED_SCOPE` to restrict mutations to named projects/containers.
- Treat the data volume and `QM_SECRET_KEY` as sensitive.

## Trust boundaries

| Boundary         | Rule                                                                                                                                                                                                                                                        |
| ---------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Browser → server | Authenticated session, CSRF protection. The browser never talks to Docker.                                                                                                                                                                                  |
| Server → Docker  | Named, validated actions only. No generic proxy to the Engine API; no route accepts a command line. Reads use a GET-only client; mutations go through one gateway (below).                                                                                  |
| Compose files    | Trusted only if their real path lies under `QM_STACK_PATHS` (or a same-path mount of Quaymaster's own container) and not under the Docker data root. Container labels are untrusted input. Quaymaster never edits Compose files.                            |
| Outbound network | Notification and registry clients: http/https only, no URL credentials, link-local, unspecified, multicast and cloud-metadata addresses refused, at most three same-host redirects, 5 s timeout, 64 KiB response cap. LAN addresses are allowed on purpose. |

## Authentication and sessions

- **No anonymous mode.** Everything under `/api/` needs a session, with four public exceptions: `GET /healthz` (returns just
  `{"status":"ok"}`), `GET /api/v1/auth/status`, `POST /api/v1/auth/setup`, `POST /api/v1/auth/login`. The guard is
  fail-closed: the path is canonicalised first, and anything that is or routes to `/api/` is authenticated by default.
  Unauthenticated requests to unknown `/api/` paths get 401, not 404.
- **First run:** no default password. Either a one-time **setup token** (random, announced on stderr outside the logging
  pipeline so redaction cannot mask it, written to `<data>/setup-token` with mode 0600, valid 60 minutes, five attempts per
  IP, constant-time compare, deleted on success, re-issued at every start and after expiry), or an admin from
  `QM_ADMIN_USER` plus a hash/password file, applied only while no users exist.
- **Passwords:** scrypt (default N=2^17, r=8, p=1, 64-byte key, 16-byte salt, NFKC-normalised, parameters stored with the
  hash, constant-time compare), 12 to 1024 characters. Hashes are byte-compatible with the original's. Hashing is
  serialised (one at a time, a queue of eight, then 429) so a flood of logins cannot allocate many 128 MiB buffers. Unknown
  usernames still pay for a dummy hash.
- **Sessions:** a random 256-bit token in the cookie; only its SHA-256 is stored. A new session at every login. Idle
  timeout 30 minutes and absolute lifetime 12 hours by default (`QM_SESSION_IDLE_MINUTES`, `QM_SESSION_ABSOLUTE_HOURS`).
  Logout-all revokes every session of the user; a password change revokes the user's other sessions; role changes and
  disabling revoke that user's sessions and live streams.
- **Cookie:** `qm_session=<token>; Path=/; HttpOnly; SameSite=Strict; Max-Age=<absolute lifetime>`. With
  `QM_COOKIE_SECURE=true` the name becomes `__Host-qm_session`, `Secure` is added and HSTS is sent.
- **CSRF:** every state-changing request (anything except `GET`, `HEAD`, `OPTIONS`) needs an `Origin` header that **exactly**
  matches a configured origin (`QM_PUBLIC_ORIGIN`, default derived from host and port); otherwise 403 `origin`. Requests that
  carry a session also need the header `X-QM-CSRF` with the session-bound token (HMAC-SHA256 of the session id hash under a
  per-session secret, constant-time compare); otherwise 403 `csrf`. The token is returned by login, setup and
  `GET /auth/me`. Login and setup have no session yet, so the exact `Origin` is the check there. GET requests have no side
  effects.
- **Brute force** (limiter state is in memory, see [limitations](#known-limitations)): a per-IP budget of 20 login attempts
  per 5 minutes, checked **before** any hashing; a hard lock keyed on (account, IP) after 5 consecutive failures, starting
  at 30 s and doubling up to 15 minutes; a progressive delay (from the third failure, capped at 30 s) on failed responses
  for the account, which slows guessing but never blocks a correct login. Limit hits answer 429 `rate_limited` with
  `Retry-After`. Failed logins are audited; the raw username of a failed login is never stored for unknown accounts (it is
  recorded as `unknown:` plus 12 hex characters of its SHA-256), because people type passwords there.
- **Roles:** `admin` can do everything; `viewer` is read-only. Container logs need the separate `logs:read` permission:
  admins always, viewers only when granted (default off). Some reads are admin-only: `/users`, `/audit`, `/ignored`,
  `/registries/credentials`. Viewers can read notification-channel metadata and maintenance policies (secrets inside are
  masked). The last active admin cannot be demoted, disabled or deleted; the check and the write happen under one lock, and
  user updates write only the changed columns, so concurrent edits cannot undo each other.
- Usernames: 1-64 characters, letters, digits, `. _ @ -`.

## Authorization layers for every mutation

All Docker and Compose mutations pass through one service, `MutationGateway`; an architecture test fails the build if any
other class uses the mutating interfaces. In order:

1. **Role.** Admin only (403 `forbidden`).
2. **Read-only mode** (`QM_READ_ONLY`). Refused with 409 `read_only` inside the gateway, which also applies an allow-list of
   Compose subcommands (read: `config ls ps images version`; write: `up stop start restart down`). Read-only mode is
   enforced at this choke point, **not** in the HTTP transport to Docker: the code never calls the Engine API mutatively
   anywhere else, but a bug inside the gateway could. (Red-team F-11, accepted.)
3. **Managed scope** (`QM_MANAGED_SCOPE`). The target's name **and** its Compose project must match; for projects, so must the
   volumes, networks and containers the project references. Otherwise 409 `out_of_scope`. A maintenance plan whose image
   tag is shared with an out-of-scope or protected container is refused.
4. **Self-protection.** Refuses actions on Quaymaster's own container, Compose project (per-service actions on its other
   services are allowed), volumes and networks, and on anything in `QM_PROTECTED` (409 `self_protected`). When Quaymaster runs
   in a container and cannot identify itself, the policy fails closed to read-only.
5. **Resolve, check, act by ID.** Names are resolved to full 64-hex IDs, the inspected object is checked, and the action is
   sent by ID, so a rename between check and act cannot redirect it.
6. **Confirmation.** Remove, recreate, down, redeploy with force, cleanup and restore require a server-validated typed
   confirmation (409 `confirmation_required`, with the expected value in `details`).
7. **Maintenance fence.** While a maintenance run is active on a project, manual actions on it are refused. The update engine
   refuses to plan, and asserts again before every `compose up`, if a service's namespace/volume dependents are protected,
   self, or out of scope (red-team F-02).
8. **Audit.** Append-only `audit_log` with actor, IP, action, target, redacted parameters, outcome and duration
   (see [Audit](#audit)).

Other safety limits:

- Container removal never uses force and never removes volumes; volumes are removed only by the separate storage cleanup with
  a typed confirmation, and only when no container references them. Compose `down` never uses `-v`.
- Compose is invoked with an explicit project name, directory and file arguments, in a fixed minimal child environment
  (`PATH`, `HOME`, `DOCKER_HOST`, `LANG`, and `DOCKER_CONFIG` if set). Child processes are started with an argument list,
  never through a shell; identifiers starting with `-` are rejected and `--` separates arguments.
- Images are pulled **by digest only**; the gateway refuses any other pull.
- Safety reads in the update engine (shared-tag fence, post-deploy checks) are fresh and ignore `QM_VISIBLE_SCOPE`, so hiding
  a container in the UI cannot blind the checks.

## Secrets

- **At rest:** notification-channel configuration and registry credentials are encrypted with AES-256-GCM. The key is derived
  with HKDF-SHA256 (salt `quaymaster`, info `secrets-v1`) from `QM_SECRET_KEY`; each value has a random IV and
  authenticated context. The key lives in the environment or a file, never in the database. The server refuses to start with
  a missing or weak key (fewer than 32 bytes, or fewer than 8 distinct byte values). The format is compatible with the
  original's.
- **On the way out of the process:** every API response, SSE frame, stored event and operation output, notification payload,
  audit parameter and log line is redacted. Keys that look like secrets (password, token, key, secret, auth, cookie, DSN,
  webhook ...) are masked as `••••••`; values that look like secrets (URL userinfo, JWTs, PEM blocks, known token prefixes,
  high-entropy strings, `Bearer`/`Basic` credentials) are masked regardless of the key; and **learned** secret values are
  scrubbed wherever they reappear, even in places that have no key name (a container's `Cmd`, a health check, a Compose
  `command`). The scrub also runs over JSON API bodies (`ResponseScrubMiddleware`, not `/auth/*` or `/users*`), the error
  envelope, and console log lines and exception text. `QM_REDACT_ALL_ENV=true` masks all environment values. There is no
  "reveal" function.
- **Never returned:** notification URLs and channel secrets (masked), registry passwords (a credential listing returns host,
  username and update time only).
- **Not covered:** redaction is heuristic. The logs of your own containers are scrubbed with the same rules, but if an
  application prints a secret in an unusual form it can appear in logs shown to users who have log access. Grant `logs:read`
  sparingly.

## HTTP hardening

Sent on every response (verified against a running instance):

| Header                         | Value                                                                                                                                                                                                |
| ------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Content-Security-Policy`      | `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'` |
| `X-Frame-Options`              | `DENY`                                                                                                                                                                                               |
| `X-Content-Type-Options`       | `nosniff`                                                                                                                                                                                            |
| `Referrer-Policy`              | `no-referrer`                                                                                                                                                                                        |
| `Cross-Origin-Resource-Policy` | `same-origin`                                                                                                                                                                                        |
| `Cross-Origin-Opener-Policy`   | `same-origin`                                                                                                                                                                                        |
| `Permissions-Policy`           | `camera=(), microphone=(), geolocation=()`                                                                                                                                                           |
| `Strict-Transport-Security`    | `max-age=15552000; includeSubDomains`, only with `QM_COOKIE_SECURE=true`                                                                                                                             |
| `Cache-Control`                | `no-store` on `/api/*` and `/healthz`; the SPA's hashed assets are `immutable`, other static files `no-cache`                                                                                        |

No CORS headers are sent. The `Server` header is off. Request bodies are limited to 64 KiB; JSON bodies are parsed strictly
(unknown properties are rejected). The machine-readable `/openapi/v1.json` exists only in the Development environment.
`X-Forwarded-For` is honoured only from the IPs/CIDRs in `QM_TRUST_PROXY` (an explicit list; `true`, `*`, `0.0.0.0/0` are
rejected at start). Unhandled errors become the standard error envelope without a stack trace.

**Live streams (SSE)** are bound to the session: closed on logout, expiry, password or role change; re-validated every
60 seconds; limited per session (default 8) and in total (default 32); heartbeated every 15 s; and closed when the client
falls more than 1 MiB behind. The final event is `end {reason}`.

## Audit

`audit_log` is append-only at the database level too: triggers abort every `UPDATE`, and abort a `DELETE` of any row younger
than 30 days. Retention (`QM_AUDIT_RETENTION_DAYS`, default 365, minimum 30) is applied by a housekeeping task. Entries record
actor, client IP, action, target, redacted parameters, outcome (`success`, `failure`, `denied`) and duration. `GET /audit`
is admin-only and there is no write API. Refusals (read-only, scope, self-protection) are recorded as `denied`.

## Update-safety properties

These are security-relevant because an update runs with root-equivalent power:

- An update changes only the image. Compose drift between files and the running container blocks automatic runs.
- Images are pulled by digest and staged; user-visible tags move only inside the deploy step.
- Approvals are bound to a hash of the exact plan; any change makes the approval stale.
- Critical targets are never automated. Standalone updates are never automated.
- Registry credentials are sent only over HTTPS (or to loopback / allow-listed insecure hosts) and only to the registry host
  or an allow-listed token service. Redirects are never followed.
- Disk checks fail closed. Runs that crash are never resumed automatically; they are marked `interrupted` with recovery
  commands.

## Deployment hardening included

Container: non-root uid 10001, read-only root filesystem, `no-new-privileges`, all capabilities dropped, memory/CPU/pid limits,
`tini`, health check, Docker group added at run time rather than baked in, port published on `127.0.0.1`. Native: dedicated
user, `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, `NoNewPrivileges`, `MemoryMax`. Details in [DEPLOY.md](DEPLOY.md).
The data directory is created with mode 0700 and the database with 0600.

## Known limitations

- **No 2FA, no multi-host, no per-resource roles.** Two roles only.
- **Login limiter and setup-token budget are in memory.** A restart clears them. Behind a proxy without `QM_TRUST_PROXY`, one
  client can exhaust the per-IP budget for everyone. Set `QM_TRUST_PROXY`.
- **Read-only mode lives in the gateway, not the Docker transport** (F-11, accepted).
- **Cleanup previews** are single-use per admin, but two concurrent executions of the same preview are not strictly
  serialised; per-item re-verification still protects in-use resources (F-12). **Rejecting an approval** is a
  read-then-write that only an admin racing their own approve can hit (F-13).
- **Leftover one-off containers** from a crashed run can make the next run stop with "Another tool changed ..." (F-14, same
  as the original; fails closed).
- **Regex anchors** in about forty validators accept a trailing newline (`$` instead of `\z`); no exploitable path was found
  because lookups fail closed (F-15). Token-pattern redaction edge cases next to non-ASCII characters (F-16).
- **Redaction is heuristic** (see above). **Compose `include`/`extends` from remote sources** make a project `limited`.
- **Webhook and health-probe URLs:** the outbound policy is applied at send time; a hostname that resolves to a forbidden
  address is refused then, not when you save it. Health-probe URLs saved in policies are verified once (5 s, no redirects)
  but are not subject to the notification outbound policy.
- **Recovery text and audit `argv`** contain absolute paths of your Compose directories.
- **Supply chain:** no vulnerable NuGet packages were reported; npm advisories are dev-only. Base images and CI actions are
  pinned by tag, not by digest (F-18).
- **Testing gaps:** SMTP and Telegram delivery were tested against local fake servers, not real services; Docker Hub and GHCR
  round trips were exercised in live tests on the development host only. The CI workflow has not run on GitHub.

## Review history and accepted findings

The red-team review (19 findings: 0 critical, 5 high, 5 medium, 7 low, 2 info) was followed by a fix round. All high and
medium findings are fixed with regression tests that fail on the old code: learned secrets in API responses (F-01), the
maintenance fence (F-02), lost-update and last-admin races (F-03, F-04), stale "fresh" container reads (F-05), Docker errors
after `up` without rollback guidance (F-06), runs stuck `running` (F-07), monitors dying on a registry error (F-08), the
visible-scope blind spot (F-09), and log/error scrubbing (F-10). The low and informational findings (F-11 to F-19) were
accepted with the reasons listed above; the full table is in [PROGRESS.md](PROGRESS.md) under T8.

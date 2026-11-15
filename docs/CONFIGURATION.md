# Configuration

Quaymaster is configured **only through `QM_*` environment variables**. The settings you manage in the UI (policies,
windows, schedules, notification channels, registry logins, monitor thresholds, users) are stored in the database, not in
the environment.

How the loader behaves (`QmConfigLoader`, parsed once at start-up):

- **Unknown `QM_*` names are errors** (typo protection), and **every** problem is listed at once. The process prints them
  and exits with code `2`. The messages name variables, never secret values.
- An **empty value counts as unset**, so `QM_PORT=` in a template falls back to the default.
- **Booleans:** `true`/`1`/`yes` and `false`/`0`/`no` (case-insensitive). Anything else is an error.
- **Lists** are comma-separated; spaces around items are trimmed.
- **Integers** are range-checked; the allowed range is in the tables below.
- `appsettings.json` (next to the binary) only holds log-level filters. Do not put `QM_*` settings there.
- [`.env.example`](../.env.example) lists every variable. Docker Compose reads it as `.env`; the systemd unit reads
  `/etc/quaymaster-net.env`. See [Where the file is read](#where-the-file-is-read) and the
  [cautions about `.env.example`](#cautions-about-envexample).

Example of what a bad configuration looks like (real output):

```text
Invalid configuration:
  - QM_BOGUS: unknown variable (typo?)
  - QM_PORT: must be an integer between 1 and 65535
  - QM_SECRET_KEY (or QM_SECRET_KEY_FILE) is required
  - QM_TRUST_PROXY must be an explicit IP/CIDR list, never "true", "*" or 0.0.0.0/0

See .env.example for every supported variable.
```

## Server and network

| Variable           | Default                  | Notes                                                                                                                                                                                                                                                                                                           |
| ------------------ | ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `QM_HOST`          | `127.0.0.1`              | Bind address (an IP, `localhost`, or a host name that resolves). The container image sets `0.0.0.0`; exposure is then decided by the port mapping.                                                                                                                                                              |
| `QM_PORT`          | `8787`                   | Listen port, 1-65535.                                                                                                                                                                                                                                                                                           |
| `QM_PUBLIC_ORIGIN` | derived from host/port   | Exact browser origin(s), comma-separated. Bare origins only (`https://qm.example.org`, no path, no trailing slash). **When set it replaces the default list**, so include `http://127.0.0.1:8787` too if you still want to use it. State-changing requests with any other `Origin` are rejected (403 `origin`). |
| `QM_TRUST_PROXY`   | empty                    | IPs or CIDRs of reverse proxies whose `X-Forwarded-For` is trusted for the client IP (rate limiting and audit use it). `true`, `*`, `0.0.0.0/0` and `::/0` are rejected; IPv4 must be written in full (`10.0.0.5`, not `10.0.0`). Empty = forwarded headers are ignored.                                        |
| `QM_COOKIE_SECURE` | `false`                  | Set `true` when the UI is served over HTTPS: the session cookie becomes `__Host-qm_session` with `Secure`, and `Strict-Transport-Security` is sent. With `true` the UI does not work over plain HTTP.                                                                                                           |
| `QM_WEB_DIR`       | `<working dir>/web/dist` | Directory with the built web UI (must contain `index.html`). If it does not exist the server runs API-only. The image and the systemd unit set it. A relative value resolves against the process's working directory.                                                                                           |

Default origins when `QM_PUBLIC_ORIGIN` is empty: `http://<QM_HOST>:<QM_PORT>`, plus `http://127.0.0.1:<port>` and
`http://localhost:<port>` when the host is a loopback or any-address value (`127.0.0.1`, `localhost`, `::1`, `0.0.0.0`,
`::`). IPv6 hosts are bracketed.

## Storage and secrets

| Variable             | Default  | Notes                                                                                                                                                                                                                                                                                                 |
| -------------------- | -------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `QM_DATA_DIR`        | `./data` | Resolved to an absolute path against the working directory (under `dotnet run` that is the project directory, so use an absolute path). Holds `quaymaster.db` (+ `-wal`/`-shm`), `quaymaster.lock`, `setup-token` and `backup-pre-<migration>.db`. The image sets `/var/lib/quaymaster-net`.          |
| `QM_SECRET_KEY`      | none     | **Required** unless `QM_SECRET_KEY_FILE` is set. At least 32 random bytes, hex or base64 (with at least 8 distinct byte values). Passphrases are rejected. Encrypts notification channels and registry logins; losing it makes those unreadable. Generate: `gen-secret` or `openssl rand -base64 32`. |
| `QM_SECRET_KEY_FILE` | none     | File that contains the key; used when `QM_SECRET_KEY` is empty. Suited to Docker/systemd secrets.                                                                                                                                                                                                     |

Changing `QM_SECRET_KEY` later is not supported: stored channel secrets and registry passwords can no longer be decrypted
and must be entered again.

## First admin

Applied only while the user table is empty. See [INSTALL.md](INSTALL.md#4-create-the-first-admin).

| Variable                 | Notes                                                                                                                |
| ------------------------ | -------------------------------------------------------------------------------------------------------------------- |
| `QM_ADMIN_USER`          | Required if either password variable is set, and the other way round.                                                |
| `QM_ADMIN_PASSWORD_HASH` | `scrypt$N$r$p$salt$hash`, from `hash-password`. **Single-quote it** in shells and `.env` files: it contains `$`.     |
| `QM_ADMIN_PASSWORD_FILE` | Path to a file with a plain password (at least 12 characters), hashed at first start. Set only one of hash and file. |

## Docker and Compose

| Variable                   | Default                | Notes                                                                                                                                                                                                                                                                                   |
| -------------------------- | ---------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `QM_DOCKER_SOCKET`         | `/var/run/docker.sock` | Unix socket path, or `tcp://host:port` (no path) for a socket proxy. Also handed to the `docker` CLI as `DOCKER_HOST`.                                                                                                                                                                  |
| `QM_DOCKER_BIN`            | `docker`               | CLI used for `docker compose`.                                                                                                                                                                                                                                                          |
| `QM_STACK_PATHS`           | none                   | **Optional.** Absolute directories where update, redeploy and the resolved configuration may run. Discovery and monitoring never need it. Files are trusted only if their real path is under an allowed root; container labels are untrusted. Relative paths are an error.              |
| `QM_STACK_AUTO_MOUNTS`     | `true`                 | Treat directories bind-mounted into Quaymaster's own container at the **identical** path (source = destination) as allowed stack roots. Never the data dir, the Docker socket, the Docker root or system directories (`/proc`, `/sys`, `/dev`, `/etc`, `/run`, `/var/run`, `/boot`).    |
| `QM_DOCKER_ROOT_MOUNT`     | none                   | A directory on the Docker root's filesystem, used only for free-space (`statfs`) checks before pulls and updates. If unset, the Docker root reported by the daemon is used. When neither can be read, pull and update preflight fails closed.                                           |
| `QM_REGISTRY_INSECURE`     | empty                  | Registry hosts (exact `host` or `host:port`) allowed over plain HTTP, e.g. a local registry. Loopback hosts are always allowed. Credentials are otherwise sent only over HTTPS. CIDR ranges are **not** supported.                                                                      |
| `QM_REGISTRY_TOKEN_REALMS` | none                   | Comma-separated `registry.host=token.host` pairs. Quaymaster sends token requests only to the registry's own host or a listed token service. Adds to the built-in list (Docker Hub, GHCR, Quay, lscr.io, GitLab, ECR Public, docker.n8n.io). Hostnames only, no port on the token host. |
| `QM_HELPER_ENABLED`        | `true`                 | **Accepted but has no effect.** The original's read-only file-helper container is not part of the .NET port.                                                                                                                                                                            |
| `QM_HELPER_IMAGE`          | none                   | Accepted, validated, and ignored (see above).                                                                                                                                                                                                                                           |
| `QM_HELPER_LABELS`         | none                   | `key=value,...`; accepted, validated, and ignored (see above).                                                                                                                                                                                                                          |

## Safety

| Variable            | Default | Notes                                                                                                                                                                                                                                                     |
| ------------------- | ------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `QM_READ_ONLY`      | `false` | No mutating Docker or Compose action is possible. Enforced in the mutation gateway (one choke point), not in individual routes. Quaymaster also forces read-only when it runs in a container and cannot identify itself.                                  |
| `QM_MANAGED_SCOPE`  | empty   | Name globs (`media-*,web`; `*` and `?`, case-insensitive) of projects/containers that may be mutated. Empty means everything. Anything else is refused with `out_of_scope`. Includes the volumes, networks and containers an in-scope project references. |
| `QM_VISIBLE_SCOPE`  | empty   | Read-side filter: resources whose names do not match are hidden from lists and lookups. Independent of `QM_MANAGED_SCOPE`; meant for demos and screenshots. The safety checks inside the update engine ignore it on purpose.                              |
| `QM_PROTECTED`      | empty   | Name globs that are never mutated (reverse proxy, socket proxy).                                                                                                                                                                                          |
| `QM_SELF_CONTAINER` | auto    | Name or ID of Quaymaster's container. Auto-detected from the environment (`/proc/self/mountinfo`, cgroup, hostname). Quaymaster protects its own container, Compose project, networks and volumes.                                                        |
| `QM_REDACT_ALL_ENV` | `false` | Mask every environment value in API output (not just those that look secret).                                                                                                                                                                             |

## Sessions and streams

| Variable                      | Default  | Range          | Notes                                                                                                      |
| ----------------------------- | -------- | -------------- | ---------------------------------------------------------------------------------------------------------- |
| `QM_SESSION_IDLE_MINUTES`     | `30`     | 1-1440         | Idle session timeout.                                                                                      |
| `QM_SESSION_ABSOLUTE_HOURS`   | `12`     | 1-720          | Absolute session lifetime (also the cookie Max-Age).                                                       |
| `QM_SSE_MAX_PER_SESSION`      | `8`      | 1-100          | Live streams per session.                                                                                  |
| `QM_SSE_MAX_TOTAL`            | `32`     | 1-1000         | Live streams in total.                                                                                     |
| `QM_LOG_FOLLOW_BYTES_PER_SEC` | `524288` | 1024-104857600 | Rate cap for log follow streams.                                                                           |
| `QM_SCRYPT_LOG2_N`            | `17`     | 10-20          | scrypt cost (log2 N). Values below 14 are accepted only in the `Testing` environment, never in production. |

## Monitoring and retention

| Variable                     | Default              | Range     | Notes                                                                                                                                                                       |
| ---------------------------- | -------------------- | --------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `QM_STATS_INTERVAL_SECONDS`  | `10`                 | 2-3600    | Stats sampling interval.                                                                                                                                                    |
| `QM_STATS_RETENTION_HOURS`   | `48`                 | 1-720     | Stats roll-up retention.                                                                                                                                                    |
| `QM_EVENT_RETENTION_DAYS`    | `14`                 | 1-365     | Docker event history.                                                                                                                                                       |
| `QM_AUDIT_RETENTION_DAYS`    | `365`                | 30-3650   | The audit table refuses (by trigger) to delete rows younger than 30 days.                                                                                                   |
| `QM_DISK_FLOOR_BYTES`        | `5368709120` (5 GiB) | 0 and up  | Free space on the Docker root that must remain after pulls. The plan also requires 2.5x the compressed layer size plus the larger of this floor and 10 % of the filesystem. |
| `QM_DF_MIN_INTERVAL_SECONDS` | `21600`              | 60-604800 | `docker system df` is expensive; refreshed at most this often unless an admin forces it.                                                                                    |

Alert thresholds (disk, CPU, memory, restart loops, upcoming maintenance) are **not** environment variables: they are
monitor settings stored in the database (`GET/PUT /api/v1/monitors/settings`, UI: Settings). Defaults: disk free below
10 % or 5 GiB, CPU and memory above 90 % sustained for 300 s, 5 restarts within 300 s, maintenance notice 60 minutes ahead.

## Updates and cleanup

| Variable                     | Default | Range   | Notes                                                                                     |
| ---------------------------- | ------- | ------- | ----------------------------------------------------------------------------------------- |
| `QM_CHECK_TTL_MINUTES`       | `360`   | 1-10080 | How long a registry answer is cached.                                                     |
| `QM_ROLLBACK_RETENTION_DAYS` | `7`     | 0-365   | How long `quaymaster-rollback/*` images and kept old standalone containers are protected. |
| `QM_CLEANUP_MIN_AGE_HOURS`   | `24`    | 0-8760  | Cleanup never touches images or containers younger than this.                             |
| `QM_APPROVAL_EXPIRY_HOURS`   | `72`    | 1-720   | Pending approvals expire into `skipped` after this.                                       |

## Process

| Variable                | Default | Notes                                                                                                                                           |
| ----------------------- | ------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| `QM_LOG_LEVEL`          | `info`  | `fatal`, `error`, `warn`, `info`, `debug`, `trace`, `silent`. Logs go to stdout in the console format; learned secrets are scrubbed from them.  |
| `QM_DISABLE_BACKGROUND` | `false` | Skips the event watcher, stats sampler, monitors, scheduler, notification worker and housekeeping. Diagnostics only; live data and alerts stop. |

## Compose deployment variables

Read by `docker-compose.yml` itself, **not** by Quaymaster (a `QM_`-prefixed name would be rejected as unknown):

| Variable       | Default              | Purpose                                                           |
| -------------- | -------------------- | ----------------------------------------------------------------- |
| `DOCKER_GID`   | none, **required**   | Group ID of the Docker socket: `stat -c %g /var/run/docker.sock`. |
| `DEPLOY_BIND`  | `127.0.0.1`          | Host address the port is published on.                            |
| `DEPLOY_PORT`  | `8787`               | Host port.                                                        |
| `DEPLOY_IMAGE` | `quaymaster-net:dev` | Image name used for the build and for running.                    |

`DOCKER_GID` has no default on purpose: a default of `0` would put the container in the root group.

If you change `DEPLOY_PORT` (or publish on another address), also set `QM_PUBLIC_ORIGIN` to the URL you type in the
browser. The default origin list is built from `QM_HOST`/`QM_PORT` _inside_ the container (`0.0.0.0:8787`, plus
`127.0.0.1:8787` and `localhost:8787`), so a different host port would be rejected with 403 `origin`.

## Where the file is read

| Mode           | File                            | How                                                                                                                       |
| -------------- | ------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| Docker Compose | `.env` next to the compose file | `env_file` (optional) **and** `${...}` substitution. Values in the compose file's `environment:` block win over the file. |
| `docker run`   | any file                        | `--env-file path`. Values after `-e` win.                                                                                 |
| systemd        | `/etc/quaymaster-net.env`       | `EnvironmentFile=`. The unit's own `Environment=` lines (data dir, web dir, `HOME`) come after it.                        |
| `dotnet run`   | the shell environment           | Export the variables or prefix the command.                                                                               |

In the Compose file, `QM_HOST`, `QM_PORT`, `QM_DATA_DIR`, `QM_DOCKER_SOCKET` and `QM_SELF_CONTAINER` are fixed to the
container's layout and override the same names in `.env`.

## Cautions about `.env.example`

`cp .env.example .env` produces a file that starts, but several lines are **active example values**, not neutral defaults:

- `QM_STACK_PATHS=/srv/stacks`: change it to your stack directory (or remove the line). The code default is _none_.
- `QM_REGISTRY_INSECURE=127.0.0.1:5055`: a development registry; remove it unless you run one there.
- `QM_DOCKER_ROOT_MOUNT=/`: free space is read from the container's `/`. On an overlay2 host this is the same filesystem as
  the Docker root (checked: identical sizes), but it is not guaranteed on other storage drivers. Remove the line to use the
  Docker root the daemon reports, or point it at a directory you mounted read-only from the Docker root's filesystem.
- `QM_WEB_DIR=web/dist`: fine in the image (working directory `/app`), wrong under `dotnet run` from the repo root
  (the working directory is the project directory). Remove the line there.
- `QM_HELPER_*`: no effect (see above).
- Some comments in the file are loose (for instance `QM_PUBLIC_ORIGIN` is described as "additional", `QM_STACK_AUTO_MOUNTS`
  as auto-discovery, `QM_REGISTRY_INSECURE` as accepting CIDRs). This page describes what the code does.

## Settings stored in the database

Policies, maintenance windows, schedules, notification channels, registry logins, monitor thresholds, ignored targets and
users are managed in the UI (or API) and stored in SQLite. They are included in a database backup; secrets inside them
are encrypted with `QM_SECRET_KEY`, which is **not** in the backup.

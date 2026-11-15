# Deploying Quaymaster (.NET)

Two supported modes: the container image (recommended) and a native systemd service. Both run as a non-root user that joins
the host's `docker` group. The Docker socket is root-equivalent on the host, so publish the port on `127.0.0.1` and put a
TLS reverse proxy in front for remote access (`QM_PUBLIC_ORIGIN`, `QM_COOKIE_SECURE=true`, `QM_TRUST_PROXY=<proxy IP/CIDR>`).
Step-by-step instructions are in [INSTALL.md](INSTALL.md); this page describes what the artifacts contain.

## 1. Container

```bash
docker build -t quaymaster-net:dev .

# one-time secrets (the CLI is part of the image; no Docker socket needed)
docker run --rm quaymaster-net:dev dotnet /app/Quaymaster.Api.dll gen-secret                       # QM_SECRET_KEY=...
docker run --rm quaymaster-net:dev dotnet /app/Quaymaster.Api.dll hash-password 'a long password'  # QM_ADMIN_PASSWORD_HASH='scrypt$...'
#   hash-password also reads the password from stdin or a hidden prompt (minimum 12 characters)

cp .env.example .env && chmod 600 .env       # set QM_SECRET_KEY, QM_STACK_PATHS, ...
DOCKER_GID=$(stat -c %g /var/run/docker.sock) docker compose up -d --build
```

Image layout (from the `Dockerfile`):

| What           | Where                                                                                           |
| -------------- | ----------------------------------------------------------------------------------------------- |
| Published API  | `/app` (`WORKDIR`; `dotnet /app/Quaymaster.Api.dll`, PID 1 is `tini`)                           |
| Built SPA      | `/app/web/dist` (`QM_WEB_DIR`)                                                                  |
| Data (SQLite)  | `/var/lib/quaymaster-net` (`QM_DATA_DIR`, `VOLUME`, owned by uid 10001)                         |
| User           | uid/gid 10001 (`quaymaster`), no home; `HOME=/tmp`, `DOCKER_CONFIG=/tmp/.docker`                |
| Listen         | `QM_HOST=0.0.0.0`, `QM_PORT=8787` (image defaults; exposure is the port mapping)                |
| Docker clients | `docker` CLI + `docker compose` plugin (Compose maintenance actions shell out to it)            |
| Health         | `HEALTHCHECK` runs `curl` against `/healthz` (30 s interval, 20 s start period)                 |
| Base images    | `node:22-bookworm-slim` (UI build), `dotnet/sdk:10.0` (publish), `dotnet/aspnet:10.0` (runtime) |

- **Socket without root.** The image never bakes in a GID. `group_add: [$DOCKER_GID]` (compose) or
  `--group-add $(stat -c %g /var/run/docker.sock)` (plain `docker run`) grants the socket group at run time.
- **Stack paths at identical host paths.** Compose resolves relative bind mounts on the host, so every directory in
  `QM_STACK_PATHS` must be mounted at the same absolute path inside the container (add it to `docker-compose.yml`).
  Without a stack mount, discovery, monitoring and start/stop/restart still work; updates and redeploys do not.
- **Free-space checks.** Mount `/var/lib/docker` read-only and set `QM_DOCKER_ROOT_MOUNT`, or use `QM_DOCKER_ROOT_MOUNT=/`
  (see [CONFIGURATION.md](CONFIGURATION.md#cautions-about-envexample)). If the free space cannot be read, pulls fail closed.
- **Hardening in `docker-compose.yml`.** `read_only` root filesystem (the data volume and a `/tmp` tmpfs are writable),
  `cap_drop: ALL`, `no-new-privileges`, `mem_limit: 512m`, `cpus: 1.0`, `pids_limit: 256`, port bound to `127.0.0.1`
  (`DEPLOY_BIND`, `DEPLOY_PORT`), and the label `quaymaster.role=self` (informational).
- **Environment precedence.** The compose file fixes `QM_HOST`, `QM_PORT`, `QM_DATA_DIR`, `QM_DOCKER_SOCKET` and
  `QM_SELF_CONTAINER`; the `.env` file (read via `env_file`, optional) supplies the rest.
- **Self-protection.** Quaymaster refuses to stop, restart, update or remove itself (`QM_SELF_CONTAINER`, auto-detected from
  `/proc/self/mountinfo`) or anything listed in `QM_PROTECTED`. If `QM_VISIBLE_SCOPE` hides the Quaymaster container,
  self-detection fails closed and mutations are refused: keep the container inside the visible scope.
- **Stopping.** If a maintenance run may be active, give the container a stop grace period at least as long as your longest
  health timeout. Docker's default 10 s `SIGKILL` can cut a run short; the run is then marked `interrupted` at the next
  start (see [OPERATIONS.md](OPERATIONS.md#recovery-after-a-failed-or-interrupted-update)).
- **Fixture / smoke runs.** The development fixture registry is `127.0.0.1:5055` on the host; to exercise the update flow
  from a container use `--network host` or point `QM_REGISTRY_INSECURE` at a reachable registry.

## 2. Native mode (systemd)

```bash
dotnet publish src/Quaymaster.Api -c Release -o publish
npm ci && npm run build
sudo useradd --system --home /var/lib/quaymaster-net --shell /usr/sbin/nologin -G docker quaymaster
sudo install -d -o quaymaster -g quaymaster /var/lib/quaymaster-net
sudo install -d /opt/quaymaster-net/web /opt/stacks
sudo cp -a publish/. /opt/quaymaster-net/ && sudo cp -a web/dist /opt/quaymaster-net/web/dist   # same layout as the image
sudo install -m 0640 -o root -g quaymaster .env /etc/quaymaster-net.env
sudo install -m 0644 deploy/quaymaster-net.service /etc/systemd/system/quaymaster-net.service
sudo systemctl daemon-reload && sudo systemctl enable --now quaymaster-net
```

What `deploy/quaymaster-net.service` sets (it passes `systemd-analyze verify`):

| Setting                                                                                                                                                        | Why                                                                                                                                    |
| -------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| `User=quaymaster`, `SupplementaryGroups=docker`                                                                                                                | Non-root, with the Docker socket group.                                                                                                |
| `After=`/`Requires=docker.service`                                                                                                                             | Starts after Docker and stops with it.                                                                                                 |
| `WorkingDirectory=/opt/quaymaster-net`                                                                                                                         | Must be the publish directory: ASP.NET reads `appsettings.json` (log filters) from it.                                                 |
| `EnvironmentFile=/etc/quaymaster-net.env`                                                                                                                      | Your `QM_*` settings.                                                                                                                  |
| `Environment=QM_DATA_DIR=... QM_WEB_DIR=...`                                                                                                                   | Fixed layout: `/var/lib/quaymaster-net` and `/opt/quaymaster-net/web/dist`.                                                            |
| `Environment=HOME=/var/lib/quaymaster-net ...`                                                                                                                 | `ProtectHome=true` hides the real home and the Compose CLI reads `$HOME/.docker`.                                                      |
| `ExecStart=/usr/bin/dotnet .../Quaymaster.Api.dll`                                                                                                             | Needs the ASP.NET Core 10 runtime.                                                                                                     |
| `Restart=on-failure`, `RestartSec=5`                                                                                                                           | Restarts after a crash or a configuration error.                                                                                       |
| `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, `ProtectKernelTunables`, `ProtectControlGroups`, `RestrictSUIDSGID`, `LockPersonality` | Sandbox.                                                                                                                               |
| `ReadWritePaths=/var/lib/quaymaster-net /opt/stacks`                                                                                                           | The data dir and every stack directory. Each path must exist or the unit fails with a namespace error. Add your own stack directories. |
| `MemoryMax=512M`                                                                                                                                               | Memory cap.                                                                                                                            |

The `docker` and `docker compose` CLIs must be on `PATH` for Compose actions.

## 3. Reverse proxy, TLS and socket proxy

No proxy configuration files ship with this repository. The requirements for a proxy (no response buffering, forward
`X-Forwarded-For`, keep `Origin`) and the environment variables to set are in
[INSTALL.md](INSTALL.md#5-reverse-proxy-and-tls-recommended). A read-only socket proxy can be addressed with
`QM_DOCKER_SOCKET=tcp://host:port`; that arrangement has no example and is not covered by the verification of this
documentation.

## 4. Differences from the Node original's deployment

- The data dir in the image is `/var/lib/quaymaster-net` (not `/data`); the compose volume is `qmdata`; the container and
  the service are called `quaymaster-net`. The `QM_*` surface is otherwise the same (46 variables; see
  [CONFIGURATION.md](CONFIGURATION.md)).
- `dotnet /app/Quaymaster.Api.dll gen-secret | hash-password` replace `node server/dist/cli.js ...`. The `backup`,
  `restore` and `check` subcommands of the original CLI are **not implemented**; back up with `sqlite3` or by copying
  the database with the service stopped ([OPERATIONS.md](OPERATIONS.md#backup)).
- The original's read-only file-helper container is not ported, so `QM_HELPER_*` have no effect, and projects Quaymaster
  cannot read are shown without a configuration view.
- No published image, socket-proxy compose file or reverse-proxy examples (yet).

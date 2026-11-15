# Deploying Quaymaster (.NET)

Two supported modes: the container image (recommended) and a native systemd service. Both run as a non-root user that joins
the host's `docker` group; the Docker socket is root-equivalent on the host, so publish the port on `127.0.0.1` and put a TLS
reverse proxy in front for remote access (`QM_PUBLIC_ORIGIN`, `QM_COOKIE_SECURE=true`, `QM_TRUST_PROXY=<proxy IP/CIDR>`).

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

Image layout:

| What           | Where                                                                                |
| -------------- | ------------------------------------------------------------------------------------ |
| Published API  | `/app` (`WORKDIR`; `dotnet /app/Quaymaster.Api.dll`, PID 1 is `tini`)                |
| Built SPA      | `/app/web/dist` (`QM_WEB_DIR`)                                                       |
| Data (SQLite)  | `/var/lib/quaymaster-net` (`QM_DATA_DIR`, `VOLUME`, owned by uid 10001)              |
| User           | uid/gid 10001 (`quaymaster`), no home; `HOME=/tmp`, `DOCKER_CONFIG=/tmp/.docker`     |
| Listen         | `QM_HOST=0.0.0.0`, `QM_PORT=8787` (image defaults; exposure is the port mapping)     |
| Docker clients | `docker` CLI + `docker compose` plugin (Compose maintenance actions shell out to it) |
| Health         | `HEALTHCHECK` curls `/healthz`                                                       |

- **Socket without root.** The image never bakes in a GID. `group_add: [$DOCKER_GID]` (compose) or
  `--group-add $(stat -c %g /var/run/docker.sock)` (plain `docker run`) grants the socket group at run time.
- **Stack paths at identical host paths.** Compose resolves relative bind mounts on the host, so every directory in
  `QM_STACK_PATHS` must be mounted at the same absolute path inside the container (add it to `docker-compose.yml`).
  Without a stack mount, discovery, monitoring and start/stop/restart still work; updates and redeploys do not.
- **Free-space checks.** Mount `/var/lib/docker` read-only and set `QM_DOCKER_ROOT_MOUNT`; otherwise pulls fail closed.
- **Hardening in `docker-compose.yml`.** `read_only` root filesystem (data volume and a `/tmp` tmpfs are writable), `cap_drop: ALL`,
  `no-new-privileges`, memory/CPU/pids limits, port bound to `127.0.0.1` (`DEPLOY_BIND`, `DEPLOY_PORT`).
- **Self-protection.** Quaymaster refuses to stop/restart/update/remove itself (`QM_SELF_CONTAINER`, auto-detected from
  `/proc/self/mountinfo`) or anything listed in `QM_PROTECTED`. If `QM_VISIBLE_SCOPE` hides the Quaymaster container,
  self-detection fails closed and mutations are refused: keep the container inside the visible scope.
- **Fixture / smoke runs.** The fixture registry is `127.0.0.1:5055` on the host; to exercise the update flow from a container
  use `--network host` or point `QM_REGISTRY_INSECURE` at a reachable registry.

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

`deploy/quaymaster-net.service` mirrors the original unit (same `User`, `SupplementaryGroups=docker`, `NoNewPrivileges`,
`ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, `ProtectKernel*`, `RestrictSUIDSGID`, `LockPersonality`, `MemoryMax=512M`)
with the .NET specifics:

- `WorkingDirectory=/opt/quaymaster-net` must be the publish directory: ASP.NET reads `appsettings.json` (log filters) from the
  working directory. `ExecStart=/usr/bin/dotnet /opt/quaymaster-net/Quaymaster.Api.dll` needs the ASP.NET Core 10 runtime.
- `QM_WEB_DIR=/opt/quaymaster-net/web/dist`, `QM_DATA_DIR=/var/lib/quaymaster-net`; `HOME` points at the data dir because
  `ProtectHome=true` hides the real home and the Compose CLI reads `$HOME/.docker`.
- `ReadWritePaths` lists the data dir and every stack directory (`/opt/stacks` in the sample); each path must exist or the unit
  fails with a namespace error. Add your own stack directories there.
- The `docker` and `docker compose` CLIs must be on `PATH` for Compose actions.

## 3. Differences from the Node original

- Data dir default is `/var/lib/quaymaster-net` (not `/data`); the `QM_*` surface is otherwise the same.
- `dotnet Quaymaster.Api.dll gen-secret | hash-password` replace `node server/dist/cli.js ...`. The `backup`, `restore` and `check`
  subcommands of the original CLI are not implemented; back up with `sqlite3 quaymaster.db ".backup 'dest.db'"` or copy the
  database with the service stopped.

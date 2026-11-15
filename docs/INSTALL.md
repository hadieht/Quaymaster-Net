# Installation

Two supported modes: **container** (recommended) and **native** (systemd). Both read the same `QM_*` environment
variables; [`.env.example`](../.env.example) lists all of them and [CONFIGURATION.md](CONFIGURATION.md) explains each.
Deployment details (image layout, hardening, the unit file) are in [DEPLOY.md](DEPLOY.md).

Quaymaster needs access to the Docker socket, which is root-equivalent on the host. Read
[SECURITY-MODEL.md](SECURITY-MODEL.md) first, and do not expose the service to the internet.

> **No published image yet.** The .NET port is not published to a registry. You build the image from a checkout of this
> repository; there is no `docker pull` step.

## Requirements

|       | Container mode                                                  | Native mode                                                                                                                      |
| ----- | --------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| Host  | Linux with Docker Engine and the Compose plugin                 | same                                                                                                                             |
| Extra | nothing (the image ships the Docker CLI and the Compose plugin) | .NET 10 SDK to build (ASP.NET Core 10 runtime to run), Node.js 22+ to build the UI, `docker` CLI with `docker compose` on `PATH` |
| Disk  | a few hundred MB for the image, plus a small data volume        | similar                                                                                                                          |

The image build downloads the .NET SDK and Node base images, so the first build takes a few minutes.

## 1. Build the image

From a checkout of this repository:

```bash
docker build -t quaymaster-net:dev .
```

The build is multi-stage: Node builds the web UI, the .NET SDK publishes the API, and the runtime stage
(`mcr.microsoft.com/dotnet/aspnet:10.0`) adds `tini`, `curl` and the Docker CLI with the Compose plugin. It runs as
uid/gid 10001. `docker-compose.yml` builds the same image for you (`docker compose up --build`), so this step is optional
when you use Compose.

## 2. Create the configuration

```bash
cp .env.example .env
chmod 600 .env
grep -v '^QM_SECRET_KEY=' .env > .env.new                                              # drop the blank line from the example
docker run --rm quaymaster-net:dev dotnet /app/Quaymaster.Api.dll gen-secret >> .env.new   # appends QM_SECRET_KEY=...
mv .env.new .env && chmod 600 .env
```

Quaymaster will not start while the key is missing; it exits with `QM_SECRET_KEY (or QM_SECRET_KEY_FILE) is required`
and Docker's restart policy keeps relaunching it.

Edit `.env`:

- `QM_SECRET_KEY`: already written by the commands above. **Store a copy outside the server** (password manager):
  backups do not contain it.
- `QM_PUBLIC_ORIGIN`: **required as soon as you open the UI by anything other than `127.0.0.1` or `localhost`** (LAN IP,
  host name, Tailscale). Comma-separated, scheme and port included, for example
  `http://127.0.0.1:8787,http://192.168.1.50:8787`. The value _replaces_ the default list, so keep the loopback entry
  if you also use it. Otherwise login fails with "Missing or unexpected Origin header".
- `QM_STACK_PATHS`: absolute directories that contain your Compose projects, for example `/opt/stacks`. The sample file
  ships `/srv/stacks`: change it or remove the line.
- Review the other **active example values** listed under
  [Cautions about `.env.example`](CONFIGURATION.md#cautions-about-envexample)
  (`QM_REGISTRY_INSECURE`, `QM_DOCKER_ROOT_MOUNT`, `QM_WEB_DIR`).
- `QM_COOKIE_SECURE`, `QM_TRUST_PROXY`, `QM_PROTECTED`: when you put a reverse proxy in front (step 5).

## 3. Start (container mode)

```bash
export DOCKER_GID=$(stat -c %g /var/run/docker.sock)     # the compose file needs it on every compose command
docker compose up -d --build
docker ps --filter name=quaymaster-net                    # wait for "healthy"
docker logs quaymaster-net 2>&1 | grep -A2 "first-run setup"    # the one-time setup token
```

Open `http://127.0.0.1:8787`.

> `DOCKER_GID` must be set for **every** `docker compose` command in this directory (`logs`, `restart`, `down` ...);
> the file refuses to load without it. Put `DOCKER_GID=<number>` into `.env` to make that permanent.

What the compose file sets up:

- **User and socket.** Runs as `10001:10001`; `group_add` gives it the host's Docker group, so it needs `DOCKER_GID`.
- **Volumes.** The named volume `qmdata` at `/var/lib/quaymaster-net`; the Docker socket. **Your stack directories are not
  mounted by default**; see below.
- **Hardening.** Read-only root filesystem (only the data volume and a 64 MB `/tmp` tmpfs are writable),
  `no-new-privileges`, all capabilities dropped, 512 MB memory, 1 CPU, 256 pids, `tini` as PID 1, health check on `/healthz`.
- **Port.** `127.0.0.1:8787` by default (`DEPLOY_BIND`, `DEPLOY_PORT`).
- **Self-protection.** `QM_SELF_CONTAINER=quaymaster-net` matches the fixed `container_name`. If you rename the container,
  change it too.

### Mounting your stack directories

Docker resolves bind-mount paths on the **host**, and Compose resolves relative paths against the project directory.
So each directory in `QM_STACK_PATHS` must be mounted into the container at the **identical absolute path**. Add one
line per directory to the `volumes:` list of `docker-compose.yml`:

```yaml
- /opt/stacks:/opt/stacks
```

Without a stack mount, discovery, monitoring, logs and start/stop/restart still work; updates, redeploys and the
resolved-configuration view do not (the project is shown as _limited_, with the reason and the exact mount line to add).
Add `:ro` to the mount for a monitoring-only setup.

### Optional: free-space checks

Pulls and updates fail closed when the free space of the Docker root cannot be measured. Either mount the Docker root
read-only (uncomment `- /var/lib/docker:/var/lib/docker:ro` in the compose file) and point `QM_DOCKER_ROOT_MOUNT` at it, or
rely on `QM_DOCKER_ROOT_MOUNT=/` from the sample file (the container's own root; on an overlay2 host that is the same
filesystem as the Docker root). To check that the reading works, call `GET /api/v1/system/host` while logged in:
`dockerRoot` holds the sizes, `dockerRootError` explains a failure.

### Trying it without the Docker socket

To see the UI and the first-run flow without giving Quaymaster the socket (reads then answer `docker_unavailable`):

```bash
KEY=$(docker run --rm quaymaster-net:dev dotnet /app/Quaymaster.Api.dll gen-secret | sed -n 's/^QM_SECRET_KEY=//p')
docker run -d --name qm-try -p 127.0.0.1:18787:8787 -e QM_SECRET_KEY="$KEY" quaymaster-net:dev
curl -fsS http://127.0.0.1:18787/healthz            # {"status":"ok"}
docker rm -f qm-try
```

To actually log in through a host port other than 8787, also pass `-e QM_PUBLIC_ORIGIN=http://127.0.0.1:18787`: the
default origin list is derived from the port **inside** the container (8787), so a different published port fails the
origin check (see [Troubleshooting](../README.md#troubleshooting-and-faq)).

## 4. Create the first admin

There is no default password. Two options:

1. **Setup token (default).** Leave `QM_ADMIN_USER` empty. On first start Quaymaster prints a one-time token to stderr
   and writes it to `<data dir>/setup-token` (mode 0600; valid 60 minutes; five attempts per IP; compared in constant time;
   deleted on success). Enter it on the first-run page together with a username and a password of at least 12 characters.
   If the token expires, restart Quaymaster to issue a new one.
2. **Pre-seeded admin.** Set `QM_ADMIN_USER` and either `QM_ADMIN_PASSWORD_HASH` or `QM_ADMIN_PASSWORD_FILE`. This is applied
   only while there are no users, and a later password change in the UI is never reverted by a restart.

   ```bash
   docker run --rm quaymaster-net:dev dotnet /app/Quaymaster.Api.dll hash-password 'a long password'
   # prints:  QM_ADMIN_PASSWORD_HASH='scrypt$...'   <- paste the whole line, keep the single quotes
   ```

   Without the argument the command reads the password from stdin, or from a hidden prompt when run with `-it`.
   Passwords shorter than 12 characters are refused. Putting the password on the command line leaves it in your shell
   history: prefer the prompt or stdin for real passwords.

## 5. Reverse proxy and TLS (recommended)

The repository does not ship proxy configuration files. Whatever proxy you use, it must:

- terminate TLS and forward to `127.0.0.1:8787` (or the container's address),
- **not buffer responses** for the proxied location: the UI uses server-sent events for logs and live updates,
- pass the client address in `X-Forwarded-For`,
- keep the browser's `Origin` header intact (do not rewrite it).

Then set, in `.env`, and recreate the container (`docker compose up -d --force-recreate`; `.env` is only read when the
container is created):

```ini
QM_PUBLIC_ORIGIN=https://quaymaster.example.com
QM_COOKIE_SECURE=true
QM_TRUST_PROXY=<proxy IP or CIDR>
QM_PROTECTED=<proxy container name>
```

If the proxy runs in a container on the same Docker network, `QM_TRUST_PROXY` is the proxy's address or that network's
subnet. `true`, `*` and `0.0.0.0/0` are rejected. Caddy's `reverse_proxy 127.0.0.1:8787` is the shortest working
setup on the same host (it streams server-sent events without extra settings), but it has not been tested as part of this
documentation.

Always set `QM_TRUST_PROXY` when a proxy is in front. Otherwise every user appears to come from the proxy's address, and
the per-IP login limiter can lock everyone out after a few failed attempts.

## 6. Monitoring-only deployments

`QM_READ_ONLY=true` makes every mutating action refuse. For stronger isolation you can point `QM_DOCKER_SOCKET` at a
read-only socket proxy (`tcp://socket-proxy:2375`); the code supports that address form, but **no socket-proxy compose
example ships with this repository and the setup was not exercised for this documentation**. Compose actions and updates
are unavailable in that arrangement.

## Native install (systemd)

```bash
git clone <this repository> && cd quaymaster-net          # or work from your existing checkout
dotnet publish src/Quaymaster.Api -c Release -o publish
npm ci && npm run build                                    # builds shared/ and web/ into web/dist

sudo useradd --system --home /var/lib/quaymaster-net --shell /usr/sbin/nologin -G docker quaymaster
sudo install -d -o quaymaster -g quaymaster /var/lib/quaymaster-net
sudo install -d /opt/quaymaster-net/web /opt/stacks
sudo cp -a publish/. /opt/quaymaster-net/
sudo cp -a web/dist /opt/quaymaster-net/web/dist           # same layout as the image

# configuration: copy .env.example, set QM_SECRET_KEY etc. (the unit sets QM_DATA_DIR and QM_WEB_DIR itself)
sudo install -m 0640 -o root -g quaymaster .env /etc/quaymaster-net.env

sudo install -m 0644 deploy/quaymaster-net.service /etc/systemd/system/quaymaster-net.service
sudo systemctl daemon-reload
sudo systemctl enable --now quaymaster-net
sudo journalctl -u quaymaster-net -n 50        # the setup token appears here
```

Notes:

- The unit runs as `quaymaster` with `SupplementaryGroups=docker`, `ProtectSystem=strict`, `ProtectHome=true`,
  `NoNewPrivileges` and `MemoryMax=512M`. It sets `QM_DATA_DIR=/var/lib/quaymaster-net` and
  `QM_WEB_DIR=/opt/quaymaster-net/web/dist` itself.
- `WorkingDirectory=/opt/quaymaster-net` **must be the publish directory**: ASP.NET reads `appsettings.json` (log filters)
  from the working directory. Without it, database commands are logged at Information level.
- `ReadWritePaths=/var/lib/quaymaster-net /opt/stacks`: **edit this line** to list every directory in `QM_STACK_PATHS`,
  or Compose cannot write there. Every listed path must exist, otherwise the unit fails with a namespace error. If you
  keep stacks under `/home`, relax `ProtectHome`.
- `ExecStart=/usr/bin/dotnet ...`: adjust if `dotnet` lives elsewhere. The host needs the ASP.NET Core 10 runtime.
- Native mode sees your Compose files directly; no identical-path mounts are needed. `QM_DOCKER_ROOT_MOUNT` is not needed
  if the service user can read the Docker root (usually it cannot; otherwise set it as in container mode).
- Operator commands: `sudo -u quaymaster dotnet /opt/quaymaster-net/Quaymaster.Api.dll gen-secret` (see
  [OPERATIONS.md](OPERATIONS.md) for backup and restore).

## Verify the installation

```bash
curl -fsS http://127.0.0.1:8787/healthz         # {"status":"ok"}
```

Log in and open **Settings → General**: it shows the read-only state, managed scope, stack paths, retention, and the
detected self container. Compose projects should show capability **full**; **limited** means Quaymaster cannot read their
files (see [Troubleshooting](../README.md#troubleshooting-and-faq)).

## Uninstall

Container: `docker compose down` (add `-v` only if you want to delete the data volume). Quaymaster does not modify your
containers or Compose files by itself; the rollback and staging images it created carry the tags `quaymaster-rollback/*`
and `quaymaster-staged/*` and can be removed with `docker rmi` once you no longer need them. Native:
`systemctl disable --now quaymaster-net`, remove the unit, `/opt/quaymaster-net`, `/etc/quaymaster-net.env` and, if
wanted, `/var/lib/quaymaster-net`.

# Operations

Backup, restore, upgrade and recovery. Commands assume container mode with the compose project `quaymaster-net`
(container `quaymaster-net`, data volume `quaymaster-net_qmdata` mounted at `/var/lib/quaymaster-net`; confirm the volume
name with `docker volume ls`). For native mode the data directory is `/var/lib/quaymaster-net` on the host and the
service is `quaymaster-net`.

## What state Quaymaster keeps

| What                                                                                                          | Where                                                                   | In a backup                                             |
| ------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------- | ------------------------------------------------------- |
| Users, sessions, policies, windows, schedules, runs, audit log, notification channels, registry logins, stats | SQLite `<QM_DATA_DIR>/quaymaster.db` (+ `-wal`/`-shm`)                  | yes                                                     |
| `QM_SECRET_KEY`                                                                                               | your `.env` / secret file                                               | **no**: keep it separately                              |
| Your Compose files and volumes                                                                                | your stack directories                                                  | no: Quaymaster never writes them; back them up yourself |
| Rollback and staged images                                                                                    | Docker image store, tags `quaymaster-rollback/*`, `quaymaster-staged/*` | no                                                      |

The data directory also holds `quaymaster.lock` (an OS file lock that allows only **one** instance per data directory) and,
until first-run setup is done, `setup-token`.

## Backup

> **No backup command in the CLI.** The original had `backup`, `restore` and `check` subcommands; the .NET CLI only has
> `gen-secret` and `hash-password`. Use SQLite's own tools or copy the files. Quaymaster does not schedule or prune
> backups itself.

### Online backup (Quaymaster keeps running)

Needs the `sqlite3` command on the **host** (the Quaymaster image does not contain it) and read access to the data
directory (usually root):

```bash
DATA=$(docker volume inspect quaymaster-net_qmdata --format '{{.Mountpoint}}')    # native mode: DATA=/var/lib/quaymaster-net
sudo sqlite3 "$DATA/quaymaster.db" ".backup '/var/backups/quaymaster-$(date +%F).db'"
sudo chmod 600 /var/backups/quaymaster-$(date +%F).db
sudo sqlite3 /var/backups/quaymaster-$(date +%F).db 'PRAGMA integrity_check;'      # prints: ok
```

`.backup` uses SQLite's online backup API and is safe while the WAL is active (checked against a running instance;
`VACUUM INTO '<file>'` works the same way). The backup file contains password hashes and encrypted secrets: keep it
mode `0600`.

### Offline copy (no `sqlite3` needed)

Stop Quaymaster, then archive the directory:

```bash
docker compose stop
docker run --rm -v quaymaster-net_qmdata:/data:ro -v "$PWD:/out" alpine tar czf /out/qm-data-$(date +%F).tgz -C /data .
docker compose start
```

(`DOCKER_GID` must be set for the compose commands, see [INSTALL.md](INSTALL.md#3-start-container-mode).) Native mode: stop
the service and copy `quaymaster.db` (and `-wal`/`-shm` if present).

### Keep the secret key with the backup

Store `QM_SECRET_KEY` with the backup, in a different place from the database. Without the key, stored notification and
registry credentials cannot be decrypted (the rest of the data stays usable; re-enter the credentials).

Before every schema migration of an existing database Quaymaster also writes
`backup-pre-<migration id>.db` into the data directory (a `VACUUM INTO` copy, mode 0600).

Automate with cron or a systemd timer on the host (daily is typical) and copy the files off the machine.

## Restore

Stop Quaymaster first. Replace the database, remove stale `-wal`/`-shm` files, fix ownership and mode, start:

```bash
docker compose stop
docker run --rm --user 0 -v quaymaster-net_qmdata:/data -v "$PWD:/in:ro" alpine sh -c '
  mkdir -p /data/before-restore && cp -a /data/quaymaster.db* /data/before-restore/ &&
  rm -f /data/quaymaster.db-wal /data/quaymaster.db-shm &&
  cp /in/quaymaster-YYYY-MM-DD.db /data/quaymaster.db &&
  chown 10001:10001 /data/quaymaster.db && chmod 600 /data/quaymaster.db'
docker compose start
```

(Run this from the directory that contains the backup file. The `before-restore` directory keeps the database files you
are replacing.) Native mode: stop the service, copy the file over `/var/lib/quaymaster-net/quaymaster.db`, delete
`quaymaster.db-wal` and `quaymaster.db-shm`, `chown quaymaster:quaymaster`, `chmod 600`, start.

Use the same `QM_SECRET_KEY` as when the backup was taken. Sessions stored in the backup remain valid until they expire, so
log in again if needed and review Settings. A restore procedure of this shape was run against a scratch container: the
restored database started, reported `setupRequired: false` and the old admin could log in.

If the restored database is from a **newer** Quaymaster build than the one running, the server refuses to start
("Database schema (...) is newer than this build supports"); use the newer build.

## Upgrading

Migrations are forward-only (EF Core), run at start-up and are preceded by `backup-pre-<migration id>.db`. A database written
by a newer build is refused.

```bash
# 1. back up (see above); 2. update the checkout (git pull), then rebuild and recreate:
export DOCKER_GID=$(stat -c %g /var/run/docker.sock)
docker compose up -d --build                                  # builds the new image, recreates the container, migrates on start
curl -fsS http://127.0.0.1:8787/healthz                       # {"status":"ok"}
```

Read `docs/PROGRESS.md` and the commit log first for new or changed `QM_*` variables. Because unknown `QM_*` names are
rejected, a removed variable stops the server with a message that names it. Native mode: `dotnet publish`, copy over
`/opt/quaymaster-net`, `systemctl restart quaymaster-net`.

Do not upgrade while a maintenance run is active: check Maintenance → History and let it finish. Stopping Quaymaster during
a run is handled (see below) but leaves the run `interrupted`.

**Downgrade:** stop the container, restore the pre-upgrade backup and start the previous build.

Quaymaster never updates itself and refuses to act on its own container, so this procedure is always manual.

## Recovery after a failed or interrupted update

Start in **Maintenance → History** and open the run. It shows the stored plan, every step with its output, the result per
service, and a **guidance** list with exact recovery commands where a run did not end cleanly. The recovery semantics are
the same as in the original Quaymaster.

### Run ended `rolled_back`

The update failed health checks and the previous image was restored and verified. Nothing to do except to read the
failing step's output and decide whether to try again later. The failed target digest is **quarantined**: unattended runs
skip it until the tag moves again or an admin clears the quarantine.

### Run ended `failed` or `partial` with a service left as is

This is the default for services with writable persistent mounts: auto-rollback is off because old code on migrated data
can be worse than a broken new version. The service is running (or not) on the new image, the remaining services are
`skipped`, and you got an alert (`service.left_down` when it is not running). Choose:

1. **Fix forward**: read the logs of the service, fix the cause, and redeploy.
2. **Restore the recovery point**: on the run page choose **Restore previous version…** and type the project name to
   confirm. Quaymaster re-tags the recorded previous image IDs and redeploys those services in reverse order with the
   health gate (`POST /api/v1/maintenance/runs/:id/restore`). Restore the data from your own backup first if the new
   version migrated it.

### Run ended `interrupted`

Quaymaster (or the host) stopped in the middle of a run. At the next start the run is marked `interrupted`, never resumed,
and a critical `service.left_down` notification is sent for any service that is not running, with the exact command to
bring it back. Intended-but-unfinished steps are written ahead of time, so the guidance states whether the image tag may
have moved. Typical recovery:

```bash
cd <project directory>
docker compose --project-name <project> up -d --no-deps --pull never <service>
```

Use the commands from the run's guidance rather than this template: they contain the correct project directory and file
list.

### Run ended `blocked` / `blocked_drift`

Nothing was deployed. Compose files differ from the running containers (or free space is too low, or a validation
failed). The plan's drift section shows what differs. Resolve it, then plan again.

### Run ended `approval_stale`

The plan changed between approval and execution (a new image appeared, a container changed, a policy changed). It was not
executed. A new approval request was created; review and approve the new plan.

### A `run.conflict` alert (another tool changed the containers)

Quaymaster stops without rolling back when something else modifies the run's containers or images mid-run, and sends a
`run.conflict` alert. Inspect the state by hand before doing anything else.

### Standalone containers

A standalone update renames the old container to `<name>-qm-old-<epoch seconds>`, stops it, sets its restart policy to `no`
and keeps it as the recovery point. If the new container is bad: stop it, rename it aside, rename the old one back, set the
restart policy back (the run output records the original) and start it. **This restore is manual**, there is no restore
button for standalone runs, and the `-qm-old-*` containers are not removed automatically (remove them yourself when you
no longer need them).

### Rollback images and cleanup

Rollback images are tagged `quaymaster-rollback/<hash>:<run-id>` and protected for `QM_ROLLBACK_RETENTION_DAYS` (default 7).
Staged pulls use `quaymaster-staged/…`. List them with `docker images 'quaymaster-*'`. Expired rollback tags are an
image-cleanup category (preview first, see below); once removed, the previous image can only be recovered from the
registry.

## Cleaning up images, networks and volumes

Admins can remove items from the **Images** and **Volumes & Networks** pages, one by one or with **Clean up…**. Bulk
cleanup always shows a preview first (what, how big, what is kept and why); the preview expires after 10 minutes, belongs to
the admin who created it, can be used once, and only what it lists can be removed. Everything is re-checked immediately
before removal, so something that started being used in the meantime is skipped.

| Resource | Removed when                                                                                                  | Never removed                                                                                                                                                                   |
| -------- | ------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Image    | no container (running or stopped) uses it, older than `QM_CLEANUP_MIN_AGE_HOURS`, no Compose project names it | images any container uses, rollback/staged images inside retention, anything outside `QM_MANAGED_SCOPE`                                                                         |
| Network  | no container is attached or configured for it                                                                 | `bridge`/`host`/`none`, networks of a Compose project that still exists on disk, Quaymaster's own network                                                                       |
| Volume   | no container (running or stopped) references it                                                               | Quaymaster's own data volume, volumes outside `QM_MANAGED_SCOPE`, volumes of a Compose project that still exists (unless you opt in), anything a stopped container still mounts |

Things to know:

- **Removing a volume deletes its data permanently. It cannot be recovered.** Take a backup first (for example
  `docker run --rm -v VOLUME:/data -v "$PWD":/backup alpine tar czf /backup/VOLUME.tgz -C /data .`). Quaymaster does not
  make backups of volumes.
- Volumes are never part of a cleanup unless you tick them. "Anonymous" volumes (a 64-character hash name, created by
  `docker run -v /path`) are the default once volumes are enabled; "named" volumes are a separate opt-in and may hold
  databases of stacks that are only stopped. Typing the exact volume name (single) or `delete N volumes` (bulk) is required
  for every volume removal.
- A stopped container counts as a user: its volumes, networks and image are kept. Removing the container first is your
  decision; Quaymaster's cleanup never removes containers on these pages.
- Untagged images and expired internal tags are kept under a non-empty `QM_MANAGED_SCOPE` unless you give a label filter.
- An image with several tags can be untagged one tag at a time (the image and its disk usage stay) or removed entirely.
  Image sizes in the preview count only the layers that image owns; shared layers are freed when the last image using them
  is removed.
- After a run, Quaymaster shows the estimate and the measured result: the drop in what Docker accounts for (image layers
  and volumes) and the change in free space on the Docker disk. The latter is noisy if other things write to the disk.
  Networks free no space.
- Docker build cache is not cleaned up by Quaymaster. Use `docker builder prune` yourself if you want that.
- Read-only mode (`QM_READ_ONLY`) and `QM_MANAGED_SCOPE` refuse removals server-side. Every removal and refusal is in the
  audit log with the number of bytes.

## Lost admin access

There is no password-reset command. If another admin exists, they can set a new password for you (Settings → Security).
If the only admin is locked out, you can restore a backup taken before the password changed, or, **with Quaymaster
stopped**, empty the users table so that the first-run flow (setup token or `QM_ADMIN_USER`) applies again. This needs
`sqlite3` on the host:

```bash
docker compose stop
sudo sqlite3 "$(docker volume inspect quaymaster-net_qmdata --format '{{.Mountpoint}}')/quaymaster.db" 'PRAGMA foreign_keys=ON; DELETE FROM users;'
docker compose start          # a new setup token is printed
```

**This procedure was not run for this documentation** (the SQL was deliberately not executed in the verification
environment), and the same procedure was also untested for the original. It removes all users and, by cascade, their
sessions, but keeps audit history, policies and schedules. Take a backup first.

## Health and logs

- `GET /healthz` returns `{"status":"ok"}` without details; the container health check uses it. It answers only after the
  database is ready. `GET /api/v1/system/health` (login required) gives the details: Docker reachable, API and engine
  versions, event stream state, uptime.
- Logs go to stdout/stderr in the standard .NET console format (`docker logs quaymaster-net`, `journalctl -u quaymaster-net`).
  `QM_LOG_LEVEL=debug` adds detail. Learned secret values are scrubbed from log lines and exception text.
- Settings → General shows the installation's read-only state, scope, stack paths and retention; the audit log
  (Logs & Events, admins) records every login, action and run.
- A second instance on the same data directory exits at start with "Another Quaymaster instance is using <dir>".
- A Docker daemon that is down at start does not stop Quaymaster: reads answer `docker_unavailable` (503) and it reconnects
  in the background.

## Project discovery, file access and stop monitoring

**Discovery.** Quaymaster lists every Compose project Docker reports (labels `com.docker.compose.project`, `.working_dir`,
`.config_files`, `.service`, plus `docker compose ls -a`), including stopped ones, with no configuration. Stacks that are
down are also found by a bounded, read-only scan of the allowed stack roots.

**What needs a mount, and why.** Start, stop, restart, logs and the dependency view work for every project. Update,
redeploy and the resolved configuration run `docker compose` against the project's files, so Quaymaster must read them at
the path Docker recorded. Mount the directory into Quaymaster at the identical path (the project page shows the reason and
the mount line to add); it is then trusted automatically (`QM_STACK_AUTO_MOUNTS`), or list its parent in `QM_STACK_PATHS`.
Unlike the original, there is **no file-helper container** to show the configuration of unmounted projects.

**Stop monitoring.** On a project page choose _Stop monitoring_ and type the project name. The project disappears from
lists, update checks, notifications, schedules and policies. Nothing is stopped, removed or modified. Restore it under
Settings → Ignored projects (or `DELETE /api/v1/ignored/:id`). Standalone containers can be ignored the same way.

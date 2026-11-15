# Red-team audit: quaymaster-net before publish (T7, 2026-11-15)

Reviewer: rook (red team). Scope: the whole .NET port at HEAD `2f24ddd` compared with the original
(`/root/projects/quaymaster`, `docs/SECURITY-MODEL.md`, `docs/reviews/0*.md`). Method: code reading, scratch unit/integration
harnesses (outside the repo), and a **live run** against the real daemon. Live instances were bound to `127.0.0.1:18788-18790` with
`QM_MANAGED_SCOPE=qmtest-*`, and only `qmtest-*` / `quaymaster.test=true` fixtures were used. Nothing in the repo was changed except
this file. Nothing was pushed.

Proof scripts are kept outside the repo, in `~/.hermes/profiles/rook/cache/scratch/t7/` (`p1`–`p12`, `qm.py`). Scratch harnesses are
in `…/scratch/qmrt/` (update engine), `…/scratch/rt-pipe/` and `…/scratch/rt-redact/` (redaction), and `…/scratch/contract-probe/`
(contract).

## Verdict: **not-ready**. It can be published once the must-fix list below is fixed.

The perimeter is solid: auth, CSRF/Origin, the compose argv, read-only mode and the setup token all held under attack. Four problems
still break promises the original keeps, and all four were shown live or with a test:

1. Secrets reach viewers.
2. A protected container gets recreated by maintenance.
3. Account changes are lost to races.
4. The update engine can report a deploy that worked as failed, and leave a service changed without guidance.

### Must fix before publish
F-01, F-02, F-03, F-04, F-05, F-06, F-07. Each needs a regression test, including wiring the `qmtest-secrets` fixture into a live test.

---

## Findings

### F-01 — HIGH — Learned secrets are not scrubbed from API responses (regression)
- **Where**
  - The original scrubs every JSON response with the learned-secret `globalScrubber` in an `onSend` hook
    (`server/src/app.ts:178-194`). The port has no equivalent: no `Scrubber` use anywhere in `src/Quaymaster.Api`.
  - `ContainerService.cs:265` and `ComposeService.cs:227` *learn* the values. The responses (`ContainerService.cs:281`,
    `ComposeService.cs:260`) are only key/pattern redacted.
- **Proof (live, p5)**
  - With the `qmtest-secrets` fixture up, a **viewer** gets `DB_PASSWORD` and `API_TOKEN` values in clear text:

    ```
    viewer GET /containers/qmtest-secrets-leaky-1           200 leaked=['fx-env-password-7c1','fx-env-token-4d2']
         ... "Cmd": ["sh","-c","echo \"starting with password fx-env-password-7c1 ••• token fx-env-token-4d2\"; …"]
    viewer GET /compose/projects/qmtest-secrets/config      200 leaked=['fx-env-password-7c1','fx-env-token-4d2']
    ```

  - The same leak happens through `Args`, `Cmd`, `Healthcheck.Test` and the compose `command`. The common real-world case is
    `mysqladmin ping -p${MYSQL_ROOT_PASSWORD}`.
  - A scratch harness (`rt-pipe`) runs the same input through the original's pipeline. The original masks it after `onSend`; the port
    does not.
  - The fixture was built for exactly this leak ("the test suite asserts none of them appears in any API response"). No .NET test uses
    it (`grep -r qmtest-secrets tests/` finds nothing).
- **Impact:** any viewer can read database and root passwords and tokens of every container whose command or healthcheck echoes them.
- **Fix**
  - Add response middleware: buffer `application/json` responses under `/api/` (except `/auth/*` and `/users*`, as the original does)
    and run `scrubber.Scrub`.
  - Add a live test over all GET endpoints with `qmtest-secrets` up.

### F-02 — HIGH — Maintenance runs recreate QM_PROTECTED / self / out-of-scope namespace dependents (regression)
- **Where**
  - `PlanService.cs:236-251` adds `network_mode`/`pid`/`ipc: service:x` and `volumes_from` dependents without `ExclusionReason`.
  - `RunEnvironment.UpAsync` (`RunEnvironment.cs:94-110`) calls `MutationGateway.RunComposeAsync` (`MutationGateway.cs:276-295`). That
    path checks only the subcommand, the argv and the maintenance gate. It never calls `AssertProjectAsync` or `AssertContainerAsync`.
  - The original's `deployServices` (`compose/service.ts:497-505`) asserted the project and **each container** before every `up`.
- **Proof (live, p11)**
  - Setup: a temporary stack (`provider` updatable, `side` with `network_mode: service:provider`) and an instance with
    `QM_PROTECTED=qmtest-rt7ns-side-1`.
  - A direct `POST /containers/qmtest-rt7ns-side-1/actions/restart` is refused: `409 self_protected`. The plan *itself* lists
    `excluded: side "Refused: listed in QM_PROTECTED"` **and** `('side','dependent','provider')`.
  - After create (`applyDrift:true`, needed for this dependent) and approve, the run `completed` with steps
    `deploy side succeeded, health side succeeded`.
  - The protected container was recreated: `qmtest-rt7ns-side-1 ccb795c5495f → 6c350c343694`.
- **Impact**
  - The protection list and self-protection do not hold for maintenance.
  - If Quaymaster runs as `network_mode: service:<sidecar>` (a VPN sidecar is a common setup), updating the sidecar makes Quaymaster
    recreate itself mid-run with `--force-recreate`, killing the executor.
  - A container whose name is out of scope inside an in-scope project is also recreated.
- **Fix**
  - Apply `ExclusionReason` to dependents. A protected dependent must block the plan.
  - Call `AssertProjectAsync(perService:true)` and `AssertContainerAsync` for every container of the service inside `UpAsync`, as the
    original did.
  - Better still, make `RunComposeAsync` take a typed target, so the gateway can assert it itself.

### F-03 — HIGH — Lost update: "demote / disable user" is silently undone by a concurrent password change
- **Where**
  - `AuthService.cs:272-274`: `FindByIdAsync`, then scrypt `HashAsync` (~0.5–1 s), then `UpdateAsync(user)`.
  - `UserRepository.UpdateAsync` uses `db.Users.Update(user)`, which writes **every column**, and the entity has no concurrency token.
  - The same pattern is in `UserService.SetPasswordAsync`. The original's synchronous better-sqlite3 calls wrote only the password
    column.
- **Proof (live, p4)**
  - admin2 changes its own password while admin demotes admin2 0.7 s later.

    ```
    attempt 5: {'delay': 0.7, 'demote': 200, 'pw': 200} -> admin2 role now = admin
    LOST UPDATE: demotion answered 200 and was audited (user.update success), but admin2 is still admin.
    admin2 logs in again with the new password; role = admin
    ```

- **Impact**
  - The defender's "disable this compromised admin" answers 200 and shows success in the audit log, yet it is reverted.
  - An attacker who keeps a password change in flight (each takes ~1 s) wins most races. They then log in again as an enabled admin with
    a password they know.
- **Fix:** update only the changed columns (`ExecuteUpdateAsync(set password_hash, password_changed_at)` and
  `set role/disabled/logs_read`). Re-check the role after the update, or add a row-version concurrency token.

### F-04 — MEDIUM — Last-admin guard is not atomic: two admins can lock everyone out
- **Where:** `UserService.cs:69-75` and `:98-103` count active admins, then update, in separate DbContexts. The original was atomic
  because it ran synchronously.
- **Proof (live, p3):** two admins disable each other at the same instant (barrier).

  ```
  attempt 2 {'admin2->admin': 200, 'admin->admin2': 200}
  users: [(1,'admin','admin',1), (2,'viewer1','viewer',0), (3,'admin2','admin',1)]   # zero active admins
  login admin: 401 / login admin2: 401
  ```

  `QM_ADMIN_USER` only applies when **no users exist**, so the only way back is to edit the DB by hand.
- **Fix:** do it in one statement or transaction, e.g.
  `UPDATE users SET … WHERE id=@id AND (SELECT count(*) FROM users WHERE role='admin' AND disabled=0 AND id<>@id) >= 1`, or a
  `BEGIN IMMEDIATE` transaction around the count and the write. The same applies to delete.

### F-05 — HIGH — A good deploy can be judged failed (stale "fresh" container list); for stateful services this means downgrade guidance on migrated data
- **Where**
  - `ContainerService.ListRawAllAsync(fresh:true)` (`ContainerService.cs:104-116`) joins an **in-flight** single-flight fetch
    (`_inflight ??= FetchAsync()`). `Invalidate()` clears only `_cache`.
  - So the executor's post-`compose up` read (`RunExecutor.Deploy.cs:156`, after `RunEnvironment.cs:106-109`) can get a list fetched
    *before* compose finished.
  - The StatsSampler polls every 10 s, so a fetch is often in flight. `PostDeployLive` (`Deploy.cs:71-76`) then falls back to the old
    ids, and the gate reports "container … disappeared".
  - This is the in-image failure that PROGRESS.md:482 recorded and could not reproduce.
- **Proof (scratch harness RT5w/RT5s, wrapping the repo's `RunWorld` fakes)**
  - Writable service: `failed … (container 2222… disappeared) … tag put back on the previous image`, `tag=aaaa running=bbbb`,
    `service.left_down`. The guidance says to retag the old image and run `compose up`.
  - Stateless service: a needless rollback, and the good digest is quarantined, so unattended runs never retry it.
- **Fix:** after a mutation, call `engine.ListContainersAsync` directly (no single-flight join). Wait until the expected number of
  *new* ids is listed (bounded), and never fall back to the previous ids.

### F-06 — HIGH — A transient Docker error after `compose up` leaves the service changed with no rollback, no guidance, no `service.left_down` (regression)
- **Where**
  - `Deploy.cs:156` (`ServiceContainersAsync`) and the health gate's `env.InspectAsync` (`HealthGate.cs:51` →
    `RunEnvironment.cs:76` → `DockerHttp.cs:78-80`) throw `AppException(503)` on a socket timeout.
  - This propagates through `DeployOneAsync`'s `catch { CompleteIntentAsync; throw; }` (`RunExecutor.cs:382-386`) to the generic
    "Unexpected error" handler (`:343-347`).
  - The original's `inspectLite` (`executor.ts:816-833`) swallowed the error, so it was treated as a failed health check, which led to
    rollback and guidance.
- **Proof (scratch RT1):** `status=failed reason=Unexpected error: Docker daemon unreachable: timeout guidance=null notes=run.failed
  tag=bbbb running=bbbb openIntents=0`.
  - The new image runs unverified and the user's tag stays moved.
  - Because the intent is closed, restart recovery cannot report it either.
- **Fix**
  - Map inspect/list errors inside the gate to "unhealthy" (with retries).
  - Wrap everything after the deploy in one failure path (restore the tag, write guidance, run `AlertLeft`).
  - Leave the intent open on unexpected exceptions.

### F-07 — MEDIUM — A run can stay `running` forever and block every run/restore/standalone update until restart
- **Where**
  - `TryReplanAsync` (`Deploy.cs:78-88`) catches only `AppException`, `InvalidOperationException` and `HttpRequestException`.
    `RegistryException` (for example a Docker Hub 429) escapes. The original used `.catch(() => null)`.
  - Neither the escaping exception nor a failing `runs.FinishAsync` after `ctx.ReleaseLock()` (`Deploy.cs:488-489`) writes a terminal
    status.
  - Then `EnsureNoRunActiveAsync` (`RunService.cs:99-105`) refuses everything. There is no watchdog.
- **Proof (scratch RT1b):** `thrown=RegistryException … status=running runningBlocksAll=True`.
- **Note on T6a:** releasing the lock early did not create a double-run window. The DB `running` row still gates.
- **Fix:** add a `finally` that always writes a terminal status (with retry and `CancellationToken.None`), and catch all
  non-cancellation exceptions in the replan.

### F-08 — MEDIUM — One `RegistryException` permanently kills the maintenance monitor (scheduler, approval expiry, disk-low)
- **Where**
  - `PlanService.cs:556` filters on `AppException or HttpRequestException or TaskCanceledException`. A 429 from
    `CompressedSizeAsync` (`RegistryClient.cs:231`) escapes `FireAsync` (`MaintenanceMonitor.cs:143`), then `Guarded` (`:99`), then
    `RunAsync` (`:65`).
  - `QmBackgroundHost.RunIsolatedAsync` logs "crashed" and never restarts the task.
- **Impact:** it fails safe (nothing runs), but every schedule, approval expiry and disk alert stops silently until restart.
- **Fix:** catch per occurrence and per tick, and restart crashed background tasks with back-off.

### F-09 — MEDIUM — With `QM_VISIBLE_SCOPE`, the shared-tag fence and several safety reads are blind to hidden containers (regression of RT4-04)
- **Where:** `ContainerService.cs:119-127` applies visibility *inside* `ListRawAllAsync`, which the docs call "every container… for
  safety". `PlanService.cs:305-356` therefore always counts `hidden = 0`.
  - The same goes for the per-service container asserts in `ComposeService.cs:559-563` (`RawContainersAsync`).
- **Impact:** a tag move silently changes the image that an out-of-scope project, hidden from view, will run on its next recreate.
- **Fix:** run every safety computation on `engine.ListContainersAsync` with no visibility filter. Add a second test instance with
  `QM_VISIBLE_SCOPE`.

### F-10 — MEDIUM — Log lines and error envelopes are not scrubbed (regression, defence in depth)
- **Log lines:** `Program.cs:27-28` uses plain `AddConsole()`. The original logger scrubbed messages, fields and `err.stack`.
  - Raw `e.Message` / exception text is logged at `ExceptionHandlingMiddleware.cs:54`, `MaintenanceMonitor.cs:67/101/145`,
    `StatsSampler.cs:129/139` and `NotificationService.cs:252`.
- **Error envelopes:** `ErrorEnvelope.cs:45-47` writes `message` unredacted. The original ran
  `redactText(globalScrubber.scrub(message))` and `redactDeep(details)` (`app.ts:233-234`).
  - Raw messages reach clients at `StandaloneExecutor.cs:244` (`Pull failed: {e.Message}`), `ImageCleanupService.cs:86` and
    `StreamEndpoints.cs:86`.
- **Proof:** a live grep of `server.log` found no `fx-` value, so I have no concrete leak. The protection layer is simply absent.
- **Fix:** add a scrubbing logger provider/formatter, and scrub in `ErrorEnvelopeWriter`.

### F-11 — LOW — Read-only mode is enforced only in the gateway, not in the Docker transport
- **Where:** `DockerHttp.PostJsonAsync` and the Docker.DotNet calls in `DockerEngineAdapter.cs:353-555` have no method/path
  allow-list.
- **Spec gap:** SECURITY-MODEL says read-only is enforced "in the Docker transport as an allow-list … so a bug in a route cannot bypass
  it". The port relies on `MutationGateway` plus the architecture test.
- **Live check (p9):** every mutating route tried was refused with `read_only`.
- **Fix:** a read-only `DelegatingHandler` on the socket client that refuses non-GET; and update the SECURITY-MODEL wording.

### F-12 — LOW — Cleanup previews are not single-use under concurrency
- **Where:** `PreviewStore.Get` and `Remove` are separate calls (`PreviewStore.cs:33-36`).
- **Proof (live, p8):** three concurrent `POST /storage/cleanup/execute` with the same previewId returned `409, 200, 200`. Two
  executions ran and two audit entries were written.
- **Saving grace:** per-item re-verification held. A container attached after the preview kept its volume ("in use by container
  qmtest-rt7-holder"), and the second execution found the volume already gone.
- **Fix:** take the preview atomically, `TryRemove` first.

### F-13 — LOW — Reject is read-then-write
- **Where:** `RunService.cs:267-276`, then an unconditional `FinishAsync` (`MaintenanceStores.cs:441-447`).
- **Impact:** a concurrent approve can be overwritten to `rejected` while the executor deploys.
- **Fix:** `WHERE status='awaiting_approval'`.

### F-14 — LOW — Leftover one-off containers block every run
- **Where:** a `compose run` container without `--rm` is dropped by the planner (`PlanService.cs:160`) but not by
  `AssertNoConflictAsync`/`ServiceContainersAsync` (`Deploy.cs:61-62,400-414`) or by recovery.
- **Impact:** runs fail with "Another tool changed …". The original had the same issue.

### F-15 — LOW — `$` anchors accept a trailing newline in identifier allow-lists
- **Mechanism:** in .NET, `$` matches before a final `\n`.
- **Proof (live, p10):**
  - Project `qmtest-upd\n` passed `ProjectNameRe` and reached discovery (404 instead of 400).
  - The same pattern appears in ~40 regexes, including the full-id check (`DockerEndpoints.cs:137`), `ServiceNameRe`, header names
    and the e-mail address check (`ConfigReader.cs:289`).
- **Impact:** no exploitable path found; lookups fail closed.
- **Fix:** use `\z`, or `RegexOptions.ECMAScript`-style anchoring.

### F-16 — LOW — Token patterns miss tokens next to non-ASCII letters (port-only)
- **Where:** `Redactor.cs:49,54-66`. In .NET, `\b`/`\w` are Unicode-aware.
- **Proof:**
  - `令牌ghp_…6789 已过期` is masked by the original, not by the port.
  - The reverse also happens: `passé=x` is masked only by the port.
  - All 59 ASCII cases gave identical output (`rt-redact`).
- **Fix:** ASCII lookarounds.

### F-17 — LOW — Other update-engine weaknesses (from the scratch harness; also present in the original unless noted)
- The scheduler backlog is chunked at 500. Above that it can fire a stale occurrence plus later ones (`MaintenanceMonitor.cs:118,131`).
- When two services in one run share a tag, a later service's rollback puts the tag back on the old image under an updated earlier
  service (`Deploy.cs:216-221,303`).
- A rollback of a stopped service counts an empty container list as success (`Deploy.cs:320`).
- The pull-time watchdog uses only `QM_DISK_FLOOR_BYTES`, not 10 % (`ImageOps.cs:76-77`). The pre-check is fail-closed.
- Recovery after a crash between services says "before any container was changed" (`RunRecoveryService.cs:48-51`).

### F-18 — INFO — Supply chain
- **NuGet:** `dotnet list package --vulnerable --include-transitive` reports nothing vulnerable. Some packages are outdated
  (BouncyCastle 2.6.2→2.7.0, FluentValidation, Cronos).
- **Microsoft.OpenApi pin:** 2.13.0 is correctly past GHSA-v5pm-xwqc-g5wc (≤2.7.4), and it is only used in Development.
- **`npm audit --omit=dev`:** 2 moderate advisories in react-router 6.30.6. No reachable sink was found.
- **`npm audit` (all):** 12 issues, all dev-only (vitest/tinypool critical).
- **Dockerfile:** base images use floating tags (`sdk:10.0`, `aspnet:10.0`, `node:22-bookworm-slim`), and the apt `docker.io` /
  compose packages are unpinned.
- **CI:** actions are pinned by tag, not SHA. Safe otherwise: `pull_request`, `contents: read`, no secrets.

### F-19 — INFO — Contract (captured JSON from ~130 requests through the test harness; no high-severity mismatches)
- **`PATCH /notifications/channels/{id}` (medium; also broken in the Node original):** the web sends `type`
  (`pages/settings/Notifications.tsx:86`), and the strict schema answers `400 Unrecognized key 'type'`. Editing a channel in the UI
  therefore always fails.
- **`/compose/projects/{name}/graph` (low; also in the original):** it returns `{graph, namespaceProviders}`, but the web type is
  `DependencyGraph`. Nothing uses it yet.
- **Extra `isUpdate` field (low):** `plan.services[]` carries `isUpdate` (`PlanModels.cs:63`, a computed property leaking into the wire
  format and the stored plan). Fix with `[JsonIgnore]`.
- **`/system/health` (low):** the version fields are `null` when Docker was never reachable, but are typed `string`.
- **Run output stream (info):** `/stream/maintenance/runs/{id}` opened *before* approval answers `status` + `end` at once (p6). Live
  output is only available once the run's operation exists.

---

## Verified OK (checked, held — do not re-litigate)

| Area | How it was checked |
| --- | --- |
| **Origin/CSRF** on state-changing requests | Live p1 against `PUT /monitors/settings`. Refused (`403 origin`): no Origin, foreign origin, subdomain lookalike (`127.0.0.1.evil.example`), trailing slash, upper-case origin, `null`, Referer only. Refused (`403 csrf`): missing CSRF, upper-cased token. A lower-case header name is accepted (correct). `X-HTTP-Method-Override` ignored. Guard-path tricks (`/%61pi`, `//api`, `/API`, `/api/v1/./`, `%2F`-encoded, trailing slash) all give 401 anonymous / 403 with a cookie. Login and setup also need Origin. |
| Session fixation | Live: logging in with a chosen `qm_session` cookie issues a new random token. Tokens are 32 random bytes, stored as SHA-256 only. CSRF = HMAC(per-session secret, id hash), constant-time compare. |
| Cookie flags | Live: `Path=/; HttpOnly; SameSite=Strict; Max-Age=43200`. Code: `__Host-qm_session` plus `Secure` plus HSTS when `QM_COOKIE_SECURE` (no Domain attribute). |
| Setup token | Live: needs Origin, wrong token gives 403, token **file deleted** after setup, reuse gives `409`. File and dir modes 0600/0700. The token is claimed under a lock before any await (no double redemption). 5 attempts per IP. |
| Password change / logout / demotion close sessions **and SSE streams** | Live p2: the changed user's other session gets `event: end {"reason":"session revoked"}` and 401; the current session survives. Logout ends its own streams. Demotion ends the demoted admin's streams. |
| Login limiter vs spoofed headers | Live p12: 25 failed logins with rotating `X-Forwarded-For` / `X-Real-IP` → 429 from the 6th onward. ForwardedHeaders is only enabled with `QM_TRUST_PROXY` and then uses explicit `KnownProxies`/`KnownIPNetworks`. |
| Compose argv / command injection | Live p10: project/container refs `--help`, `-v`, `a b`, `$(id)`, backticks, `;id`, `--file=/etc/passwd`, `../x`, `/etc`, `%00`, globs → 400. Service `--build`, `-v`, `app --force-recreate`, `$(id)`, `app,db`, `../app` → 400. Bodies with `command`/`args`/`flags` → 400 unknown key. Code: `ArgumentList` (no shell), `--flag=value`, `--` before services, forbidden flags and subcommands re-checked, cleared child environment (PATH/HOME/DOCKER_HOST/LANG/DOCKER_CONFIG). |
| Read-only mode | Live p9 (`QM_READ_ONLY=true`): container restart/stop, compose restart/down, image pull, volume remove, run create, standalone, image remove → all `409 read_only`, and nothing changed (fixture uptimes unchanged). |
| Gateway is the only mutation path | Code: only `MutationGateway` uses `IDockerCommands`/`IComposeCli` (architecture test). `DockerHttp.PostJsonAsync` is only called from the adapter's command methods. No route accepts a command line. Removals are never forced; `down` never uses `-v`. |
| Label paths and stack roots | Code: label working dirs must be absolute, canonical, without `..`/control characters and not under `/proc`/`/sys`/`/dev`; realpath must be under `QM_STACK_PATHS` or a same-path mount and outside the Docker root; symlinks leaving a root give `untrusted`; the scan does not follow symlinked directories. |
| Static/SPA serving | Live p12: `..%2f` and `%2e%2e` traversals return `index.html` (no file escape). `PhysicalFileProvider` excludes dotfiles. Unknown `/api/*` → JSON 404 envelope. CSP and other headers present; `no-store` on `/api`. |
| Error envelope | Live: malformed JSON → 400, `text/plain` → 415, 2 MB body → 413, all as envelopes. 5xx are generic; no developer exception page; OpenAPI only in Development. |
| DB / files | Live: data dir 0700; `quaymaster.db`, `-wal`, `-shm`, lock and setup-token all 0600. EF/parameterised SQL (no string-built SQL found by grep). |
| Update flow end to end | Live p6/p7 on `qmtest-upd`: dry-run plan (app `update`, db/worker excluded as up to date). Viewer create/approve → 403. Wrong hash → `409 approval_stale`. **3 concurrent approves → exactly one 202**, two `409 conflict`. Run `completed`, all 10 steps succeeded, app moved `4547c1…` → `a836f6…`. Restore with a wrong confirm → `409 confirmation_required {expected}`. Restore → 202; the operation SSE gave `output…status{succeeded}`, `end`; app back on `4547c1…`. |
| Storage cleanup | Live p8: preview bound to its owner (other admin → 403). Wrong phrase → 409 with `expected`. A container attached after the preview keeps its volume. Reuse after execute → 409 (but see F-12). |
| Update engine (scratch + code) | Approve / mark-running / expiry use conditional `UPDATE … WHERE status=`. The plan hash covers ref, from-image id, digest, config hash, role, order and policy (mode, critical, approval, autoRollback, health). Re-plan at validate. Per-service drift gate. Recovery point taken before the pull and protected from cleanup. Rollback retags and asserts the image id. Auto-rollback is off for RW bind/volume mounts (anonymous included; tmpfs/ro excluded). Disk pre-check fail-closed. Startup recovery marks running/approved runs `interrupted` and never resumes. Schedule cursor moved before firing. Standalone runs are never scheduled. |
| SSRF (notifications/registry) | Scratch probes on `ResolveAllowedAsync`: decimal/octal/hex IPv4, `0`, `0.0.0.0`, `[::]`, IPv4-mapped link-local, `fe80::%eth0` and userinfo tricks are refused. The check runs at connect time (ConnectCallback), so DNS rebinding is covered. `AllowAutoRedirect=false`, `UseProxy=false`, same-host redirects ≤3. Registry credentials go only over https / allow-listed hosts; the token realm must be https and the same host or allow-listed. (Loopback/LAN allowed by design, as in the original; the health-probe URL has no outbound policy, same as the original's L-02/L-03, admin only.) |
| Crypto | SecretBox: random 12-byte nonce per encryption, HKDF-SHA256, AAD bound to table/column/row, Node-compatible vector. A missing / short (<32 B) / low-entropy key refuses start. scrypt N=2^17 default (<2^14 only in Testing), stored-hash bounds (N≤2^20, ≤256 MB), one concurrent hash with a queue of 8 and then 429, constant-time compare, dummy verify for unknown users. |
| Redaction rules | 59 ASCII adversarial cases (secret-like keys, `KEY=a=b=c`, URL userinfo, JWT, PEM, `ghp_`, webhook paths, nested arrays, compose secrets/configs, `--password`, LogConfig tokens) give output identical to the original. Audit params, operation output, log lines and events are cleaned. |

## Housekeeping
- **Removed:** every `qmtest-*` container, volume and network from this run, including the temporary `test-fixtures/stacks/qmtest-rt7ns`
  (deleted, `git status` clean). All three server processes were stopped.
- **Verified afterwards:** `docker ps -a --filter label=quaymaster.test=true` is empty, there are no `qmtest` volumes or networks, and
  the six real containers are untouched.
- **More fixture images removed than I created:** `test-fixtures/scripts/registry-down.sh` also removed the fixture images that were
  already there before the run (`qmtest-build/*`, `127.0.0.1:5055/qmtest-*`, one older `quaymaster-rollback/…` tag). All were
  throw-away test images; `upd-up.sh` recreates them. No non-fixture image was touched.
- **Gaps:** AGENTS.md is still missing (T6d). The five T6b endpoints still have no integration tests.

# Progress — quaymaster-net

Fleet progress file. Each phase appends a section: what was built, what was
verified (commands + results), what the next phase must know (conventions
settled, open questions). A retry of a phase reads this file first and
`git log --oneline` to resume, not restart.

## Conventions (settled at scaffold)

- .NET 10, nullable enabled, warnings as errors, CPM (Directory.Packages.props).
- Layers: Domain (pure) → Application (use cases/DTOs/abstractions) →
  Infrastructure (adapters) → Api (endpoints/middleware only).
- API contract = 1:1 port of /root/projects/quaymaster (read its
  docs/API.md, docs/SECURITY-MODEL.md, server/src/ for exact behaviour).
  The frontend (web/) is copied unmodified and MUST keep working without
  changes: same routes, same JSON, same error envelope, same cookie/CSRF,
  same SSE event names.
- Reference implementation (behaviour spec): /root/projects/quaymaster.
  Port behaviour, not code.

## Phase 0 — scaffold (atlas, 2026-11-14)

- Solution + 4 src projects + 2 test projects, Clean Architecture layering.
- Frontend (web/, shared/, e2e/, test-fixtures/) copied from quaymaster.
- Minimal API skeleton: /healthz + SPA static serving (QM_WEB_DIR) + OpenAPI doc.
- CI (build/test/docker/gitleaks), Dockerfile, compose, systemd unit.
- Verified: `dotnet build` 0 warnings, `dotnet test` 5/5 pass, `npm run build`
  (shared + web) OK, live smoke: /healthz → {"status":"ok"}, SPA fallback
  serves index.html, /openapi/v1.json → 200.
- Note: root npm workspaces are now [shared, web] (no server); package-lock
  regenerated. Root typescript stays ^5.6.0 for typescript-eslint; web nests
  its own ^7.0.2 (same shape as the original lock).

## Open

- GitHub remote repo hadieht/quaymaster-net not created yet (no API token on
  this host). Origin configured; push when repo exists.

## T1 — foundation: config, security pipeline, auth, DB, error envelope (forge, 2026-11-14)

**Built** (wire format identical to the original; zero changes to `web/`):
- Endpoints: `GET /healthz`, `GET /auth/status`, `POST /auth/setup|login|logout|logout-all|password`, `GET /auth/me`,
  `GET|POST /users`, `PATCH /users/:id`, `POST /users/:id/password`, `DELETE /users/:id` (all under `/api/v1`).
  Everything else under `/api/` returns the `not_found` envelope; non-API GETs fall back to the SPA when `QM_WEB_DIR`/`web/dist` exists.
- Config: `Application/Configuration/QmConfigLoader` parses the full `QM_*` surface (names/defaults/ranges from the original
  `config/env.ts`; the scrypt var is **`QM_SCRYPT_LOG2_N`**, not `QM_SCRYPT_LOG`). Unknown `QM_*` = error, all problems listed,
  exit code 2. Empty value = unset. `<14` scrypt only in the `Testing` environment. Env vars are primary; appsettings.json holds only logging.
- Layering: Domain (`User`, `Session`, `LastAdminRule`, `AuditEntry`) → Application (services, DTOs, FluentValidation validators,
  `AppException`/`ErrorCode`, `LoginLimiter`, `MutationGuard`, abstractions) → Infrastructure (`AppDbContext`, repositories,
  `ScryptPasswordHasher`, hosting) → Api (middleware + endpoints only).
- Persistence: EF Core 10 SQLite, factory-per-operation (all services are singletons), pragmas via interceptor (WAL, NORMAL,
  FK on, busy_timeout 5000), pooled connections instead of the original single connection. Data dir 0700, DB 0600
  (created with 0600 before SQLite touches it), exclusive `quaymaster.lock` (flock) = single instance. `Baseline` migration =
  the original v1+v2+v3 DDL verbatim (`Persistence/Migrations/Baseline.sql`, embedded, idempotent): 24 tables + 2 audit
  triggers (+ `__EFMigrationsHistory`, `__EFMigrationsLock`, `sqlite_sequence` = the original's "26"). A pre-upgrade `VACUUM INTO`
  backup is taken when migrating an existing DB; a DB newer than the build refuses to start.
- Audit: `IAuditLog.RecordAsync` (append-only; triggers also enforce it), retention prune in `HousekeepingTask`.
- Background: `IQMBackgroundTask` + `QmBackgroundHost` (failure-isolated, skipped with `QM_DISABLE_BACKGROUND`).
- CLI: `dotnet run --project src/Quaymaster.Api --no-launch-profile -- gen-secret | hash-password [pw]` (same output as the original,
  hash printed as `QM_ADMIN_PASSWORD_HASH='…'`; password from arg, stdin or hidden prompt).

**Middleware order** (Program.cs): ExceptionHandling → ForwardedHeaders (only with `QM_TRUST_PROXY`) → SecurityHeaders → Routing →
ApiGuard (canonical path, session auth 401, exact Origin 403 `origin`, `X-QM-CSRF` 403 `csrf`) → StaticFiles → endpoints.
Rate limiting runs in the use cases (after body validation, before hashing) because the original keys it on account+IP.

**Packages** (versions in Directory.Packages.props): Microsoft.EntityFrameworkCore.Sqlite 10.0.15, …Design 10.0.15 (design-time),
Microsoft.Extensions.Hosting.Abstractions 10.0.15, BouncyCastle.Cryptography 2.6.2, FluentValidation 12.1.1 (+DependencyInjectionExtensions),
tests: Microsoft.Extensions.TimeProvider.Testing 10.5.0. Tool: `dotnet-ef` 10.0.15 in `dotnet-tools.json` (`dotnet tool restore`).

**Verified**: `dotnet build --no-incremental` 0 warnings/0 errors; `dotnet test` 183 unit + 56 integration green; `npm run build` green;
`scripts/smoke-t1.sh` (real process, curl) all checks pass. scrypt output verified against vectors produced by the original Node implementation.

**Conventions / what later phases must know**
- New endpoint group: static class in `Api/Endpoints`, `Map(RouteGroupBuilder api)`, registered in Program.cs. Admin-only:
  `.RequireCapability(Capability.Write)`; logs: `Capability.Logs`. `/api/*` is authenticated by default (fail-closed); only
  `ApiGuardMiddleware.PublicPaths` is public. Read the caller with `http.RequireAuth()` / `http.ActorOf()`.
- Bodies: `await StrictJsonBody.ReadAsync(http, validator)` with a record of nullable fields + an `AbstractValidator` in
  `Application/Validation` (use `RequireString`/`RequireEnum` for zod-identical messages). Unknown properties are rejected.
- Errors: throw `AppException` (factories in the class); never write error JSON by hand. Add new codes in `ErrorCode` (all 24 already exist).
- Audit every mutation through `IAuditLog.RecordAsync(new AuditRecord(...))`; **no redactor yet** — do not pass secrets in `Params`
  (the original's `redact` module is a later phase; add it behind `IAuditLog`).
- DB: add an entity + `ToTable` mapping for an existing baseline table, then `dotnet ef migrations add X --project src/Quaymaster.Infrastructure`
  and **delete the generated CreateTable/CreateIndex for tables Baseline already created** (see the Baseline.cs doc comment). Columns use
  epoch-ms integers (`HasConversion(EpochMs)` pattern in `AppDbContext`).
- Background work: implement `IQMBackgroundTask`, `services.AddSingleton<IQMBackgroundTask, T>()`. Session revocation for SSE:
  subscribe to `ISessionRevocationBus`. `SessionService.IsStillValidAsync` exists for the 60 s stream re-validation.
- Tests: `QmFactory` (integration) boots the host with an isolated data dir, fake clock (`Time`) and `Testing` environment;
  `ApiClient` models a browser (cookie + CSRF + Origin). Settings go through `builder.UseSetting` (reach `builder.Configuration` early).
- Logging: CA1848 is enforced — use `[LoggerMessage]` (see `Infrastructure/Logging.cs`, `Api/Http/Log.cs`). Braces are required (IDE0011).

**Contract decisions** (wire format: no deviations)
- Default allowed origins derive from `QM_HOST`/`QM_PORT` exactly like the original (not from the Host header); IPv6 hosts are bracketed.
- Unauthenticated requests to *unknown* `/api/*` routes get 401 (as in the original, auth runs first); authenticated ones get 404.
- Wrong method on a known route = 404 envelope (Fastify behaviour), not 405.
- `/openapi/v1.json` (scaffold) is now served only in Development: it is not part of the original surface and would be unauthenticated.
- Only the first unknown JSON key is named in the strict-body error (zod lists all).

**Known limits**: no `GET /audit` yet; `SecretBox` (AES-GCM) not ported (needed by notifications/registry phases); login limiter state is in
memory (same as the original); redaction module not ported; `Kestrel` 431/400 protocol-level rejections are not enveloped.

## T2 — Docker core: containers, compose, system, events, operations, SSE (forge, 2026-11-14) — **COMPLETE**

**Status**: all T2 endpoints are implemented and were exercised live against the real daemon (`scripts/smoke-t2.sh`, read-only
posture). `dotnet build --no-incremental` = 0 warnings / 0 errors; the T1 suites still pass (183 unit + 56 integration).

**Endpoints** (`/api/v1`): `GET /system/mode|health|info|host|df[?refresh=1]` · `GET /containers`, `/containers/:ref`, `/:ref/logs` (logs:read),
`/:ref/stats`, `/:ref/stats/history?minutes`, `POST /containers/:ref/actions/:action` (admin) · `GET /compose/projects`, `/:name`, `/:name/graph`,
`/:name/config`, `/:name/validate`, `POST /compose/projects/:name/actions/:action` (admin, 202 `{operationId}`) · `GET /images|/images/:ref|/volumes|/volumes/:name|/networks|/networks/:ref` ·
`GET /events`, `GET /audit` (admin), `GET /operations`, `/operations/:id` · `GET|PUT /monitors/settings` (PUT admin) ·
SSE `GET /stream/events|stats|logs/:ref|operations/:id`.

**Layout**: Domain `Redaction/` (Redactor + Scrubber = port of redact.ts), `Safety/SafetyPolicy` (guard.ts decisions, pure), `Compose/DependencyGraph`,
`Stats/StatsMath`, `Docker/DockerNames`. Application `Docker/` (ContainerService, InventoryServices, SystemServices, IDockerEngine/IDockerCommands, stores),
`Compose/` (ComposeArgv allow-list, ComposeDiscovery, ComposeService), `Safety/` (MutationGateway, DockerBootstrap, SelfDetection), `Operations/`,
`Events/` (EventSanitizer, InventoryDiff, EventWatcher), `Stats/StatsSampler`, `Monitoring/Monitors`, `Streaming/SseConnection`. Infrastructure `Docker/` (adapter),
`Compose/` (ComposeCli, StackFiles), `Streaming/SseHub`, `Host/`, `Persistence/Repositories/DockerStores.cs`, `DockerDependencyInjection.cs`. Api `Endpoints/{Docker,Misc,Stream}Endpoints.cs`, `Http/SseResult.cs`.

**Decisions**
- **Docker.DotNet**: package `Docker.DotNet.Enhanced` **4.4.1** (the maintained fork; original `Docker.DotNet` stops at 3.125.15, same `Docker.DotNet` namespace; client via `DockerClientBuilder`).
  Hybrid adapter: Docker.DotNet for ping/version and the seven lifecycle commands (by full 64-hex id; remove is never forced, never `-v`); a small GET-only socket client for inspect/list/info/df/events/logs/stats so the daemon's PascalCase JSON passes through unchanged (typed models drop fields). API version = min(daemon, 1.55), refuses < 1.40.
- **Single gateway** `MutationGateway`: the only class allowed to use `IDockerCommands` and `IComposeCli`; read-only / managed scope / self-protection (`SafetyPolicy`) / audit. Audit params are now redacted (`AuditLog` takes the shared `Scrubber`; `Redactor.RedactDeep` on every param object).
- No SQL entities for events/stats_rollups/operations/kv/ignored_targets: parameterised SQL over the Baseline tables (no model-snapshot churn).
- Container `recreate` answers **200** `{ok:true, operationId}` exactly like the original route (the brief said 202; the original returns 200; the web UI does not care). Compose actions answer 202.
- Compose action `pull` (listed in the brief) does **not** exist in the original (pull would move tags outside the staged workflow) → unknown action = 400. 
- A daemon that is down at start does not stop the app: reads answer 503 `docker_unavailable`, `DockerBootstrap` retries lazily (and in the background).
- `GET|POST|DELETE /ignored` ("stop monitoring", admin only, `{entries}` / 201 entry / removed entry) are ported (`IgnoreService`): a pure filter that never calls Docker/Compose/files, allowed in read-only mode; audit actions `monitoring.stop` / `monitoring.restore`.
- Image refs containing `/` arrive percent-encoded (`/images/reg.io%2Fteam%2Fapp:tag`); the route value is unescaped before the safe-ref check (tested).
- **Doc bug to fix in T6**: the original `docs/API.md` lists a compose `pull` action; the original route (`routes/compose.ts` ACTIONS = up|stop|start|restart|down|redeploy) does not have it → 400 unknown action is correct here.

**Tests (T2)**: unit 216 (33 new) · integration `SystemAndContainerReadTests`, `ContainerActionTests`, `ComposeTests`, `EventsAuditMonitorsIgnoredTests`, `StreamTests`, `ArchitectureTests` against `Support/FakeDocker` (one in-memory implementation of `IDockerEngine`/`IDockerCommands`/`IComposeCli`/`IStackFiles`/`ISelfProbe`/`IHostProbe`; `DockerHarness` = admin + viewer + logs-viewer) ·
`LiveDockerTests` (`[Trait("Category","Live")]`, run `dotnet test --filter "Category!=Live"` where there is no daemon; without a reachable daemon they return early): read-only posture lists + df + info, and a throwaway container (`quaymaster.test=true`) create → logs → API remove (name confirmation) cycle. Architecture test: only `MutationGateway` (plus the adapters implementing the seams and the DI root) mention `IDockerCommands`/`IComposeCli`.

**Known gaps for T3–T5**: read-only file helper (`compose/helper.ts`) not ported → limited projects have `fileAccess:"none"`; maintenance gate (`assertNotUnderMaintenance`) not wired; monitors cover container/host alerts only (update/maintenance monitors + real notification delivery = T5; default sink writes `monitor.alert` audit rows with an in-memory cool-down);
stats history/ring buffer are in-memory + rollups; SSE `/stream/maintenance` is T4; image/volume/network removal & prune, registry, updates = later phases.

## T3 — updates engine, image/volume/network removal, storage cleanup, registry credentials (forge, 2026-11-14) — **COMPLETE**

**Status**: every T3 endpoint is implemented, covered by fake-backed integration tests and exercised live against the real daemon and
Docker Hub (`LiveStorageTests`, throwaway resources only). `dotnet build --no-incremental` = 0 warnings / 0 errors, `npm run build` green.

**Endpoints** (`/api/v1`; "admin" = `Capability.Write`, viewers get 403, no session 401):
`GET /updates` · `POST /updates/check` (admin) · `GET /updates/quarantine` · `POST /updates/quarantine/clear` (admin) ·
`POST /images/pull` (admin, **202** `{operationId}`) · `GET /images/staged` · `POST /images/:ref/remove` (admin) ·
`POST /images/cleanup/preview|execute` (admin) · `POST /volumes/:name/remove` (admin) · `POST /networks/:ref/remove` (admin) ·
`POST /storage/cleanup/preview|execute` (admin) · `GET|PUT /registries/credentials` (admin, the read too — as in the original) ·
`DELETE /registries/credentials/:host` (admin). `/ignored*` already existed (T2); update detection reads containers through
`ContainerService.ListRawAsync`, which applies the ignore list, so ignored targets are skipped.

**Layout** — Domain: `Registry/ImageReferences` (parse, registry host resolution, cache key, staged/rollback tags), `Updates/UpdateClassifier`
(the six-state digest machine) + records, `Storage/{ReclaimMath, StorageRules, ImageCleanupRules}` (pure decisions: reclaim arithmetic,
volume/network eligibility, image candidate selection by age/label/category/scope/retention). Application: `Registry/` (IRegistryClient,
ISecretBox), `Updates/` (UpdateService, PullService, ImageOps, QuarantineService, RegistryCredentialService, store interfaces, DTOs),
`Storage/` (ImageCleanupService, StorageCleanupService, PreviewStore), `Docker/Reclaim.cs` (DfMeasurer, ReclaimMeasurer,
ComposeOwnershipProvider). Infrastructure: `Registry/RegistryClient` (HttpClient), `Security/SecretBox` (AES-GCM), `Persistence/Repositories/UpdateStores.cs`,
adapter additions in `DockerEngineAdapter`. Api: `Endpoints/UpdateEndpoints.cs`.

**Decisions**
- **One gateway, extended**: `IDockerCommands` gained `PullImageAsync/TagImageAsync/RemoveImageAsync/RemoveVolumeAsync/RemoveNetworkAsync`;
  `IMutationGateway` wraps each (read-only → `read_only`; volume/network also re-check self-protection + managed scope; pull refuses anything
  but a `sha256:` digest). Nothing else may touch `IDockerCommands` (architecture test still green). Deletes are never forced; `RemoveVolume` never `force`.
- **Pull is by digest only** (`fromImage=repo&tag=sha256:…`), then tagged `quaymaster-staged/<hash12>:<digest12>`; the user's tag is added only when it
  does not exist locally. Free-space preflight (2.5× compressed size + max(QM_DISK_FLOOR_BYTES, 10 % of the root)) and a 1.5 s floor watchdog that
  cancels the pull (→ 507 `insufficient_disk`). Credentials come from the store (looked up by API host then by registry), sent as `X-Registry-Auth`.
- **Registry client** = port of `registry/client.ts`: HEAD with Accept for index+manifest types, 401 challenge → bearer token (cached per host|repo)
  or basic; credentials only to the registry or an allow-listed realm (built-ins + `QM_REGISTRY_TOKEN_REALMS`), never over plain http except
  loopback / `QM_REGISTRY_INSECURE`; per-host serialisation, 250 ms spacing + jitter, `Retry-After` retries (2) then host back-off; redirects never
  followed; missing digest header → GET and hash the body. Failures are cached ≤ 5 min, successes `QM_CHECK_TTL_MINUTES`; the daemon's
  `/distribution/{name}/json` is the fallback (source `engine`).
- **SecretBox is wire-compatible with the original** (HKDF-SHA256 salt `quaymaster` / info `secrets-v1`, AAD `secret_store:ciphertext:<name>`,
  `v1.<keyId>.<iv>.<tag>.<ct>` base64url) — a unit test decrypts a ciphertext produced by the Node implementation, so an existing DB's credentials keep working.
  Passwords are never returned (`GET` = host, username, updatedAt), never audited, and the DB file contains only ciphertext (integration test greps the file).
- **Previews** are in memory (10 min, one use, bound to the admin who made them; a restart invalidates them). Image cleanup: `confirm == previewId`.
  Storage cleanup: `confirm == confirmPhrase ?? previewId`, phrase = `delete N volume(s)`. Every candidate is re-verified at execute time
  (in use by ANY container incl. invisible ones, recreated volume, kind change, new tags, scope, rollback, Compose naming).
- **Reclaim** = `{estimatedBytes, freeSpaceDeltaBytes, dfDeltaBytes, reclaimedBytes, basis: df|statfs|estimate, note, caveat}` from a fresh df + statfs
  before/after, capped at what the removed items occupied (RT3-06); untagging and "nothing removed" report 0. Live: a 2 MB volume reported
  df Δ 2 000 000 / statfs Δ 1 998 848.
- **Compose ownership** for the "named by a project" / "project volume" rules comes from the resolved config of every discovered project (including
  ignored ones) via the new `IComposeService.ResolvedConfigForSafetyAsync` (raw config, never returned to a client).
- **Untagged images and expired internal tags under a managed scope** are kept unless a `labelFilter` is given (original rule) — note that the default
  harness scope `*` therefore protects dangling images; integration tests for cleanup run with an empty scope.
- Error codes as in the original: confirmation mismatch 409 `confirmation_required` (+ `details.expected`), no/expired preview 409 `conflict`,
  other admin's preview 403 `forbidden`, in-use / Compose-owned 409 `conflict` (+ `details.requires:"acknowledgeComposeProject"` for volumes),
  scope 409 `out_of_scope`, own volumes/networks 409 `self_protected`, read-only 409 `read_only`. `POST /images/pull` with out-of-scope ref = 403 `out_of_scope` (as the original).
- Preview endpoints are admin-only and allowed in read-only mode (they change nothing); `/updates/check` and quarantine clear are allowed in read-only mode too.

**Docker.DotNet.Enhanced 4.4.1 gaps**: none blocking. `JSONMessage` has no `ProgressMessage`; progress is rebuilt from `Progress.Current/Total`; pull errors
arrive as `JSONMessage.Error` in the stream (not an exception), so the adapter collects the first one and throws `registry_error`. `Progress<T>` posts
asynchronously, so a synchronous `IProgress<T>` is used to see the error before `CreateImageAsync` returns. `/distribution/*/json` is read through the raw GET client.

**Tests (T3)**: unit **101 new → 317** (reference parsing, six-state machine, quarantine, reclaim math, cleanup/storage rules, SecretBox incl. Node vector,
RegistryClient: challenge dance, realm allow-list, http/https rules, 429/back-off, throttle, error kinds) · integration **+86 → 207 non-live, 212 with the 5 live tests** (`UpdatesApiTests`,
`RegistryCredentialsApiTests`, `ImagePullApiTests`, `ImageRemovalApiTests`, `ImageCleanupApiTests`, `StorageCleanupApiTests`; `FakeDocker` now has a mutable image/volume/network world + `FakeRegistry`) ·
live **+3** (`LiveStorageTests`, `[Trait("Category","Live")]`: pull alpine by digest → staged → user's tag unmoved → untag; throwaway image untag/delete cycle with measured reclaim;
anonymous-volume preview+execute + single volume + network removal, QM_MANAGED_SCOPE pinned to the throwaway names; all cleaned up, host volume/image/network lists identical before/after).
CI without Docker: `dotnet test --filter "Category!=Live"`.

**What T4 (maintenance/rollback) must know**
- `IRollbackStore` (put/get/active/release/list) and `IStagedStore` exist; rollback tags: `ImageReferences.RollbackTag(project, service, runId)`; retention `QM_ROLLBACK_RETENTION_DAYS` = `config.RollbackRetention`.
  Cleanup already protects active rollback points (by tag and by image id) and expired ones are removed via the `expired_rollback` category.
- `IQuarantineService.AddAsync(target, digest, runId, reason)` is the hook for failed unattended runs; `UpdateService` flags `quarantined` on the status.
  `target` = `ImageReferences.CacheKey(ref)` (`docker.io/library/nginx:1.27`).
- Deploy = use `IImageOps` + gateway (`TagImageAsync` moves the user's tag); `PullService.StartAsync` is the standalone pull; maintenance should call `IImageOps.PullByDigestAsync` directly.
- The maintenance gate (`assertNotUnderMaintenance`) is still not wired into the new mutations (also not into T2's); the hook point is `MutationGateway.EnsureWritableAsync`.
- Image/volume/network removal invalidate the container cache only; T4's inventory caches (compose, df) are not invalidated by removals (df is re-measured per reclaim anyway).

**Known gaps for T4/T5**
- No periodic update check yet (T5 monitors: "update available" alerts + scheduled checks); `/updates` only checks on demand / TTL.
- `compose` projects' image refs are read from the resolved config only for `level: full` projects; limited projects protect only through their running containers' images.
- Pulls of private registries with credentials under a *different* host alias (e.g. `index.docker.io`) are not matched; use `registry-1.docker.io` for Docker Hub.
- Preview state is process-local (single instance by design).
- `DELETE /registries/credentials/:host` returns `{ok:true}` as the original; PUT echoes the host as typed but stores it lower-case.

## T4a — maintenance configuration + dry-run planner (forge, 2026-11-14) — **COMPLETE**

T4 domain layer (8847781) is unchanged. T4a adds policies, windows, schedules and the dry-run plan; the run lifecycle (T4b) and
standalone/monitors/stream (T4c) are **not** here.

**Endpoints** (`Api/Endpoints/MaintenanceEndpoints.cs`; reads = any authenticated user, writes = admin; wire format of `shared/src/maintenance.ts`)
- `GET /maintenance/policies` → `{policies:[{scopeKind,scopeKey,policy,updatedAt}]}` · `GET /maintenance/policies/effective?project&service&container` → `EffectivePolicy`
- `PUT|DELETE /maintenance/policies/:kind/:key` (kind = global|project|service|container; `:key` is URL-decoded, `shop%2Fweb`; global is always keyed `*`) → PUT `{scopeKind,scopeKey,policy}`, DELETE `{ok:true}`
- `GET /maintenance/windows` → `{windows:[…openNow,nextStart]}` · `POST` (201) · `PUT /:id` · `DELETE /:id`
- `GET /maintenance/schedules` → `{schedules:[…nextRuns[3]]}` · `POST` (201) · `PUT /:id` · `DELETE /:id`
- `POST /maintenance/plan {project, services?, applyDrift?, allowUnhealthy?}` → `RunDetail` (run fields flattened + `plan` + `steps`), stored as a `dry_run` run, status `planned`

**What was built**
- Application (`Application/Maintenance/`): `Stores.cs` (`IPolicyStore`, `IWindowStore`, `IScheduleStore`, `IRunStore` + records), `Dtos.cs`, `Validators.cs`
  (FluentValidation, zod-style messages, strict bodies), `PolicyService` (also `IPolicyResolver`, `IProbeVerifier` abstraction), `WindowService`,
  `ScheduleService` (in `WindowAndScheduleServices.cs`), `PlannerService : IPlanService` (`PlanService.cs`; the class is named `PlannerService` because
  `Domain.Maintenance.PlanService` is the plan's per-service record).
- `IComposeService` gained `PlanContextAsync` (summary + validation + unredacted resolved config + live containers) and `HashesAndDriftAsync`
  (`compose config --hash` + drift decision) — read-only, no new mutation path.
- Infrastructure: `Persistence/Repositories/MaintenanceStores.cs` (SQL against the Baseline tables `policies`, `windows`, `schedules`, `runs`, `run_steps`;
  stored JSON bodies are the original's), `HttpProbeVerifier`; DI in `DockerDependencyInjection.cs`. Api: wiring only (`Program.cs`: `MaintenanceEndpoints.Map(api)`;
  `StrictJsonBody.ParseAsync` made public for the raw policy body).

**Decisions**
- **Read-only mode** does not block policy/window/schedule CRUD or the dry run at the endpoint (as the original: only run create/approve/restore check it). It
  does show up in the plan: the guard's read-only refusal becomes `blocked:{code:"refused"}` (original `empty('refused', …)`), nothing is mutated. All writes
  are audited through `IMutationGateway.AuditedAsync` (`maintenance.policy.put|delete`, `maintenance.window.create|update|delete`, `maintenance.schedule.*`,
  `maintenance.dry_run`). Policy for a name outside `QM_MANAGED_SCOPE` → 409 `out_of_scope`.
- **Probe**: a policy with `health.probe` is only saved after `IProbeVerifier` got the expected status (5 s, no redirects) — 400 `validation` otherwise.
- **Unique names**: windows and schedules have UNIQUE(name) in the schema; the services answer 409 `conflict` instead of a 500. Deleting a window used by a schedule → 409.
- **`maxParallel`** accepts only `1` (domain rule from the original). `requireApproval` is OR-combined only through `critical`/explicit flags (no implicit force).
- **Plan blocked codes** (kept in `plan.blocked`, HTTP stays 200): `refused`, `limited_capability`, `validation`, `cycle`, `out_of_scope`/`self_protected`/`read_only`
  (project refs), `preflight`, `blocked_drift`, `nothing_to_do`. Unknown project is 404 (thrown by discovery), a bad body 400.
- Plan semantics are the original `planner.ts` 1:1: candidates only for `update_available|update_staged` with a remote digest; `ignore`, `build:`, self container,
  scope/protected refusals, up-to-date, never-created are `excluded` with a reason; namespace/volume dependents join with `role:"dependent"`; ordering via
  `ServiceOrdering` (deps first, `updateOrder`, name); disk check fail-closed (2.5 × compressed + max(floor, 10 %)); shared-tag containers (incl. ones hidden by
  the visible scope) are listed/warned and fenced by the managed scope; `planHash` from `PlanHasher` (volatile data excluded).
- The plan uses `IUpdateService.StatusesAsync(fresh: true)` (one throttled registry pass for all images in use) and matches `ref` + running image id.

**Tests (T4a)**: unit **+10 → 397** (`MaintenanceConfigServiceTests`: policy merge/sources/%2F/scope/probe, window openNow/nextStart across the Berlin spring-forward,
schedule next runs across the gap (02:30 → once at 03:00 CEST), validation rules; `PlannerInputsTests`: topo order of a resolved Compose config incl. namespace
edge + cycle, drift detection, plan-hash staleness/diff — on top of the 40 domain tests from 8847781) · integration **+20 → 227 non-live** (`MaintenanceConfigApiTests`: CRUD
contracts for policies/windows/schedules incl. auth (viewer 403, anon 401), 201s, 400/404/409 codes, audit entry; plan shape with all steps, stored run, hash stability,
digest change, drift block/`applyDrift`, ignore policy, services filter, dependency order, unknown project, out-of-scope, read-only, low disk) · live **+1 → 233 total**
(`LiveMaintenanceTests`, `[Trait("Category","Live")]`: brings up `qmtest-upd`, publishes app v2, effective policy merge, real out_of_scope, dry-run plan with the real
`compose config --hash`, digest from the fixture registry, healthcheck-kind, dependents, identical hash on re-plan, container ids/start times unchanged, torn down in `finally`).
Totals: **397 unit + 233 integration, 0 failed**; `dotnet build --no-incremental` 0 warnings, `npm run build` green, `web/` untouched.

**What T4b must know (hooks)**
- `IPlanService.PlanProjectAsync(PlanRequest, ct)` is the re-plan at the executor's preflight: compare `Plan.PlanHash` with the approved one
  (`PlanHasher.Compute`, `PlanHasher.DescribeDiff` for the `approval_stale` reason). `PlanRequest` already carries `ScheduleMode` and `OnlyModes` (scheduled runs: `["auto"]`),
  `AllowUnhealthy`, `ApplyDrift`, `Fresh`. `IPlanService.DryRunAsync` shows how a run row + audit entry are written.
- `IRunStore` (insert + get-with-steps) is deliberately minimal: T4b adds status transitions (`planned→approved→running→…`), step appends, list/filter, `recover()`;
  the `runs`/`run_steps` DDL and the row classes (`RunSqlRow`, `RunStepSqlRow`) are in `MaintenanceStores.cs`. `RunDetailDto.From(RunDetail)` is the wire mapper.
- `IPolicyResolver.ResolveAsync(project, service, container)` for per-service policy at execution time; `PlanService.Unattended.Blockers` already encode the RT-23 truth table.
- `IScheduleStore.SetCursorAsync` + `schedule_occurrences` (deleted with the schedule) are ready for the scheduler tick; `TimeMath.NextOccurrences(cron, tz, afterMs, n)`
  and `TimeMath.WindowAt/NextWindowStart` are the DST-safe primitives (`WindowRecord.Def` converts a stored window).
- `IComposeService.PlanContextAsync` / `HashesAndDriftAsync` are read-only; the executor still needs its own up/pull/tag steps through `IMutationGateway`/`IImageOps`.
  The maintenance gate (`assertNotUnderMaintenance`) is still not wired into T2/T3 mutations (hook: `MutationGateway.EnsureWritableAsync`).

**Known limits**: no `GET /maintenance/runs*` yet (T4b); a dry run stores its plan but exposes it only in the POST response; plan generation calls `StatusesAsync(fresh:true)`
for the whole host (acceptable for a manual dry run, T4b scheduled planning should pass `Fresh=false` where the cache is enough); policy keys are decoded with
`Uri.UnescapeDataString` (a literal `%` in a container name key must be sent as `%25`).

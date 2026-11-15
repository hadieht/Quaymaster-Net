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
- Unauthenticated requests to _unknown_ `/api/*` routes get 401 (as in the original, auth runs first); authenticated ones get 404.
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
- Pulls of private registries with credentials under a _different_ host alias (e.g. `index.docker.io`) are not matched; use `registry-1.docker.io` for Docker Hub.
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

---

## T4b — run lifecycle (executor, approval, rollback, restore)

**Built** (Application/Maintenance: `RunService`, `RunExecutor` (+`.Deploy`), `HealthGate`, `RunEnvironment`, `RunRecoveryService`; Domain `RunRules`; Infrastructure `MaintenanceStores.cs` extended with the run store, steps, rollback points, quarantine; Api `MaintenanceEndpoints.cs`).

**Endpoints**: `POST /maintenance/runs`, `GET /maintenance/runs[?status&target&limit]`, `GET /maintenance/runs/:id`, `POST /maintenance/runs/:id/approve {planHash}`,
`POST /maintenance/runs/:id/reject`, `POST /maintenance/runs/:id/restore {confirm, services?}` → 202 `{operationId}`.

**Behaviour**: approval bound to the plan hash (`approval_stale` 409 when the plan changed, a fresh request is created); expiry via `QM_APPROVAL_EXPIRY_HOURS` (default 72), applied on list/get and `IRunService.ExpireApprovalsAsync`;
unattended only when policy auto + schedule auto + no approval + not critical; 10 persisted steps (preflight, validate, check, recovery point, pull by digest, impact, deploy one service at a time deps-first with image-ID assertion, health, confirm/rollback, record);
auto-rollback OFF for writable persistent mounts (service left down, `service.left_down`); startup recovery marks in-flight runs `interrupted` with exact recovery commands (never resumed); overlapping runs serialised/declined (`run.conflict`).

**Event types for T5**: `maintenance.upcoming` (T4c scheduler emits), `maintenance.awaiting_approval`, `run.succeeded`, `run.failed`, `run.rolled_back`, `run.partial`, `run.skipped`, `run.interrupted`, `run.conflict`, `service.left_down`, `updates.available`, `cleanup.report` (the last three plus `maintenance.upcoming` are defined for T4c/T5 and not yet emitted).

**Tests**: 479 unit, 244 integration (11 new in `RunLifecycleApiTests`), all green; `dotnet build --no-incremental` 0 warnings; `npm run build` green.
Failure scenarios: create→approve→execute with persisted steps and recovery point; start-now confirmation + read-only refusal; pull failure → abort with zero impact; restarting service → blocked at preflight;
health failure → rollback, recovery point re-tagged, old image running; tag moved after approval → approval_stale; crash mid-run → interrupted + commands, not resumed; overlapping runs serialised/declined;
critical target with auto policy still waits (non-critical runs unattended); restore after confirm; approval expiry and reject.

**Hooks for T4c**: `IRunService.CreateAsync(request, actor, ScheduledRun)` for the scheduler; `ExpireApprovalsAsync` for periodic expiry; `RunHooks.OnStep` is a test-only crash/timing seam; notification wiring should consume the event types above.
**Known limits**: HTTP health probe and real `RunEnvironment.UpAsync` have no unit coverage (T4c live fixture run exercises them).

---

## T4c — standalone update, maintenance monitors, maintenance streams, live verification (forge, 2026-11-14) — **COMPLETE**

**Endpoints (new)**: `POST /maintenance/standalone {container, confirm}` → **202**, body = the run (`RunDetail`, flattened) **plus `runId`** (admin; 400 validation, 401/403,
404 unknown container, 409 `confirmation_required` (+`details.expected`) / `read_only` / `unsupported` (Compose-managed, `container:` network mode, `--rm`) / `conflict` (nothing to update,
policy `ignore`, another run active) / `out_of_scope`). `GET /stream/maintenance` (SSE: `snapshot` once per recent run, oldest first, then `run` on every status/step change, polled every 2 s;
final `end {reason}`) and `GET /stream/maintenance/runs/:id` (live output of a run: `output {text}`, `status {status,error}`, `end`; a run id **is** its operation id, so
`/stream/operations/:id` works too; 404 for an unknown run, a finished run whose operation record is gone answers with its final `status`).

**Gateway surface completed** (`IMutationGateway`, the only user of `IDockerCommands`; the T2 in-flight branch had nothing, so it was re-implemented): `CreateCloneAsync`, `ConnectCloneNetworkAsync`,
`RenameContainerAsync`, `SetRestartPolicyAsync`, `ExecuteOnCloneAsync`. They all authorise on the **original** container's ref (never on the temporary clone name), enforce read-only / scope /
self-protection / maintenance gate, require full ids. Adapter: `DockerHttp.PostJsonAsync` (create / network connect / rename / update; Engine JSON passes through unchanged). "Probe" =
`HealthGate.WaitAsync` on the clone id (healthcheck → `healthy`, else stability, + the policy's HTTP probe).

**Standalone** (`Application/Maintenance/StandaloneExecutor`, `Domain/Maintenance/CloneSpecBuilder`): recovery point (old image id tagged `quaymaster-rollback/standalone…`) → pull by digest →
tag moves → clone created stopped (only env/cmd/labels… that differ from the old image are carried; every mount re-attached by volume name; first user network at create, the rest connected
before start) → old container stopped, restart policy → `no`, renamed `<name>-qm-old-<epoch s>`, clone renamed → start → health gate. Any failure: clone stopped/renamed aside/removed, old renamed
back, policy restored, started, tag moved back (`rolled_back` on health failure, else `failed`; undo failure → `guidance` + `service.left_down`). Same run store, steps, audit and notification
events as compose runs (`run.succeeded|rolled_back|failed`). **No `/restore` for standalone runs** (409 "restored by hand": the old container is kept; the run's `reason` has the exact commands).

**Monitors** (`Application/Monitoring/MaintenanceMonitor`, `IMonitorEvaluator.TickAsync`, background task, 60 s): per tick — approvals past `QM_APPROVAL_EXPIRY_HOURS` → `run.skipped`; **scheduler**
(cron cursor moves _before_ firing = at-most-once; `Domain/Maintenance/ScheduleTick` decides Fire/SkipMissed(>5 min, unless the latest one of an update job with an open window)/SkipWindow;
`update` → `IRunService.CreateAsync(..., ScheduledRun)`, `image_check` → `updates.available`, `cleanup_report` → `cleanup.report`; `image_pull` is consumed but **not executed**);
`maintenance.upcoming` (within `upcomingMaintenanceMinutes`); `updates.available` (daily, marker in `kv` `monitor.updates.lastCheck`); `host.disk_low` when the Docker root is below `QM_DISK_FLOOR_BYTES`.
Container died / unhealthy / OOM / restart loop / CPU / memory stay in `Monitors` (event driven; restart-loop counting now `Domain/Maintenance/RestartLoop`). **De-dup/cool-down lives in the
outbound abstraction** (`INotificationSink`: in-memory per `DedupeKey` + `CooldownMs`; the monitor deliberately re-emits every tick). `GET|PUT /monitors/settings` drive the loop (`enabled=false` stops announcements, schedules still fire).

### Event contract for T5 (`NotificationEvent(Type, Severity, Title, Message, DedupeKey, CooldownMs?, Data?)`)

| Type                                                                                                                                                 | Severity                                   | DedupeKey                                      | CooldownMs | Data                                                              |
| ---------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------ | ---------------------------------------------- | ---------- | ----------------------------------------------------------------- |
| `maintenance.upcoming`                                                                                                                               | info                                       | `upcoming:<scheduleId>:<occurrenceMs>`         | 86 400 000 | `schedule, scheduleId, mode, target, at(ISO)`                     |
| `maintenance.awaiting_approval`                                                                                                                      | info                                       | `approval:<runId>`                             | –          | `run, planHash`                                                   |
| `run.succeeded` / `run.rolled_back` / `run.partial` / `run.skipped` / `run.failed` (also blocked/interrupted-by-executor)                            | info / warning / warning / info / critical | `run:<runId>`                                  | –          | `run, target, status` (`guidance` appended to message on failure) |
| `run.skipped` (approval expired)                                                                                                                     | info                                       | `expired:<runId>`                              | –          | `run, target, status`                                             |
| `run.skipped` (scheduled update, window closed)                                                                                                      | info                                       | `skipped:<scheduleId>:<occurrenceMs>`          | –          | `schedule, reason:"window_closed"`                                |
| `run.failed` (scheduled update blocked)                                                                                                              | warning                                    | `blocked:<project>:<code>`                     | 86 400 000 | –                                                                 |
| `run.interrupted` / `service.left_down` (startup recovery, auto-rollback off, failed undo)                                                           | warning / critical                         | see T4b                                        | –          | `run, target, services[]`                                         |
| `run.conflict`                                                                                                                                       | warning                                    | see T4b                                        | –          | –                                                                 |
| `updates.available`                                                                                                                                  | info                                       | `updates:<sorted ref@digest list, ≤300 chars>` | 86 400 000 | `count, images[]`                                                 |
| `cleanup.report`                                                                                                                                     | info                                       | `cleanup:<yyyy-MM-dd>`                         | –          | `candidates, totalBytes, protected`                               |
| `host.disk_low`                                                                                                                                      | warning/critical                           | `disk-low`                                     | 21 600 000 | `freeBytes, freePercent` / `floorBytes`                           |
| `container.died` / `container.unhealthy` / `container.restart_loop` / `host.cpu_high` / `host.memory_high`                                           | per T2                                     | per T2                                         | per T2     | `container, …`                                                    |
| Wire source of truth: `INotificationSink.Notify`; T5 replaces `AuditNotificationSink` (default: audit `monitor.alert` with the in-memory cool-down). |

**Tests**: unit **+23 → 502** (`T4cTests.cs`: clone spec; the full standalone sequence order CreateClone→ConnectCloneNetwork→stop→SetRestartPolicy→Rename×2→ExecuteOnClone(start)→Probe; rollback sequence on
failed health; pull failure touches nothing; prepare refusals; scheduler decisions + window gating; restart-loop window; monitor events + dedupe keys; sink cool-down; approval expiry via `RunService`) ·
integration **+7 → 251 non-live** (`StandaloneAndStreamTests`: standalone contract/auth/validation/errors/read-only, full accepted run on the fake world, `/stream/maintenance` connect → snapshot →
`run` → heartbeat → `end` on logout, run output stream replay, monitor loop with a capturing sink: `updates.available`, `maintenance.awaiting_approval`, `run.skipped` on expiry) · **live +2** (`LiveMaintenanceRunTests`,
`[Trait("Category","Live")]`, `[Collection("Live")]` shared with `LiveMaintenanceTests` so fixture-registry users never run in parallel).

**Live evidence (real daemon, `qmtest-upd`, scope `qmtest-*`)**: dry-run plan → create (awaiting_approval, same plan hash) → approve → run `completed` with all steps `succeeded`
(preflight, validate, check, recovery_point, pull, impact, deploy [real `docker compose up --no-deps --pull never`], health, dependents, record) → `app` runs a **different image id** (`sha256:a836f68e…`) → restore (confirm
= project) → operation `succeeded` → `app` runs the **original image id** again; the container ids of every other stack on the host are identical before/after; fixture removed. Standalone: `qmtest-solo`
(restart `unless-stopped`, named volume, env) updated by config clone → new image, restart policy/env/volume carried, `qmtest-solo-qm-old-<ts>` kept stopped with restart `no` on the old image; cleaned up.

**Gaps for T6**: `image_pull` schedules are accepted but not executed; no standalone restore endpoint (guidance only, old `-qm-old-*` containers are not auto-pruned after the rollback retention);
`GET /stream/maintenance` is polled (2 s) not pushed; monitor cool-down state is process-local (lost on restart — a restart re-announces `updates.available` at most once, the daily marker is in `kv`);
real channel delivery (T5) still missing; `docs/API.md` should document `POST /maintenance/standalone` (response carries `runId` in addition to the flat run) and the two new streams; the web UI
does not use `/stream/maintenance` yet; `Fresh` plan generation for scheduled runs still refreshes the whole host (see T4a limits).

## T5 — notifications: 7 channels, de-dupe, retries, outbound dispatcher (forge, 2026-11-14) — **COMPLETE**

**Endpoints** (`/api/v1`): `GET /notifications/types` · `GET|POST /notifications/channels` · `GET|PATCH|DELETE /notifications/channels/:id` · `POST /notifications/channels/:id/test` · `GET /notifications/deliveries`

**Channel kinds & wire options**

| kind     | required config      | optional config            | masked in API        |
| -------- | -------------------- | -------------------------- | -------------------- |
| webhook  | url                  | secret, headers            | url, secret, headers |
| ntfy     | serverUrl, topic     | token                      | serverUrl, token     |
| gotify   | serverUrl, appToken  | —                          | serverUrl, appToken  |
| discord  | url                  | —                          | url                  |
| slack    | url                  | —                          | url                  |
| telegram | botToken, chatId     | apiBase                    | botToken             |
| smtp     | host, port, from, to | secure, username, password | password             |

Per-channel: `enabled`, `events[]` (event types this channel receives; `[*]` = all), `minSeverity` (info|warning|critical).

**Behaviour (port of original outbound.ts, channel.ts, service.ts)**

- Outbound policy: http/https only, no userinfo, unspecified/link-local/multicast/broadcast IPs refused (cloud metadata), loopback/RFC1918 allowed (home self-hosted), max 3 redirects to **same host**, 307/308 keep method+body, others become GET; TLS required unless plain `http://` used; 5 s timeout, 64 KiB response cap.
- Webhook HMAC: `X-Quaymaster-Signature: sha256=<HMAC-SHA256(body)>`, timestamp header `X-Quaymaster-Timestamp`.
- Ntfy priority 3/4/5, tags `information_source|warning|rotating_light,<event.type>`.
- Gotify priority 3/5/8, `X-Gotify-Key`.
- Discord/Slack/Telegram/SMTP templates identical to original (byte-for-byte for JSON payloads).
- De-dupe: persisted in `notification_dedupe` keyed by `channelId|dedupeKey` (default `type:title`), cooldown 15 min default, 5 min for critical.
- Delivery log in `notification_deliveries` with statuses pending|sent|failed|suppressed|dead.
- Retries: backoff 30s, 2m, 10m, 30m, 2h (±20 % jitter), max 5 attempts.
- Dead deliveries raise ONE critical `app.error` to **other** channels (never back to the failing one, never for `app.error` or `test` events — no loops).
- Secrets encrypted at rest via T3's `ISecretBox` (AES-GCM under `QM_SECRET_KEY`), never returned (masked like registry credentials).
- Channel CRUD audited; outbound failures audited as `app.error`.
- Monitors (T4) now emit through `INotificationSink` → real deliveries (replacing `AuditNotificationSink`).

**Tests (all green)**

- Unit: 146 tests — channel config validation (all 7 kinds, boundaries, SSRF/URL policy table), rendering snapshot vs. original channels/*.ts, outbound policy (IPv4/IPv6 rules, redirect logic), delivery rules (cooldown, backoff, jitter).
- Integration (faked transport): 17 tests — full pipeline event→channels→render→send→delivery log; retries on 500; HMAC verification; test endpoint; cooldown suppression; dead-letter self-monitor; delivery query filters; audit masking.
- Live (`[Trait("Category","Live")]`): 18 tests — real HTTP/SMTP on 127.0.0.1 via `RawHttpReceiver` + `FakeSmtpServer`. Wire-format assertions: webhook headers & HMAC byte-for-byte, ntfy headers, gotify/telegram/discord/slack JSON, redirect 307/308 vs 301/302/303 semantics, host policy enforcement, connection failures & timeouts never leak URLs, response cap, SMTP dialogue + AUTH, blocked link-local hosts.

**Verification**

```
dotnet build --no-incremental          0 warnings / 0 errors
dotnet test                            648 unit + 288 integration + 18 live = 954 passed
npm run build                          green
```

**Decisions & known gaps for T6**

- The raw `INotificationSink` replacement means monitors now use the real dispatcher; `AuditNotificationSink` is retained (unit tests depend on it) but no longer registered in DI.
- Image pull schedules (`image_pull` cron) are accepted but not executed (gap from T4c).
- No standalone restore endpoint (`-qm-old-*` containers kept after rollback; no prune job yet).
- `GET /stream/maintenance` still polled at 2 s; monitor cool-down is process-local (lost on restart, at most one re-announce of `updates.available` per restart; daily marker in `kv`).
- `docs/API.md` does not yet document the new notification endpoints or streams.
- Web UI does not yet use `/stream/maintenance` (gap from T4c).

## T6a — run-serialisation guard race (forge, 2026-11-15) — **COMPLETE**

**The race.** A run holds the `maintenance` operation lock (`OperationManager._locks`) for the whole `Run` delegate. The terminal status (`completed`/`failed`/`rolled_back`…)
is written by `RecordAsync`/the standalone executor *inside* that delegate, which then still did the update-status refresh, the audit write, the log line and the
notification dispatch (T5) before returning — only then was the lock released. A client that saw `completed` and immediately approved the next run got 409
("Another operation … is already running on maintenance") although no run was executing. Deterministic in the isolated
`Overlapping_runs_…` test (4/4 before the fix).

**The fix.** `IOperationContext.ReleaseLock()` (idempotent, only releases if this operation still holds the key); `OperationManager.RunAsync` uses it for its own release.
`RunExecutor.RecordAsync` and `StandaloneExecutor` call `ctx.ReleaseLock()` immediately *before* `runs.FinishAsync`. Overlap safety is unchanged: until the terminal status is
written the row is `running`, and `RunService.EnsureNoRunActiveAsync` (store-backed `RunningAsync`) refuses the second start; after it, nothing blocks. No wire-format change.
The unit fake (`FakeRunStore.OtherRunExecuting`) now simulates the foreign running run for `Overlapping_runs_are_serialised_a_second_start_now…`, which was still mocking the
old lock-based check (stale since the 567a5a3 checkpoint).

**Evidence.** Isolated filter `Overlapping_runs` 3/3 passed in a row; full `dotnet test -c Release` after the fix: integration 289 passed / 0 failed / 0 skipped (live tests
included, 5m17s); unit 648/648 after the fake fix; `dotnet build` 0 warnings / 0 errors; `npm run build` green.

## T6b — Playwright e2e green + screenshots (forge, 2026-11-15) — **COMPLETE**

`e2e/run.sh` adaptation verified (builds the API, runs the built dll directly so the PID we kill is the server, 127.0.0.1 only, qmtest-* scope, fixtures torn down in the trap).
Specs (`app.spec.ts`, `screenshots.spec.ts`) are **unchanged**. Two contract bugs found and fixed in the API:

1. **Client abort cancelled state-changing runs.** The spec clicks "Prepare run" and immediately `goto`s another page; the aborted request cancelled `POST /maintenance/runs`
   (`HttpContext.RequestAborted`), so no run was created (Node ran the handler to completion). Mutating run endpoints (create / approve / reject / standalone start / restore)
   now use `CancellationToken.None`.
2. **Five UI endpoints from the original `routes/webapi.ts` were missing** (404 "Route not found", visible on Settings → General in the first screenshot run, and Security/cron preview/dashboard charts):
   `GET /system/settings`, `GET /auth/sessions`, `DELETE /auth/sessions/{id}`, `GET /maintenance/cron/preview`, `GET /stats/overview`
   (new `WebApiEndpoints.cs`, `ISessionRepository.ListForUserAsync`, `IStatsStore.SinceAsync`). Route-table diff original vs .NET is now empty.
   Also: static files run before routing (SPA fallback) — Program.cs edit that was left uncommitted by T6a.

Screenshots: `npm run screenshots` runs from a neutral copy of the fixtures (`/srv/qmtest/test-fixtures`, same as the original), so no host paths appear (Settings shows `/srv/qmtest/test-fixtures/stacks`).
vite.config.ts needed no change (default proxy target `http://127.0.0.1:8787` = .NET default port; `QM_DEV_API` overrides). README has the dev workflow.

Gap: no dedicated integration tests for the five new endpoints (verified live with curl + the UI screenshots).

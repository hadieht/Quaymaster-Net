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

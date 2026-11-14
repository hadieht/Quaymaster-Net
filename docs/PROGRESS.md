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

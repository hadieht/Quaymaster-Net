# Quaymaster (.NET)

Self-hosted Docker dashboard — the same product as [quaymaster](https://github.com/hadieht/quaymaster),
rebuilt on a **modern .NET 10 backend** with an unchanged React frontend.

> **Status: in progress.** The API contract is a 1:1 port of the original
> (`/api/v1`, same JSON shapes, same session-cookie + CSRF auth, same SSE
> streams), so the React frontend runs unmodified. See
> [docs/PROGRESS.md](docs/PROGRESS.md) for phase status.

## Why a .NET rebuild

- .NET 10 / C# with strict warnings-as-errors and enforced code style
- Clean Architecture layering: Domain → Application → Infrastructure → Api
- Built-in rate limiting, health checks, OpenAPI, structured logging
- EF Core + SQLite with real migrations
- Docker SDK for the Engine API; `docker compose` CLI for Compose operations

## Repository layout

| Path                            | Contents                                                 |
| ------------------------------- | -------------------------------------------------------- |
| `src/Quaymaster.Domain`         | Pure domain model (no framework refs)                    |
| `src/Quaymaster.Application`    | Use cases, DTOs, validation, abstractions                |
| `src/Quaymaster.Infrastructure` | Docker, EF Core/SQLite, registry, secrets, notifications |
| `src/Quaymaster.Api`            | ASP.NET Core minimal API: endpoints, middleware, SSE     |
| `tests/`                        | xUnit unit + integration (WebApplicationFactory)         |
| `web/`                          | React frontend (copied from quaymaster, unmodified)      |
| `shared/`                       | Shared API types (copied from quaymaster)                |
| `e2e/`                          | Playwright end-to-end suite (adapted to the .NET server) |

## Quick start (development)

The .NET server serves the built SPA itself from `QM_WEB_DIR` (the directory containing `index.html`; unset = API only).
Two workflows:

**A. Hot-reloading UI (two processes)**

```sh
npm install
# terminal 1 — API on 127.0.0.1:8787 (default QM_PORT), data in ./.local/dev-data
QM_DATA_DIR=.local/dev-data QM_SECRET_KEY="$(openssl rand -base64 32)" \
  dotnet run --project src/Quaymaster.Api --no-launch-profile
# terminal 2 — Vite on http://127.0.0.1:5173, proxies /api to the API
npm run dev -w web
```

The proxy target is `http://127.0.0.1:8787`; if the API listens elsewhere set `QM_DEV_API=http://127.0.0.1:<port>`
for the Vite process. The proxy rewrites `Origin` to the target so the server's origin check on mutating requests passes.
Open the Vite URL (5173), not the API port.

**B. Production-like (single process, built SPA)**

```sh
npm run build                      # shared + web -> web/dist
QM_WEB_DIR="$PWD/web/dist" QM_DATA_DIR=.local/dev-data QM_SECRET_KEY="$(openssl rand -base64 32)" \
  dotnet run --project src/Quaymaster.Api --no-launch-profile   # http://127.0.0.1:8787
```

Or everything in Docker: `docker compose up --build`.

First run: the server prints a one-time setup token (or `<data>/setup-token`);
create the first admin with it. There is no default password.

### Tests

```sh
dotnet test                 # unit + integration (Category=Live tests need the local Docker daemon)
npm run build && npm run e2e          # Playwright against the .NET server and test-fixtures/ (qmtest-* only)
npm run screenshots                   # regenerates docs/screenshots/*.png from a neutral fixture copy (/srv/qmtest/test-fixtures)
```

`e2e/run.sh` builds the API, starts the fixture stacks, runs the server on `127.0.0.1:${E2E_PORT:-8791}` with
`QM_MANAGED_SCOPE=QM_VISIBLE_SCOPE=qmtest-*`, runs Playwright, and tears everything down on exit. Prerequisites:
`npm run build`, Docker, the local `alpine` image, `npx playwright install chromium`.
Check for leftovers with `docker ps -a --filter label=quaymaster.test=true` (must be empty).

## Security

Read [SECURITY.md](SECURITY.md) and the original
[security model](https://github.com/hadieht/quaymaster/blob/main/docs/SECURITY-MODEL.md)
before deploying. Docker socket access is root-equivalent on the host.
Do not expose Quaymaster to the internet.

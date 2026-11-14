# Quaymaster (.NET)

Self-hosted Docker dashboard — the same product as [quaymaster](https://github.com/hadiieht/quaymaster),
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

| Path | Contents |
| --- | --- |
| `src/Quaymaster.Domain` | Pure domain model (no framework refs) |
| `src/Quaymaster.Application` | Use cases, DTOs, validation, abstractions |
| `src/Quaymaster.Infrastructure` | Docker, EF Core/SQLite, registry, secrets, notifications |
| `src/Quaymaster.Api` | ASP.NET Core minimal API: endpoints, middleware, SSE |
| `tests/` | xUnit unit + integration (WebApplicationFactory) |
| `web/` | React frontend (copied from quaymaster, unmodified) |
| `shared/` | Shared API types (copied from quaymaster) |
| `e2e/` | Playwright end-to-end suite (adapted to the .NET server) |

## Quick start (development)

```sh
# backend
dotnet run --project src/Quaymaster.Api
# frontend (dev server, proxies /api to the backend)
npm install && npm run dev -w web
# or everything in Docker:
docker compose up --build
```

First run: the server prints a one-time setup token (or `<data>/setup-token`);
create the first admin with it. There is no default password.

## Security

Read [SECURITY.md](SECURITY.md) and the original
[security model](https://github.com/hadieht/quaymaster/blob/main/docs/SECURITY-MODEL.md)
before deploying. Docker socket access is root-equivalent on the host.
Do not expose Quaymaster to the internet.

# Quaymaster.Infrastructure

Adapters and external concerns: Docker Engine client, Compose CLI,
EF Core + SQLite persistence, registry client, secret store (AES-GCM),
outbound notification transports, audit writer. References Application (and
through it Domain).

Conventions:

- Every external system gets one folder with a single public entry point
  (IDockerEngine, IComposeRunner, AppDbContext, ...).
- No ASP.NET-specific types (no HttpRequest, no middleware).
- All Docker mutations funnel through one gateway service that enforces
  read-only mode, managed scope, self-protection and audit — same shape as
  the original implementation. Never add a route that accepts raw command
  lines.

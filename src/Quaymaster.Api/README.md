# Quaymaster.Api

HTTP surface only: middleware pipeline, endpoint groups, DI composition. No business rules (those live in
Application) and no persistence code (Infrastructure). References Infrastructure.

Conventions:

- `Program.cs` documents the middleware order; security-relevant middleware lives in `Http/`.
- One static `Map(RouteGroupBuilder)` class per endpoint group in `Endpoints/`; bodies via `StrictJsonBody.ReadAsync`;
  failures by throwing `AppException` (rendered as the standard error envelope by `ExceptionHandlingMiddleware`).
- Everything under `/api/` requires a session unless listed in `ApiGuardMiddleware.PublicPaths`.
- Configuration comes from `QM_*` environment variables (`ApiConfiguration`), validated at startup.
- Operator CLI: `dotnet run --project src/Quaymaster.Api --no-launch-profile -- gen-secret | hash-password [pw]`.

# Quaymaster.Application

Use cases and the API contract: application services, request/response DTOs,
validation rules, and the abstractions (interfaces) that Infrastructure
implements. References Domain only.

Conventions:

- Each feature folder contains its own service + DTOs + validators.
- Services depend on abstractions (interfaces defined here), never on
  concrete infrastructure types. Dependency inversion: Infrastructure
  implements what Application declares.
- DTOs are the only things that cross into the API layer. Domain entities
  do not leak outside this layer.
- The HTTP error envelope ({ "error": { "code", "message", "details?" } })
  and all error codes live here as a single source of truth.

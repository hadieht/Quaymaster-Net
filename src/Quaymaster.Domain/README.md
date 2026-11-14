# Quaymaster.Domain

Pure domain model: entities, value objects, domain events, domain rules.
No framework references (no ASP.NET, no EF Core, no Docker client). This
project must not reference any other project.

Conventions:
- One folder per bounded context (Containers, Compose, Maintenance, ...).
- Entities are plain C# types; behaviour over data (methods on the entity,
  not service-side mutation where possible).
- Value objects are record types, immutable, with validation in the
  constructor.

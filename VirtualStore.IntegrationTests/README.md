# VirtualStore.IntegrationTests — TESTING-README

## What lives here

`[Trait("Category","Integration")]` API tests backed by a real MongoDB
Testcontainer (`mongo:7.0`) plus `WebApplicationFactory<Program>`:

- `GET /health` → 200
- `GET /api/products` → 200 with an (empty-ok) `PagedResult` shape (`items`, `totalCount`)
- `POST /api/auth/login` with unknown credentials → 401 `ProblemDetails`
  (`title: "Unauthorized"`, `status: 401`, `traceId`)

## Prerequisites

- .NET 10 SDK
- Docker daemon running (Testcontainers needs it to start `mongo:7.0`)

## Run

```powershell
# Unit gate only (no Docker needed) — from the repo root:
dotnet test VirtualStore.slnx -c Release --filter Category!=Integration

# Full suite with coverage:
dotnet test VirtualStore.slnx -c Release --collect:"XPlat Code Coverage"

# Integration only (requires Docker):
dotnet test VirtualStore.IntegrationTests -c Release
```

## Skip behavior (Docker unavailable)

xUnit v2 has no runtime dynamic skip (`SkipException.ForSkip` only works on
v3+, where the runner maps it to Skipped — on v2 it surfaces as Failed), so
readiness is evaluated at **discovery time** by `RequiresDockerFactAttribute`
(a `FactAttribute` subclass): when `DockerAvailability.IsAvailable` is false
(`docker info` fails / no daemon / no CLI), it sets `FactAttribute.Skip`, and
the runner reports every integration test as **Skipped**, not Failed.

`MongoDbFixture` additionally swallows container-start failures so a flaky
daemon at runtime cannot break collection; `VIRTUALSTORE_DOCKER_AVAILABLE=1|0`
forces the probe result (useful in constrained CI containers).

## CI

`.github/workflows/ci.yml` provides a `mongo:7.0` service (host port 27017)
for jobs that want a pre-provisioned MongoDB, runs the unit gate
(`--filter Category!=Integration`), then runs the full suite with
`--collect:"XPlat Code Coverage"`. Testcontainers-based integration tests
use the Docker service on the GitHub runner; where Docker is unavailable
they skip gracefully as described above.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| All integration tests Skipped | Docker daemon not running — start Docker Desktop / dockerd |
| `GET /health` 503 | App connected to the wrong Mongo — check the factory overrides `MongoDbSettings:ConnectionString` |
| Login test returns 400, not 401 | Request failed FluentValidation (bad email shape) — keep a well-formed email with a wrong password |
| Port 27017 conflict | Stop the local `mongo:7.0` service or let Testcontainers pick a random host port (default) |

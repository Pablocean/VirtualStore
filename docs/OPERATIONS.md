# OPERATIONS.md — run, configure, observe VirtualStore

## Environment keys

All settings bind from `appsettings.json`, overridden by env/`.env` (`__` separator, loaded by `dotenv.net`). Every secret ships **empty** — the app boots but auth/email/Stripe fail until configured.

| Key | Default | Required | Notes |
|---|---|---|---|
| `MongoDbSettings__ConnectionString` | `""` | ✅ | `mongodb://localhost:27017` dev; `mongodb://mongo:27017` in compose |
| `MongoDbSettings__DatabaseName` | `VirtualStoreDb` | — | |
| `JwtSettings__Secret` | `""` | ✅ | min 32 chars |
| `JwtSettings__Issuer` / `__Audience` | `VirtualStoreAPI` / `VirtualStoreClient` | — | must match token validator |
| `JwtSettings__AccessTokenExpirationMinutes` | `15` | — | |
| `JwtSettings__RefreshTokenExpirationDays` | `7` | — | cookie expiry mirrors this |
| `EmailSettings__SmtpServer` / `__Port` / `__SenderEmail` / `__SenderName` / `__Username` / `__Password` / `__EnableSsl` | — / `587` / — | OTP only | any SMTP (Gmail app-password works) |
| `StripeSettings__SecretKey` / `__PublishableKey` / `__WebhookSecret` | `""` | payments only | `sk_test_…` / `pk_test_…` / `whsec_…` |
| `DatabaseSeeder__AdminEmail` / `__AdminPassword` / `__AdminUsername` | `admin@virtualstore.com` / `Admin123!` / `admin` | — | defaults log a warning — rotate |
| `CorsSettings__AllowedOrigins__0…` | `http://localhost:3000`, `https://localhost:7038`, `https://localhost:5293` | — | extend for your SPA |
| `ASPNETCORE_ENVIRONMENT` | `Development` | — | `Production` in compose |

Copy `.env.example` → `.env` for local dev. Production: inject via host env or vault; never commit `.env`.

## Run

```bash
cp .env.example .env          # fill secrets
dotnet build VirtualStore.slnx -c Release
cd VirtualStore.API && dotnet run   # https://localhost:7038, http://localhost:5293
```

Scalar UI: `https://localhost:7038/scalar/v1` (Development only, Authorize button = JWT Bearer).

## docker-compose

`docker-compose.yml` runs `mongo:7.0` (health-checked via `mongosh ping`, persisted in `mongo-data` volume) + `api` (built from `Dockerfile`, port `8080`, `ASPNETCORE_ENVIRONMENT=Production`, Mongo pointed at `mongo:27017`, secrets from `.env` via `env_file`).

```bash
docker compose up --build
```

## Health endpoints

- `GET /health` — liveness + MongoDB `ready` check (`AspNetCore.HealthChecks.MongoDb`). Use for container healthchecks and load-balancer probes. Anonymous.
- Boot also runs `EnsureIndexesAsync` (non-fatal: warning on failure) and `DatabaseSeeder` — check startup logs for `MongoDB index creation failed` or `Default admin credentials are in use`.

## Quartz (03:00 AM)

`RefreshTokenCleanupJob`, cron `0 0 3 * * ?`, `[DisallowConcurrentExecution]`: deletes expired unrevoked refresh tokens across users, logs `Removed {Count} expired refresh tokens`. Missed runs are benign (next night catches up).

## Logs (Serilog)

Configured in `appsettings.json`: `Information` minimum, sinks = console + `Logs/log-.txt` (daily rolling). Request lines (`HTTP {Method} {Path}`) come from inline middleware; errors carry `TraceId` (also returned in `ProblemDetails.traceId` — correlate with that). No log shipping configured — mount/persist `Logs/` in production.

## Observability opt-ins

- **OTLP / OpenTelemetry: in progress (wave 2f)** — not merged. When it lands: point `OTEL_EXPORTER_OTLP_ENDPOINT` at your collector; traces will join the existing `traceId`.
- **Rate limiting: in progress (wave 2f)** — no limiter merged; `429` is reserved in the API contract. Until then, protect public edges (login, webhook) with reverse-proxy throttling.
- Metrics endpoint: none yet — scrape via OTel (wave 2f) or add `OpenTelemetry.Exporter.Prometheus` per your platform.

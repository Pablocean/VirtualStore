# OPERATIONS.md — run, configure, observe VirtualStore

## Environment keys

All settings bind from `appsettings.json`, overridden by env/`.env` (`__` separator, loaded by `dotenv.net`). `appsettings.json` ships secrets **empty** by design and `DatabaseSeeder` ships empty (code falls back to `admin@virtualstore.com` / `Admin123!` / `admin`). Startup **fails fast**: `ValidateRequiredConfiguration` (top of `AddApplicationServices`, before JWT setup) throws `InvalidOperationException` when `JwtSettings:Secret` is shorter than 32 chars or `MongoDbSettings:ConnectionString` is empty (all environments); in `Production` an empty `StripeSettings:SecretKey` or `EmailSettings:Password` also throws, while in `Development` those two only log a warning. `ConfigDriftTests` keeps `appsettings.json` ↔ `.env.example` in sync (excluding `Serilog`/`Logging`/`AllowedHosts`).

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
| `CorsSettings__AllowedOrigins__0/__1/__2` | `http://localhost:3000`, `https://localhost:7038`, `https://localhost:5293` | — | extend for your SPA |
| `Redis__ConnectionString` | `""` | — | empty = in-memory cache + OTP (single instance); set to `host:port` for shared HybridCache L2 + distributed OTP |
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

`docker-compose.yml` runs `mongodb/mongodb-community-server:7.0-ubuntu2204` as a single-node replica set (health-checked via `mongosh ping`, persisted in `mongo-data` volume) + `api` (built from `Dockerfile`, port `8080`, `ASPNETCORE_ENVIRONMENT=Production`, Mongo pointed at `mongo:27017`, secrets from `.env` via `env_file`).

```bash
docker compose up --build
```

## MongoDB replica set (required for transactions)

Multi-document transactions (Epic A1: order checkout, stock decrement) fail on a standalone `mongod` with `TransactionNumbers are only allowed on a replica set member or mongos`. Every environment must therefore run MongoDB as a replica set — even single-node dev/CI.

- **docker-compose / local dev:** image `mongodb/mongodb-community-server:7.0-ubuntu2204` boots single-node RS by default (no `rs.initiate` needed, no extra flags). Connection string is unchanged: `mongodb://mongo:27017` (compose) / `mongodb://localhost:27017` (local).
- **CI (`.github/workflows/ci.yml` service + Testcontainers fixture):** same community-server image, same connection string shape (`mongodb://localhost:27017`). Transaction-capable tests run unmodified.
- **Atlas (M0+):** replica set by default — no action needed. Keep using the `mongodb+srv://` connection string.
- **Production self-hosted (multi-node):** initialize the RS once on first deploy (`mongosh --eval 'rs.initiate(...)'` with your members), then use `mongodb://host1,host2,host3/?replicaSet=<name>` as `MongoDbSettings__ConnectionString`. Verify with `mongosh --eval 'db.adminCommand("ping"); rs.status().ok'`. Keyfiles/TLS per your hardening guide — out of scope here.

## Health endpoints

- `GET /health` — liveness + MongoDB `ready` check (`AspNetCore.HealthChecks.MongoDb`). Use for container healthchecks and load-balancer probes. Anonymous.
- Boot also runs `EnsureIndexesAsync` (non-fatal: warning on failure) and `DatabaseSeeder` — check startup logs for `MongoDB index creation failed` or `Default admin credentials are in use`.

## Quartz (03:00 AM)

`RefreshTokenCleanupJob`, cron `0 0 3 * * ?`, `[DisallowConcurrentExecution]`, misfire `DoNothing` (a missed firing is skipped; the next night catches up — the job is idempotent). Paged sweep (100 users/page): purges tokens that are (expired AND never-revoked) OR (expired AND revoked older than 90 days); revoked-within-90d tokens are kept for forensics. Per-user try/catch — one bad doc logs a warning (`failed to purge tokens for user {UserId}`) without aborting the run; logs `Removed {Count} expired refresh tokens across {Users} users`. See ADR-0010.

## Cache (HybridCache + Redis L2 opt-in)

`ICacheService` is backed by `HybridCache` (in-memory L1; absolute TTL when the caller passes one, else 5-minute absolute default). 2FA OTP codes + attempt counters live on `IDistributedCache` (10 min, lockout at 5). With `Redis__ConnectionString` empty everything is in-process (single-instance default); set it to `host:port` (compose: add a `redis:8` service + `Redis__ConnectionString=redis:6379`) to share cache + OTP across instances. No Redis health check wired yet — see ADR 0011.

## Logs (Serilog)

Configured in `appsettings.json`: `Information` minimum, sinks = console + `Logs/log-.txt` (daily rolling). Request lines (`HTTP {Method} {Path}`) come from inline middleware; errors carry `TraceId` (also returned in `ProblemDetails.traceId` — correlate with that). No log shipping configured — `docker-compose.yml` bind-mounts `./Logs:/app/Logs` on the `api` service so file logs survive container restarts; back it up or ship it per your platform.

## Backup & restore (MongoDB, ADR-0010)

Back up the `mongo-data` volume (or Atlas continuous backup). File-system volume snapshots alone can capture a mid-write WiredTiger state — always pair them with a logical dump:

```bash
# Nightly logical dump (02:00, before the 03:00 token cleanup), retains 7 days:
docker exec virtualstore-mongo-1 mongodump --out /dump/virtualstore-$(date +%F)
# or against Atlas / remote:
mongodump --uri "$MongoDbSettings__ConnectionString" --out ./backups/virtualstore-$(date +%F)
find ./backups -maxdepth 1 -mtime +7 -delete
```

Restore procedure:

```bash
# 1. Stop writers (scale api to 0) so no new orders land mid-restore.
# 2. Drop + restore into the target database:
mongorestore --uri "mongodb://localhost:27017" --drop ./backups/virtualstore-2026-09-20
# 3. Re-run EnsureIndexesAsync (boot does this automatically — just restart api)
#    and verify: mongosh --eval 'db.Order.countDocuments(); db.User.countDocuments()'.
# 4. Rotate JwtSettings__Secret if the backup may have exposed session material,
#    and invalidate Stripe webhook dedup expectations (ProcessedWebhookEvent TTLs
#    are 30d — a restore older than that simply reprocesses redeliveries safely).
```

**RTO/RPO note:** with a nightly 02:00 dump, worst-case data loss (RPO) is ~24 h of orders/users; single-`mongorestore` recovery (RTO) is minutes for this dataset size. Tighten RPO with 6-hourly dumps or Atlas point-in-time recovery if order volume justifies it. Test the restore quarterly — an untested backup is not a backup.

## Observability opt-ins

- **OTLP / OpenTelemetry: in progress (wave 2f)** — not merged. When it lands: point `OTEL_EXPORTER_OTLP_ENDPOINT` at your collector; traces will join the existing `traceId`.
- **Rate limiting: in progress (wave 2f)** — no limiter merged; `429` is reserved in the API contract. Until then, protect public edges (login, webhook) with reverse-proxy throttling.
- Metrics endpoint: none yet — scrape via OTel (wave 2f) or add `OpenTelemetry.Exporter.Prometheus` per your platform.

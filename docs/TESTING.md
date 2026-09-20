# TESTING.md — VirtualStore test strategy

> Test suites are owned by parallel wave 2e — this file defines the contract so suites stay consistent. Reference test layout generically (`tests/*`); do not hard-code project names that wave 2e may choose.

## Categories

| Category | Scope | Speed | External deps |
|---|---|---|---|
| **Unit** | Services, validators, `UserRoles`, `PagedResult`, `MappingProfile`, status-machine transitions | ms | none (mock `IRepository<T>`, `IMemoryCache`, `IOptions<T>`) |
| **Integration** | Controllers via `WebApplicationFactory` + real MongoDB | seconds | **Testcontainers for MongoDB** (or Atlas test cluster) — never the dev database |

No test may hit Stripe/SMTP: mock `IStripePaymentService` / `IEmailService`. Webhook tests forge payloads through `EventUtility.ConstructEvent` with the test `WebhookSecret` (same path as production verification).

## What to cover (minimum per slice)

1. **Service unit**: happy path + `KeyNotFoundException` (→404) + `InvalidOperationException` (→409) + `UnauthorizedAccessException` (→401). `OrderService`: server-side re-pricing (client total ignored), stock decrement, cart cleared only after insert, every allowed/forbidden status transition.
2. **Validator**: one case per rule across the 19 validators (`LoginRequest`, auth lifecycle (`Change/Confirm/Resend/Forgot/ResetPassword`), `Create/Update` user/product/category, cart, order items, address, purge).
3. **Controller integration**: happy path + auth matrix (anonymous → 401, wrong role → 403, `Admin`-only users endpoints) + one `ProblemDetails` assertion (`traceId` present, `errors` map on 400).
4. **Auth flows**: login → refresh rotation → reuse detection (replay revoked token ⇒ all tokens revoked + 401) → logout; OTP issue/verify/attempt-lockout (5 max); lockout (5 bad passwords → 423) + email-confirm gate (403); token cap (11th login revokes oldest, 10 active max); purge/export (GDPR).
5. **Payments**: intent creation shape, deterministic idempotency keys, post-commit linkage + retry attach, refund (incl. `400` without payment intent, stock restore on full only), webhook signature rejection (`400`), state mirroring (`PaymentReceived`/`Cancelled`), redelivery dedup (`duplicate: true`).
6. **Config**: `ConfigDriftTests` keeps `appsettings.json` ↔ `.env.example` in sync; startup fails fast on missing secrets.

## Requirements

- **Testcontainers (or equivalent) is mandatory for integration tests** — MongoDB indexes (`ux_*` unique) and linear reads make fakes/mocks invalid substitutes. Tests must start their own container, run `EnsureIndexesAsync`, and drop the database per suite.
- Keep the build green without containers: integration tests are trait/category-gated so `dotnet test` on a bare agent runs unit tests; CI runs the full matrix with Docker available.

## Commands

```bash
dotnet build VirtualStore.slnx -c Release          # 0 errors gate
dotnet test VirtualStore.slnx -c Release           # unit gate (no containers needed)
# Full matrix incl. Testcontainers integration tests (needs Docker):
dotnet test VirtualStore.slnx -c Release --filter "Category=Integration"
```

Coverage (coverlet): `dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover` — target ≥ 80% on `Application` validators/DTO-adjacent logic and `Infrastructure` services; controllers are covered via integration, not unit mocks.

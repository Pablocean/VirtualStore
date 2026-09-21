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

## Coverage policy — 100% line on non-excluded code (Wave T3 gate)

CI enforces the policy on the **merged** full-suite report (unit + integration
cobertura union: a line counts as covered when *either* suite hits it) and
**fails the job below 100% line coverage** (see `.github/workflows/ci.yml`,
"Coverage gate" step). Rationale: the two suites are complementary by design —
unit tests own services/validators/helpers with mocked `IRepository<T>`, while
integration tests own controllers, `ApiExceptionHandler`, `MongoRepository`,
`MongoDbContext`, and DI wiring over real MongoDB. Neither suite alone reaches
100%; together they must.

Exclusion list (the only code allowed below 100%):

| Excluded | Mechanism | Justification |
|---|---|---|
| `Program.cs` (host bootstrapping) | `[ExcludeFromCodeCoverage]` (honored by coverlet) | Top-level host wiring only; exercised implicitly by `WebApplicationFactory` boots but never countable as unit-coverable logic |
| `*.generated.cs` under `obj/` (e.g. OpenAPI `XmlCommentGenerator` output) | `excluded()` filename filter in the gate step | Source-generated code no test can meaningfully execute |

No other exclusions are permitted: adding `[ExcludeFromCodeCoverage]` or new
gate-step filters to dodge the policy requires an ADR.

Measured locally (unit suite only, no Docker — integration suites SKIP):

```text
overall (unit only): 71.44% line / 56.16% branch  (2,690 lines)
  VirtualStore.API:            55.45% line   (controllers + handler covered by integration)
  VirtualStore.Application:    98.71% line / 95.0% branch
  VirtualStore.Domain:         98.05% line / 100% branch
  VirtualStore.Infrastructure: 67.66% line   (Mongo/Stripe/SMTP paths covered by integration)
552/552 unit tests green; 28 integration tests Docker-gated SKIP.
```

Full-suite (CI) numbers are printed per-assembly by the gate step on every run;
per-assembly floors are intentionally *not* pinned — only the 100% overall
line gate binds, so suites stay free to shift coverage between unit and
integration as slices evolve.

Docker-gated pattern (both suites): integration facts use the shared
`RequiresDockerFact` / `Category=Integration` gate — SKIP (never fail) without
a daemon, run green in CI where the `mongo` service + Docker daemon are
present. Unit gate stays `dotnet test --filter Category!=Integration` so bare
agents never need containers.

## Commands

```bash
dotnet build VirtualStore.slnx -c Release          # 0 errors gate
dotnet test VirtualStore.slnx -c Release           # unit gate (no containers needed)
# Full matrix incl. Testcontainers integration tests (needs Docker):
dotnet test VirtualStore.slnx -c Release --filter "Category=Integration"
```

Coverage (coverlet collector): full suite collects `--collect:"XPlat Code Coverage"`
into `./TestResults`; the CI gate step merges the per-project cobertura files
and fails below 100% line coverage on non-excluded code (exclusions: §Coverage
policy above — nothing else). Local equivalent of the gate: run the unit suite
with coverage, then the integration suite with Docker, and union the reports.

# SECURITY.md — VirtualStore threat model & controls

## JWT / refresh rotation / reuse detection

- Access token: HMAC-SHA256 JWT, claims `sub` + `NameIdentifier` (= `user.Id`), `email`, `jti`, one `role` claim per role; issuer/audience validated; `ClockSkew = Zero`; default lifetime **15 min** (`JwtSettings__AccessTokenExpirationMinutes`).
- Refresh token: 32-byte opaque value (`RandomNumberGenerator`), stored hashed? **No — stored as listed on `User.RefreshTokens`** with `Expires` (default 7 d), `Created`, `CreatedByIp`, `Revoked`/`RevokedByIp`, `ReplacedByToken`. Transport only via HttpOnly + `Secure` + `SameSite=Strict` cookie.
- Rotation: every `POST /api/auth/refresh-token` revokes the presented token and links it to its replacement (`ReplacedByToken`).
- **Reuse detection**: presenting an already-revoked token revokes *all* active tokens for that user, then returns `401` — a stolen-token replay bricks the whole family instead of minting a new session.
- Logout revokes the presented token. Expired-but-unrevoked tokens are purged nightly by Quartz (revoked ones are retained for forensics).

## Token retention & GDPR erasure (Epic E, ADR-0010)

- **Active-token cap:** at most **10 active** refresh tokens per user — on login, appending the 11th revokes the oldest-active (`Revoked` + `ReplacedByToken` link). Bounds session-table growth per account.
- **Nightly purge (`RefreshTokenCleanupJob`, 03:00):** removes tokens that are (expired AND never-revoked) OR (expired AND revoked **older than 90 days**). Revoked-within-90d tokens are kept as reuse-detection evidence, then age out. The sweep is paged (100 users/page) and per-user-isolated: one corrupt doc logs a warning and the run continues; the idempotent job is retried by the next-night run (misfires skipped via `WithMisfireHandlingInstructionDoNothing`).
- **Self-service privacy (`MeController`, `api/me`, any authenticated role):** `GET /api/me/export` returns profile + orders + carts + token metadata with **no secrets** (no hash, no token values); `POST /api/me/purge {confirmPassword}` BCrypt-verifies intent, then hard-deletes the user (`HardDeleteAsync`, bypassing soft-delete), deletes carts, pseudonymizes orders (`UserId → "deleted:{sha256hex}"`, address emptied, financials kept), and clears OTP/confirm/reset cache keys. Admin `DELETE /api/users/{id}` remains a soft-delete (`IsDeleted` flag) — erasure and deactivation are deliberately distinct operations.

## OTP (2FA) hardening

- 6-digit code from `RandomNumberGenerator` (100000–999999), cached as `otp_{lowercase-email}` with **10-minute absolute TTL**, emailed via SMTP.
- Verification uses `CryptographicOperations.FixedTimeEquals` (constant-time compare).
- Attempt counter `otp_attempts_{lowercase-email}` (same TTL); **≥ 5 attempts → `401 "Too many OTP attempts"`** until the window expires. Success clears both keys.
- OTP is required only when `User.TwoFactorEnabled`; first login call (no `otpCode`) triggers issuance and returns `{ requiresTwoFactor: true }` without tokens.

## Passwords & users

- `BCrypt.Net` hash at creation (`DatabaseSeeder`, `UserService`); verify with `BCrypt.Verify` — constant work factor, per-password salts.
- Login failure returns generic `401 "Invalid credentials"` (no email-or-password oracle).
- **Users surface is Admin-only** — no public registration; roles assigned via `CreateUserDto.Roles` and validated by `UserRoles.EnsureValid`.
- Roles are a discrete list (`Customer/Manager/Admin`); no `[Flags]`, no bitwise checks — prevents privilege confusion from combined bit values.

## Account lifecycle (Epic C, ADR-0008)

- **Lockout:** `User.FailedAccessCount` + `User.LockoutEnd` (internal, never mapped).
  Bad password increments (persisted); ≥ 5 failures sets `LockoutEnd = +15 min`
  (that attempt still returns 401; the next attempt returns `423 Locked`).
  Expired lockouts clear silently; success resets both counters.
- **Email confirmation gate:** login after password+lockout checks rejects
  unconfirmed emails with `403 "Email Not Confirmed"`. The seeded admin has
  `EmailConfirmed = true`. Confirmation tokens live in `IDistributedCache` as
  `emailconfirm_{userId-lower}` (24 h TTL, single-use, constant-time compare).
- **Password lifecycle:** `change-password` (auth, BCrypt-verifies current) and
  `reset-password` (anonymous, `pwdreset_{email-lower}`, 1 h TTL, single-use,
  constant-time compare) both enforce the shared rule (min 8, upper+lower+digit),
  rehash, and revoke ALL refresh tokens. `resend-confirmation` and
  `forgot-password` ALWAYS return 200 with a generic message (no enumeration).
- All lifecycle tokens are crypto-random 32-byte hex; all lifecycle mail goes
  through the existing resilient `SendEmailAsync` pipeline.

## Transport & CORS

- `UseHttpsRedirection` enforced; refresh cookie is `Secure` (note: cookie auth over plain HTTP in local dev will silently drop — use the HTTPS profile).
- CORS allowlist from `CorsSettings:AllowedOrigins` **with `AllowCredentials`** — never widen to `*` while credentials are allowed; preflight only permits the configured origins (`http://localhost:3000` etc.).

## Webhook signature

- `POST /api/stripe/webhook` is anonymous by design; authentication = `Stripe-Signature` header verified by `EventUtility.ConstructEvent(json, signature, WebhookSecret)` — throws `StripeException` → `400 ProblemDetails` on mismatch. Never bypass verification for "testing".
- `orderId` is bound via Stripe PaymentIntent **metadata** (attacker-controlled metadata can't escalate: unknown ids only produce logged no-ops with `200`).
- **Dedup (ADR-0007):** each Stripe event id is recorded once (`ProcessedWebhookEvent`, unique `ux_webhookevent_eventId`, 30-day TTL). Redeliveries return `duplicate: true` with 200 and apply no state change.
- **No retry storms:** the handler wraps its body in a catch-all — unexpected failures log a warning and still answer 200. Only signature failures return 400.
- **Rate limiting:** the webhook sits on its own `webhook` policy (60 req/min per IP sliding window); auth endpoints stay on the stricter `auth` policy (5/min).

## Secrets & admin bootstrap

| Secret | Source | Notes |
|---|---|---|
| `JwtSettings__Secret` | env/`.env` | ≥ 32 chars (64 recommended); `appsettings.json` ships empty |
| `MongoDbSettings__ConnectionString` | env | no credentials in repo |
| `EmailSettings__Password` | env | SMTP app-password, not the mailbox password |
| `StripeSettings__SecretKey` / `__WebhookSecret` | env | test keys (`sk_test_…`/`whsec_…`) in dev; live keys only in prod vault |
| `DatabaseSeeder__AdminEmail/Password/Username` | env | defaults (`admin@virtualstore.com` / `Admin123!`) log a **warning** when used — rotate immediately |

`.env` is gitignored; `.env.example` documents every key. Production must inject via environment/vault — never bake secrets into the Docker image.

**Fail-fast startup (Epic D):** `ValidateRequiredConfiguration` runs at the top of `AddApplicationServices` (before JWT setup, via `IConfiguration` reads) and throws `InvalidOperationException` naming the key and fix when `JwtSettings:Secret` is shorter than 32 chars or `MongoDbSettings:ConnectionString` is empty (all environments). In `Production` an empty `StripeSettings:SecretKey` or `EmailSettings:Password` also throws; in `Development` those two only log a startup warning so local boot isn't blocked.

## Residual risks (accepted / queued)

- Refresh tokens stored server-side in clear text — DB read = session hijack; accepted (Mongo access = full compromise anyway), mitigated by reuse detection + short access lifetime.
- Rate limiting is enforced per IP (`auth` 5/min, `webhook` 60/min, global 100/min; `429` ProblemDetails with `Retry-After`) plus per-account login lockout (5 failures → 15 min `423`). Do not expose the API to the open internet without these (reverse-proxy throttling as defense in depth).
- Centralized audit logging is still queued; OpenTelemetry tracing/metrics are wired with opt-in OTLP export.

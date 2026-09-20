# ADR 0008 — Auth lifecycle: lockout, email confirmation, password lifecycle (Epic C)

- Status: Accepted
- Date: 2026-09-20 (wave 3d, Epic C)

## Context

Login had no brute-force throttle beyond OTP attempt counting, no email-confirmation
gate (unconfirmed users could take sessions), and no self-service password lifecycle
(confirm / resend / change / forgot / reset). `User` carried no lockout state, and
`ApiExceptionHandler` only knew 400/401/404/409/500.

## Decision

- **Lockout state on `User`.** `FailedAccessCount` (int) + `LockoutEnd` (`DateTime?`,
  internal only). `MappingProfile` ignores both on create/update maps (no source
  member exists — the ignores keep `AssertConfigurationIsValid` green).
- **Login order: lockout → password → email-confirmed → 2FA → tokens.**
  Expired lockouts clear silently. Active lockout (`LockoutEnd > UtcNow`) throws
  `AccountLockedException` before password work. Bad password increments
  `FailedAccessCount` (persisted); at ≥ 5 sets `LockoutEnd = +15 min` and still
  returns 401 — the *next* attempt gets 423. Success resets both counters.
  Unconfirmed email throws `EmailNotConfirmedException` after password+lockout
  checks, before OTP issuance (no OTP to unconfirmed addresses).
- **New exception types** (`Application/Common`, both extend
  `UnauthorizedAccessException` so existing catch sites keep working):
  `AccountLockedException` → **423 Locked** (`"Locked"`),
  `EmailNotConfirmedException` → **403** (`"Email Not Confirmed"`).
  Handler cases are ordered before the base `UnauthorizedAccessException` case.
- **Token storage follows the post-HybridCache pattern: `IDistributedCache`.**
  Crypto-random 32-byte hex tokens (`RandomNumberGenerator`), constant-time compare
  (`CryptographicOperations.FixedTimeEquals`), absolute TTLs, deleted after use:
  - `emailconfirm_{userId-lower}` — 24 h, single-use → `EmailConfirmed = true`.
  - `pwdreset_{email-lower}` — 1 h, single-use → rehash + revoke all refresh tokens.
- **Endpoints** (all on the existing `auth` rate-limit policy, 5/min per IP):
  - `POST /api/auth/confirm-email {email, token}` — 200 on success, 409 on
    bad/expired token (`InvalidOperationException`).
  - `POST /api/auth/resend-confirmation {email}` — ALWAYS 200 with a generic
    message; sends only when the user exists and is unconfirmed (no enumeration).
  - `POST /api/auth/change-password [Authorize] {currentPassword, newPassword}` —
    BCrypt-verifies current, enforces the shared password rule, rehashes, revokes
    ALL refresh tokens. Wrong current → 401; weak new → 400.
  - `POST /api/auth/forgot-password {email}` — ALWAYS 200 generic; stores reset
    token + email only when the user exists (no enumeration).
  - `POST /api/auth/reset-password {email, token, newPassword}` — constant-time
    compare, single-use delete, policy check, rehash, revoke all refresh tokens;
    bad/expired token → 409 with a generic message.
- **Password rule** (single source of truth): min 8, upper + lower + digit —
  declared in validators (400 at the HTTP boundary, auto-validation) and enforced
  in-service via `Application/Common/PasswordPolicy.EnsureValid` (400) for
  non-HTTP call paths. `IEmailService` gains `SendConfirmationEmailAsync` +
  `SendPasswordResetEmailAsync`, both funneled through the existing resilient
  `SendEmailAsync` pipeline; the OTP method is untouched.
- **No new packages** (ApplyRefund-style minimalism).

## Consequences

- (+) Password-guessing is throttled per account (5 attempts → 15 min lockout);
  423 vs 401 does reveal account existence to an attacker who already guesses
  passwords — accepted, consistent with the lockout signal itself.
- (+) Registration stays Admin-only; confirmation is bootstrapped via
  resend-confirmation (user creation does NOT auto-send — avoids coupling
  `UserService` to the mail pipeline).
- (+) All password changes kill every session (reuse-safe); reset/confirm tokens
  cannot be replayed.
- (−) Login by email is case-sensitive (pre-existing behavior, unchanged).
- (−) `pwdreset_` keys are keyed by email — renaming an email orphans an
  outstanding reset token until its 1 h TTL expires; acceptable.

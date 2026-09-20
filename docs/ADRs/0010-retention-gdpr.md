# ADR 0010 — Token retention caps, GDPR export/purge, backup runbook (Epic E)

- Status: Accepted
- Date: 2026-09-20 (wave 3e, Epic E)

## Context

Refresh tokens accumulated without bound (every login appended; only
expired-*unrevoked* tokens were swept nightly), revoked tokens were kept
forever-or-never with no stated policy, users had no self-service GDPR
export/erasure (only the Admin-only soft-delete existed), and there was no
backup/restore runbook for the MongoDB volume.

## Decision

- **Active-token cap: last-10-active.** On login (`AuthService.LoginAsync`,
  `MaxActiveRefreshTokens = 10`), after appending the new token the
  oldest-active tokens beyond the cap are revoked (`Revoked` + `RevokedByIp`
  set, `ReplacedByToken` linked to the new token). Cap enforcement counts
  only ACTIVE tokens (`Revoked == null && !IsExpired`); revoked/expired
  entries never trigger eviction. Rotation (`RefreshTokenAsync`) is untouched.
- **Cleanup job rewrite (`RefreshTokenCleanupJob`, still 03:00
  `0 0 3 * * ?`, still `[DisallowConcurrentExecution]`).** Per-token purge
  rule at run time `now`, forensics cutoff `now − 90d`:
  - Expired AND never-revoked → purge (existing behavior, kept).
  - Expired AND revoked older than 90d → purge.
  - Revoked within 90d → KEEP (reuse-detection evidence / forensics).
  - Anything unexpired → KEEP.
  - Paged sweep (`PagedAsync`, 100 users/page, dirty prefilter
    `RefreshTokens.Any(rt => rt.Expires < now)` with the exact rule applied
    in memory), per-user `UpdateAsync`, try/catch per user AND per page so
    one bad doc never aborts the run. The job is idempotent → retry is the
    next-night run. Trigger uses
    `WithMisfireHandlingInstructionDoNothing`: a missed 03:00 firing is
    skipped and the next night catches up (same benign-miss semantics as
    before; Quartz 3.13 `CronScheduleBuilder` API).
- **GDPR self-service (`MeController`, `api/me`, `[Authorize]` any role,
  user id from `User.GetUserId()` — never the body):**
  - `GET /api/me/export` → `MeExportDto{profile, orders, carts,
    tokenMetadata, exportedAt}`. Token metadata carries Created/CreatedByIp/
    Expires/Revoked/RevokedByIp/HasReplacement/IsActive — NEVER the `Token`
    or `ReplacedByToken` secret values; profile never carries the password
    hash.
  - `POST /api/me/purge {confirmPassword}` → BCrypt-verifies the current
    password (never logged; `PurgeRequestDtoValidator` requires non-empty),
    then: pseudonymize orders (`UserId → "deleted:{sha256hex(userId)}"`,
    `ShippingAddress = new Address()`, items/totals/status kept),
    hard-delete carts, clear `IDistributedCache` keys
    (`otp_`, `otp_attempts_`, `emailconfirm_`, `pwdreset_` for that identity),
    hard-delete the user document LAST. Returns 200 `PurgeResultDto`
    `{userDeleted, cartsDeleted, ordersPseudonymized}`.
  - Wrong password → 401 (`UnauthorizedAccessException`); unknown user → 404.
- **Hard-delete primitive:** `IRepository<T>.HardDeleteAsync`
  (`DeleteOneAsync`, session-aware) on `MongoRepository<T>`, reserved for
  GDPR erasure. Admin `DELETE /api/users/{id}` STAYS soft-delete
  (`IsDeleted` flag, data retained) — the distinction is documented in
  `API.md` and on the `MeController` summary.
- **Backup runbook** in `OPERATIONS.md` (mongodump schedule + restore +
  RTO/RPO) and a `./Logs:/app/Logs` volume mount in `docker-compose.yml`
  so Serilog file logs survive restarts.
- **No new packages.**

## Consequences

- (+) Sessions per user are bounded (≤ 10 active); token-table growth is
  capped by cap + nightly purge.
- (+) Revoked-token forensics survive 90d for reuse investigations, then age
  out automatically — no unbounded growth.
- (+) GDPR Art. 15 (export) / Art. 17 (erasure) covered self-service;
  financial records survive erasure in anonymized form (orders keep
  items/totals/status; `ix_order_userId` still indexes the pseudonym ref).
- (+) Restore path is documented and rehearsable (runbook + RTO/RPO).
- (−) Pseudonym is deterministic (`sha256(userId)`): two purges of the same
  id link to the same ref — accepted (the id itself is unrecoverable).
- (−) Purge is not transactional across user/cart/order collections: a crash
  mid-purge can leave orders pseudonymized while the user doc remains —
  accepted, re-running purge (or the admin soft-delete) converges; user doc
  is deleted last to minimize the window.
- (−) `pwdreset_`/`otp_` keys are email-keyed: purged emails leave no
  residue (keys are deleted explicitly), but an in-flight reset email for a
  purged address becomes a dangling link that fails closed (unknown user →
  409/200-generic).

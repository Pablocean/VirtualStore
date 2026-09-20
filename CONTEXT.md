# CONTEXT.md — VirtualStore domain language

Ubiquitous language for the shop. Code references are illustrative, not exhaustive.

## Roles (hierarchy, additive — NOT flags)

`UserRole`: `Customer = 1`, `Manager = 2`, `Admin = 4`. Membership is `User.Roles: List<UserRole>` — discrete values, no bitmask (`UserRoles.EnsureValid`: distinct, defined, non-empty).

| Role | Capabilities |
|---|---|
| `Customer` | Browse catalog, own cart, place orders, pay, view own orders, read enterprise info |
| `Manager` | Customer + create/update products and categories |
| `Admin` | Everything + delete products/categories, user CRUD (**users endpoints are Admin-only**), order status transitions, refunds, enterprise-info writes, viewing any order |

Bootstrap admin carries all three roles (`DatabaseSeeder`). `Manager` cannot touch users, orders, or payments admin surface.

## Order lifecycle (status machine)

`OrderStatus`: `Pending → PaymentReceived → Processing → Shipped → Delivered`, with `Cancelled` as a terminal escape from `Pending`, `PaymentReceived`, `Processing` only, plus refund states (`Refunded`, `PartiallyRefunded`). Full matrix (`OrderService.AllowedTransitions`):

| From | To (allowed) |
|---|---|
| `Pending` | `PaymentReceived`, `Cancelled` |
| `PaymentReceived` | `Processing`, `Cancelled`, `Refunded`, `PartiallyRefunded` |
| `Processing` | `Shipped`, `Cancelled`, `Refunded`, `PartiallyRefunded` |
| `Shipped` | `Delivered`, `PartiallyRefunded` (full refund only via partial first — `Shipped → Refunded` is absent) |
| `PartiallyRefunded` | `Refunded` (one partial per order — no `PartiallyRefunded → PartiallyRefunded`) |
| `Delivered` | `Refunded` |
| `Delivered`, `Cancelled`, `Refunded` | — (terminal) |

Rules: orders are created `Pending` with **server-side pricing** (each `UnitPrice` re-read from `Product`, stock validated + decremented, cart deleted only after the order insert) inside a replica-set transaction (`TransactAsync`); replays with the same `Idempotency-Key` header return the existing order, same key with a different payload → `409`. Only `Admin` may `PATCH /api/orders/{id}/status`; illegal transitions throw `InvalidOperationException` → `409 Conflict`. Owners see own orders; `Admin` may fetch any (`GET /api/orders/{id}` returns `403` for non-owner non-admin).

## Cart model

One `Cart` per user (unique index `ux_cart_userId` on `Cart.UserId`). `Cart.Items: List<CartItem>`; each item snapshots `ProductId`, `ProductName`, `UnitPrice` at add-time; `CartDto.Total` is computed (`Σ unitPrice × quantity`), never stored. Operations: get / add (`AddToCartDto{productId, quantity}`) / update quantity (**raw JSON number** body) / remove item / clear. Cart is **deleted after order placement** — the order is the source of truth from then on.

## Payment flow

1. Client creates order (`POST /api/orders`) → `Pending`, total fixed server-side.
2. Client requests `POST /api/payments/intent` (`CreatePaymentIntentDto{amount, currency, customerId?, orderId?}`) → `StripePaymentService` creates a `PaymentIntent` (amount converted to minor units, `orderId` stored in Stripe metadata) → returns `PaymentIntentResultDto{ paymentIntentId, clientSecret, amount, currency, status }`.
3. Client confirms with Stripe using `clientSecret`.
4. Stripe calls `POST /api/stripe/webhook` (anonymous, `Stripe-Signature` header verified via `EventUtility.ConstructEvent` + `WebhookSecret`):
   - `payment_intent.succeeded` → order → `PaymentReceived`
   - `payment_intent.payment_failed` / `charge.refunded` → order → `Cancelled`
   - unknown types → acknowledged (`200`) with `succeeded: false`, no state change
 5. `Admin` may `POST /api/payments/orders/{id}/refund` (full or partial `amount`, deterministic idempotency key `order:{id}:refund:{amount ?? "full"}`) — requires a stored `StripePaymentIntentId`, else `400`. Full refund → order `Refunded` + stock restored; partial → `PartiallyRefunded`, no stock restore (a partial is a discount/adjustment on kept goods, not a return).

Webhook rule: always answer `200`, even if the order transition fails (logged, not re-thrown). Redelivered Stripe event ids are deduped (`ProcessedWebhookEvent`, 30-day window) and acked with no state change. Note: `charge.refunded` webhooks still map to `Cancelled` (legacy); refund-driven `Refunded`/`PartiallyRefunded` transitions go through the refund endpoint.

## Cache / TTL

`ICacheService` over `HybridCache` (in-memory L1, shared Redis L2 when `Redis__ConnectionString` is set): `Set(key, value, expiration?)` uses absolute expiration when given, otherwise **5-minute absolute** default. OTP codes (`otp_{email}` — 10 min absolute), OTP attempt counters (`otp_attempts_{email}` — 10 min window, lockout at 5), email-confirm (`emailconfirm_{userId}`, 24 h, single-use) and password-reset (`pwdreset_{email}`, 1 h, single-use) tokens live on `IDistributedCache` (in-process default, shared when Redis is set). Keys are lowercase normalized. See ADR 0011 (supersedes the single-instance limits of ADR 0005).

## Auth lifecycle

Login order: lockout → password → email-confirmed → 2FA → tokens. ≥ 5 bad passwords sets `LockoutEnd = +15 min` (that attempt still `401`; the next returns `423 Locked`); success resets. Unconfirmed email → `403 "Email Not Confirmed"`. Lifecycle endpoints: `confirm-email` / `resend-confirmation` (always 200, no enumeration) / `change-password` (auth, revokes all sessions) / `forgot-password` (always 200) / `reset-password` (revokes all sessions). Shared password rule: min 8, upper + lower + digit.

## Token retention & GDPR purge

At most **10 active** refresh tokens per user — logging in with 10 active revokes the oldest (`ReplacedByToken` linked). Nightly cleanup purges expired-never-revoked tokens and expired tokens revoked **older than 90 days**; revoked-within-90d tokens are kept as reuse-detection evidence.

`POST /api/me/purge {confirmPassword}` (BCrypt-verified) is **GDPR erasure**: hard-deletes the user (`HardDeleteAsync`, bypassing soft-delete), deletes carts, pseudonymizes orders (`UserId → "deleted:{sha256hex}"`, address emptied, items/totals/status kept), clears OTP/confirm/reset cache keys. Purge is **not transactional** across collections — the user doc is deleted last so a crash mid-purge converges on retry. Admin `DELETE /api/users/{id}` stays a soft-delete (`IsDeleted` flag, data retained).

## Jobs

`RefreshTokenCleanupJob` (Quartz, `[DisallowConcurrentExecution]`, cron `0 0 3 * * ?` — daily 03:00 AM): removes expired-never-revoked refresh tokens and expired tokens revoked older than 90 days from every user and bumps `UpdatedAt`. Non-destructive: revoked-within-90d tokens are kept for reuse-detection forensics.

## Money & identifiers

- Currency defaults to `"usd"` on product/order DTOs; Stripe amounts are converted to minor units (`amount × 100`, `AwayFromZero`).
- MongoDB ids are strings; collections are named after the entity class (`User`, `Product`, `Category`, `Cart`, `Order`, `EnterpriseInfo`).
- `EnterpriseInfo` is a singleton document (one row, public read, Admin write).
- Auth claims: `sub` + `NameIdentifier` = `user.Id`, `email`, `jti`, one `role` claim per role — read via `User.GetUserId()`.

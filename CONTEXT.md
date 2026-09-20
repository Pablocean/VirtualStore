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

`OrderStatus`: `Pending → PaymentReceived → Processing → Shipped → Delivered`, with `Cancelled` as a terminal escape from `Pending`, `PaymentReceived`, `Processing` only. Full matrix (`OrderService.AllowedTransitions`):

| From | To (allowed) |
|---|---|
| `Pending` | `PaymentReceived`, `Cancelled` |
| `PaymentReceived` | `Processing`, `Cancelled` |
| `Processing` | `Shipped`, `Cancelled` |
| `Shipped` | `Delivered` |
| `Delivered`, `Cancelled` | — (terminal) |

Rules: orders are created `Pending` with **server-side pricing** (each `UnitPrice` re-read from `Product`, stock validated + decremented, cart deleted only after the order insert). Only `Admin` may `PATCH /api/orders/{id}/status`; illegal transitions throw `InvalidOperationException` → `409 Conflict`. Owners see own orders; `Admin` may fetch any (`GET /api/orders/{id}` returns `403` for non-owner non-admin).

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
5. `Admin` may `POST /api/payments/orders/{id}/refund` (full or partial `amount`) — requires a stored `StripePaymentIntentId`, else `400`.

Webhook rule: always answer `200`, even if the order transition fails (logged, not re-thrown).

## Cache / TTL

`ICacheService` over `IMemoryCache`: `Set(key, value, expiration?)` uses absolute expiration when given, otherwise **5-minute sliding**. Current uses: email OTP codes (`otp_{email}` — 10 min absolute) and OTP attempt counters (`otp_attempts_{email}` — 10 min window, lockout at 5). Keys are lowercase-email normalized. No distributed cache — single-instance semantics (see ADR 0005).

## Jobs

`RefreshTokenCleanupJob` (Quartz, `[DisallowConcurrentExecution]`, cron `0 0 3 * * ?` — daily 03:00 AM): removes expired, not-yet-revoked refresh tokens from every user and bumps `UpdatedAt`. Non-destructive: revoked-but-unexpired tokens are kept for reuse-detection forensics.

## Money & identifiers

- Currency defaults to `"usd"` on product/order DTOs; Stripe amounts are converted to minor units (`amount × 100`, `AwayFromZero`).
- MongoDB ids are strings; collections are named after the entity class (`User`, `Product`, `Category`, `Cart`, `Order`, `EnterpriseInfo`).
- `EnterpriseInfo` is a singleton document (one row, public read, Admin write).
- Auth claims: `sub` + `NameIdentifier` = `user.Id`, `email`, `jti`, one `role` claim per role — read via `User.GetUserId()`.

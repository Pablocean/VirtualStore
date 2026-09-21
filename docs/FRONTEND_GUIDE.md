# FRONTEND_GUIDE.md — VirtualStore frontend developer guide

> Every claim below was verified against the controller / DTO / validator /
> service source on this branch. Where this guide corrects `docs/API.md`,
> the correction is marked **[CORRECTS API.md]**. Base URLs (dev):
> `https://localhost:7038` (HTTPS) and `http://localhost:5293` (HTTP).
> Interactive reference: `https://localhost:7038/scalar/v1` (dev-only,
> JWT via the Authorize button). Full route table: `docs/API.md`.

## 0. Conventions used in this guide

- `Auth` = send `Authorization: Bearer <accessToken>`. `Cookie` = the
  `refreshToken` HttpOnly cookie must ride along → every `fetch` needs
  `credentials: 'include'` (see §1.1).
- `PagedResult<T>` and `ProblemDetails` shapes are in §1.3–§1.4; they are
  identical on every endpoint — learn them once.
- Money: API amounts are **major units** (dollars); the server converts to
  Stripe minor units (cents) itself (`StripePaymentService.ToMinorUnits`).
  Never multiply by 100 client-side.
- Enums are **strings** on the wire (`JsonStringEnumConverter`): `"Admin"`,
  `"PaymentReceived"` — never send numeric values or ordinals.

---

## 1. Global contract

### 1.1 Transport, auth primitives, CORS

| Rule | Source of truth |
|---|---|
| Access token goes in `Authorization: Bearer <token>` on every `Auth` route. | `JwtBearer` in `ServiceExtensions` |
| Refresh token lives **only** in the `refreshToken` cookie: `HttpOnly`, `Secure`, `SameSite=Strict`, 7-day expiry. JS can never read it — that is the point. | `AuthController.SetRefreshTokenCookie` |
| Send `credentials: 'include'` on **all** requests (auth cookie + CORS `AllowCredentials`). | `ServiceExtensions.AddWave2fCors` |
| CORS origins are an explicit server allowlist (`CorsSettings:AllowedOrigins` + `AllowCredentials`); shipped defaults are `http://localhost:3000`, `https://localhost:7038`, `https://localhost:5293` (`appsettings.json`). A wildcard origin is rejected at startup — if your dev origin gets CORS errors, it must be added server-side. | `ServiceExtensions`, `appsettings.json` |
| **HTTPS-only gotcha:** the cookie is `Secure`, so browsers only send it over HTTPS. Develop against **`https://localhost:7038`** (accept the dev cert). On `http://localhost:5293` login will *look* successful (200 + body) but the cookie is dropped and refresh/logout silently break. | `AuthController` cookie flags |
| There is **no public signup**: every `/api/users` route requires the `Admin` role. Accounts are created by admins (`POST /api/users`) or the seeded admin. Do not build a registration page. | `UsersController` (`[Authorize(Roles="Admin")]` on the controller) |

### 1.2 Token lifetimes, the `expiresAt` trap, zero-skew refresh

- Access token: **15 min**. Refresh token / cookie: **7 days**
  (`JwtSettings.AccessTokenExpirationMinutes/RefreshTokenExpirationDays`).
- **TRAP:** `TokenResponse.expiresAt` is the **refresh-token** expiry, not the
  access-token expiry (`AuthService`: `ExpiresAt = refreshTokenEntity.Expires`).
  Do not use it to schedule access-token refresh — it will schedule 7 days out.
- The server validates with **`ClockSkew = Zero`**: an access token dies exactly
  at `exp`. Refresh **early**: decode the JWT `exp` claim and refresh at
  `exp − 60–120s`, or proactively every ~14 min. Never wait for a 401 to
  discover expiry on a checkout/payment call.
- Refresh rotation: each `POST /api/auth/refresh-token` revokes the presented
  token and returns a new pair. **Reuse detection:** presenting an already
  rotated token revokes *all* of the user's active tokens and returns 401
  (`AuthService.RefreshTokenAsync`) — treat that 401 as "session compromised,
  force logout", not "retry".
- Max **10 active** refresh tokens per user (`AuthService.MaxActiveRefreshTokens`):
  the 11th login revokes the oldest. Multi-tab is fine (one cookie jar); an
  11th device signs the oldest device out.

### 1.3 `PagedResult<T>` — one envelope for every list

```json
{
  "items": [ … ],
  "totalCount": 127,
  "pageNumber": 1,
  "pageSize": 20,
  "totalPages": 7,
  "hasPrevious": false,
  "hasNext": true
}
```

Rules (`PagedResult<T>` + every service): defaults `pageNumber=1, pageSize=20`;
`pageSize` is **clamped to 100** server-side (larger values silently become 100);
`pageNumber < 1` becomes 1. Drive pagination UI from `totalPages/hasNext`,
not from `items.length`.

### 1.4 `ProblemDetails` + `traceId` — and the exceptions to the rule

The default failure body (`ApiExceptionHandler`, RFC 7807):

```json
{
  "status": 404, "title": "Not Found",
  "detail": "The requested resource was not found.",
  "instance": "/api/products/abc",
  "traceId": "00-4bf9…-01",
  "errors": { "Email": ["'Email' must not be empty."] }
}
```

- `errors` appears **only** on 400 validation failures. `traceId`
  (`Activity.Current.Id ?? HttpContext.TraceIdentifier`) is on every handler
  body — **include it in bug reports**.
- Exception → status map: `ValidationException`→400 (+`errors`),
  `UnauthorizedAccessException`→401, `EmailNotConfirmedException`→403
  (`"Email Not Confirmed"`), `KeyNotFoundException`→404,
  `InvalidOperationException`→409, `AccountLockedException`→423 (`"Locked"`),
  anything else→500 (message only in Development).
- **Three shapes that break the pattern — code defensively:**
  1. `GET /api/orders/{id}` for a non-owner, non-admin returns **empty-body 403**
     (`return Forbid()` in `OrdersController`) — check `status`, there is no
     JSON to parse.
  2. Several GET-by-id routes return **empty-body 404** on unknown id
     (`ProductsController.GetProduct`, `CategoriesController.GetCategory`,
     `EnterpriseInfoController.GetEnterpriseInfo`, `OrdersController.GetOrder`,
     `PaymentsController.RefundOrder` unknown order). Service-thrown 404s
     (users update/delete, order status update, payment attach) *do* carry
     `ProblemDetails`. Never assume `body.title` exists on 404.
  3. `GET /api/cart` with a broken identity returns 401 `{ message: "User
     identity not found" }` (plain object, not `ProblemDetails`) — only
     reachable with a malformed token, but don't crash on it.
  4. `429` bodies are built by the rate limiter, **not** the exception handler:
     `{status, title:"Too Many Requests", detail, instance}` — **no `traceId`**;
     the retry signal is the **`Retry-After` response header** (seconds).

### 1.5 Status → UX-action table

| Status | Meaning here | UX action |
|---|---|---|
| 200 / 201 / 204 | Success (`POST /api/users` returns **200**, not 201 — don't assert 201 globally) | Render / navigate; 204 has no body |
| 400 | Validation (`errors` map), missing refresh cookie, refund without payment intent, bad webhook signature (server-only) | Show field errors from `errors`; for missing-cookie 400, re-login |
| 401 | Missing/expired/invalid JWT, bad credentials, bad/over-attempted OTP, inactive refresh token, **reuse detected**, wrong current/confirm password | Silent-refresh once → retry; still 401 (or reuse) → force logout |
| 403 | Unconfirmed email on login (`ProblemDetails`), or non-owner order access (**empty body**) | Confirm-email prompt / "not your order" screen |
| 404 | Unknown id (body may be empty, §1.4) | Not-found state |
| 409 | Illegal order-status transition, idempotency-key payload mismatch, invalid/expired confirm/reset token | Show `detail`/message; do **not** auto-retry |
| 423 | Locked after ≥5 failed logins, 15-min window | "Try again in 15 minutes" (no countdown precision exposed) |
| 429 | Per-IP budget exhausted; honor `Retry-After` | Back off, then retry once |

### 1.6 Rate limits (per IP, sliding 1-min window)

`auth` **5/min** (`AuthController`: login, refresh, logout, confirm, resend,
change, forgot, reset), `webhook` **60/min** (server-only),
**global 100/min** everything else. Notes: the five auth calls share one
budget — a user hammering "resend code" can lock themselves out of login for
a minute; debounce those buttons. `/health` and `/health/live` are exempt.

### 1.7 Data conventions

- **Currency:** orders are always `"usd"` (`Order.Currency` defaults to it;
  `CreateOrderDto` has no currency field — there is nothing to send).
  Products carry a 3-letter `currency` (default `"usd"`, validated
  `^[A-Za-z]{3}$`).
- **`ImageUrl` is a single string** (`ProductDto.ImageUrl?`, `logoUrl?` on
  enterprise). No gallery array exists — build a single-image uploader/preview.
- **Roles are a list, not flags** (`List<UserRole>`; `UserRoles.EnsureValid`:
  non-empty, distinct, defined). Send `["Customer"]`, never numbers or
  bitmasks. JWT carries one `role` claim per role — decode the access token
  for route guards (roles go stale until the next refresh).
- **Health:** `GET /health` (readiness incl. MongoDB) and `GET /health/live`
  (liveness, always healthy) — both anonymous and rate-limit-exempt. Poll
  `/health/live` for "backend up", `/health` for "backend + DB ready".
- **Lockout/OTP/token TTLs** are server constants (5 fails → 15-min lockout;
  OTP 10 min / 5 attempts; confirm 24 h; reset 1 h) — display them as static
  copy, never trust client clocks for enforcement.

---

## 2. Auth flows (implement in this order)

### 2.1 Login — `POST /api/auth/login` (Anonymous, `auth` budget)

```json
// request
{ "email": "user@example.com", "password": "Secret123", "otpCode": "482913" }
// success → 200 TokenResponse + Set-Cookie: refreshToken
{ "accessToken": "eyJ…", "refreshToken": "…", "expiresAt": "2026-…", "requiresTwoFactor": false }
```

Validation (`LoginRequestValidator`): email required + valid format; password
required (no strength check at login); `otpCode`, if sent, must be 6 digits.
Server order of checks (`AuthService.LoginAsync`): unknown email → 401
(identical to wrong password — no enumeration); active lockout → 423 even with
the right password; wrong password → 401 (counts toward lockout);
unconfirmed email → 403; 2FA branch below.

**2FA branch:** if the account has 2FA on and `otpCode` is absent → `200
{ requiresTwoFactor: true, … }` (empty tokens) **and** an OTP email is sent.
Show the code screen, then POST the *same* email+password **plus** `otpCode`.
6-digit numeric string, 10-min TTL, max 5 attempts — a 6th attempt (or expired
/ wrong code) → 401. Resending = calling login again without `otpCode`.

> Store the access token in memory (never `localStorage`); the refresh token
> is unreadable by design — rely on the cookie. Ignore the `refreshToken`
> *body* field (the cookie is the source of truth).

### 2.2 Silent refresh — `POST /api/auth/refresh-token` (cookie only)

No body, no `Authorization` header — just `credentials: 'include'`.
Missing cookie → 400; unknown/inactive token → 401 (re-login); **revoked-token
reuse → 401 + every active token revoked** (force logout on all tabs).
Success rotates the cookie and returns a fresh `TokenResponse`. Drive this
from an `exp − 90s` timer (§1.2), not from 401s.

### 2.3 Logout — `POST /api/auth/logout` (Auth **and** cookie)

Requires **both** a valid Bearer token (`[Authorize]`) and the cookie
(missing cookie → 400). Revokes the token server-side and clears the cookie.
Frontend: call it, then drop the in-memory token and redirect to login.
(The cleared cookie alone is not enough — your app state still holds the
access token for up to 15 min.)

### 2.4 Confirm email — `POST /api/auth/confirm-email` (Anonymous)

`{ "email", "token" }` (both required; email must be valid format → else 400).
Success → `200 { message }`. Bad/expired/used token → **409** (single-use,
24-h TTL). UX: deep-link `/confirm?email=…&token=…` calls this on mount;
409 means "link expired — request a new one".

### 2.5 Resend confirmation — `POST /api/auth/resend-confirmation`

`{ "email" }` → **always** `200 { message }`, even for unknown or already
confirmed addresses (anti-enumeration — the copy says "if … registered and
unconfirmed"). Never reveal whether the email exists. Debounce: it spends the
shared `auth` budget (§1.6).

### 2.6 Change password — `POST /api/auth/change-password` (Auth)

`{ "currentPassword", "newPassword" }`. Policy (mirrors signup):
**≥8 chars, one upper, one lower, one digit**, and new ≠ current
(`ChangePasswordDtoValidator`). Wrong current → 401. Success → `200
{ message }` **and every refresh token is revoked** — all devices (including
this one) must re-login. UX: after success, route to login, not to a silent
refresh.

### 2.7 Forgot / reset — `POST /api/auth/forgot-password` + `reset-password`

Forgot takes `{ "email" }` → always generic 200 (stores a 1-h single-use
token, emails only if registered). Reset takes `{ "email", "token",
"newPassword" }` (same ≥8/upper/lower/digit policy; token required, else 400):
success → 200 + all sessions revoked (re-login); bad/expired/used token →
**409**. Reset links: `/reset?email=…&token=…`; a 409 means "link invalid or
already used — request a new one".

### 2.8 Lockout, export, purge

- **Lockout:** 5 consecutive bad passwords → 423 for 15 min (counter resets on
  success; expired lockouts clear silently). Show a static "locked, try again
  later" state — remaining time is not exposed.
- **Export** `GET /api/me/export` (Auth, any role) → `MeExportDto{ profile:
  UserDto, orders[], carts[], tokenMetadata[{created, createdByIp, expires,
  revoked, revokedByIp, hasReplacement, isActive}], exportedAt }`. No secrets
  are ever exported (no password hash, no token values). Render as the
  "download my data" JSON.
- **Purge** `POST /api/me/purge` (Auth) `{ "confirmPassword" }` (current
  password required, strength rules do *not* apply) → `200
  { userDeleted, cartsDeleted, ordersPseudonymized }`. Wrong password → 401.
  This is **GDPR erasure**: user doc hard-deleted, carts deleted, orders kept
  for accounting but anonymized (`userId → "deleted:{sha256hex}"`, address
  stripped). It cannot be undone — gate behind typed confirmation, then force
  logout. Contrast: `DELETE /api/users/{id}` (Admin-only) is a recoverable
  soft-delete flag, not erasure.

---

## 3. Endpoint specs by controller

### 3.1 Users — `api/users` (all **Admin-only**, no self-service)

| Method | Path | Body | Success |
|---|---|---|---|
| GET | `/api/users?search=&roles=Customer&roles=Manager&pageNumber=1&pageSize=20` | — | 200 `PagedResult<UserDto>` |
| POST | `/api/users` | `CreateUserDto` (table) | **200** `UserDto` (not 201) |
| PUT | `/api/users/{id}` | `UpdateUserDto` (all-optional) | 200 `UserDto`; unknown id → 404 `ProblemDetails` |
| DELETE | `/api/users/{id}` | — | 204 (soft-delete flag); unknown id → 404 |

`UserDto{id, email, username, firstName?, lastName?, phoneNumber?, roles[],
emailConfirmed, twoFactorEnabled, createdAt}` — passwords/hashes never appear.
`CreateUserDto`: email (valid, ≤256), username (3–50), password
(≥8/upper/lower/digit), first/lastName (≤100), phone (≤20), `roles[]`
(non-empty, defined values — enforced service-side via `UserRoles.EnsureValid`).
`UpdateUserDto`: username? (3–50 if sent), names/phone (lengths as above),
`roles[]?`, `emailConfirmed?`, `twoFactorEnabled?` — omit untouched fields;
role checkboxes need at least one selected. Duplicate email on create →
409 (`"Email already in use"`).

### 3.2 Products — `api/products`

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/api/products?search=&categoryId=&minPrice=&maxPrice=&isActive=true&pageNumber=1&pageSize=20` | Anonymous | **default `isActive=true`** — see below |
| GET | `/api/products/{id}` | Anonymous | 200 `ProductDto`; unknown id → **empty** 404 |
| POST | `/api/products` | Admin, Manager | 201 + `Location`; body table below |
| PUT | `/api/products/{id}` | Admin, Manager | 200; all-optional body; unknown id → 404 |
| DELETE | `/api/products/{id}` | Admin | 204; unknown id → 404 |

`ProductDto{id, name, description, price, currency, stockQuantity, imageUrl?,
categoryId, categoryName? (hydrated), tags[], isActive}`.
`CreateProductDto`: `name` required ≤200; `description` optional ≤2000;
`price` ≥0 (0 = free); `currency` default `"usd"`, 3-letter code;
`stockQuantity` ≥0; `categoryId` required; `imageUrl?` — **single** URL string,
no server validation, send `null` or omit when none; `tags[]` free-form.
`UpdateProductDto`: every field optional incl. `isActive?` — omit what you
don't change.

**`isActive` default asymmetry:** product listing defaults to active-only;
pass `isActive=false` for hidden items (admin view). To show *everything*,
**omit** the param (null = no filter). **[CORRECTS API.md]** Categories have
*no* `isActive` default (null = all) — and no `parentCategoryId` filter
(§3.3). Do not copy the product pattern onto categories.

### 3.3 Categories — `api/categories`

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/api/categories?search=&isActive=&pageNumber=1&pageSize=20` | Anonymous | Sorted Name asc; unknown id → **empty** 404 |
| GET | `/api/categories/{id}` | Anonymous | 200 `CategoryDto` |
| POST | `/api/categories` | Admin, Manager | 201 + `Location` |
| PUT | `/api/categories/{id}` | Admin, Manager | 200 |
| DELETE | `/api/categories/{id}` | Admin | 204 (soft-delete) |

`CategoryDto{id, name, description, parentCategoryId?, isActive}` (entities
default `isActive=true`). `CreateCategoryDto{name req ≤100, description?
≤500, parentCategoryId?}`; `UpdateCategoryDto{name?, description?,
parentCategoryId?, isActive?}` (non-empty-if-sent rules). **[CORRECTS
API.md]:** `GET /api/categories` accepts only `search`, `isActive`,
`pageNumber`, `pageSize` (`CategoryFilterDto` + `CategoryService`) — there is
**no `parentCategoryId` query filter**; a `parentCategoryId` query param is
silently ignored. Build trees client-side from `parentCategoryId` in the
payloads. The `.http` sample sending `"isActive": true` in an update body is
harmless but ignored on create (no such field).

### 3.4 Cart — `api/cart` (Auth; identity from claims, never a body userId)

| Method | Path | Body | Success |
|---|---|---|---|
| GET | `/api/cart` | — | 200 `CartDto{items[{productId, productName, unitPrice, quantity}], total}` (`total` computed server-side) |
| POST | `/api/cart/items` | `AddToCartDto{productId req, quantity 1–99, default 1}` | 200 `CartDto`; unknown product → 404; (quantities validated by `AddToCartDtoValidator`) |
| PUT | `/api/cart/items/{productId}` | **raw JSON number** (`3`, not an object) | 200 `CartDto`; item missing → 404; cart missing → 409 |
| DELETE | `/api/cart/items/{productId}` | — | 200 `CartDto` |
| DELETE | `/api/cart` | — | 204 |

`Content-Type: application/json` with body `3` — `axios.put(url, 3)` /
`fetch(body: JSON.stringify(qty))`. A `{quantity: 3}` object is rejected by
model binding: this shape is versioned, do not "fix" it. **Clamp to 1–99
client-side:** unlike add-to-cart, the update path has **no server range
check** (`CartService` assigns the raw value; the 1–99
`UpdateCartItemDtoValidator` never runs on the `int` body) — so validate
before sending and use DELETE-item for removal (quantity 0 semantics are
undefined server-side).

### 3.5 Orders — `api/orders` (Auth)

`OrderDto{id, userId, items[{productId, productName, unitPrice, quantity}],
totalAmount, currency:"usd", status, stripePaymentIntentId?,
stripeRefundId?, stripeRefundAmount?, shippingAddress, createdAt}`.

- `POST /api/orders` → **201** (also 201 on idempotent replay). Client prices
  are **ignored and re-priced** from MongoDB (`OrderService`): the `unitPrice`
  (≥0) / `productName` fields on each item exist for shape-compat — send
  `{productId, quantity}` plus dummy `0`/`""`. `quantity` 1–99; empty items →
  409; unknown/inactive product → 404/409; insufficient stock → 409.
  `shippingAddress{street ≤200, city/state ≤100, zipCode ≤20, country ≤100}` —
  all required. `stripePaymentMethodId?` optional. Currency is always `usd`
  (no field to send). On success: status `Pending`, stock decremented, cart
  deleted *after* the order insert (insert-first ordering — a failed cart
  clear never loses the order).
- **Idempotency:** send `Idempotency-Key: <uuid>` header (≤100 chars) — the
  header **wins** over `CreateOrderDto.idempotencyKey`. Same (user, key) +
  same payload → 201 with the existing order (safe retry). Same key +
  different items/address → **409** (`"already used for a different order
  payload"` — comparison ignores client prices/names). Generate one UUID per
  checkout *attempt*; reuse it only for retries of that exact payload.
- `GET /api/orders/my?pageNumber=1&pageSize=20` → own orders, newest first.
- `GET /api/orders/{id}` → owner or Admin; other authenticated users get
  **empty-body 403**; unknown id → empty-body 404.
- `PATCH /api/orders/{id}/status` (Admin) `{status}` → 200; illegal
  transition → 409; unknown id → 404 `ProblemDetails`. The guarded machine
  (`OrderService.AllowedTransitions`) — render only legal next-states:

| From ↓ | Legal next states |
|---|---|
| `Pending` | `PaymentReceived`, `Cancelled` |
| `PaymentReceived` | `Processing`, `Cancelled`, `Refunded`, `PartiallyRefunded` |
| `Processing` | `Shipped`, `Cancelled`, `Refunded`, `PartiallyRefunded` |
| `Shipped` | `Delivered`, `PartiallyRefunded` |
| `Delivered` | `Refunded` |
| `PartiallyRefunded` | `Refunded` |
| `Refunded`, `Cancelled` | terminal (nothing) |

### 3.6 Enterprise info — `api/enterprise-info` (explicit route, not `[controller]`)

- `GET` (Anonymous) → 200 `EnterpriseInfoDto{companyName, address, phone,
  email, logoUrl?, aboutUs?, termsAndConditions?, privacyPolicy?}`; **404
  (empty body) when never configured** — treat as "store setup incomplete".
- `PUT` (Admin) → **always 200** (singleton **upsert**: creates or updates).
  All fields optional with lengths (company ≤200, address ≤500, phone ≤20,
  email valid ≤256, `logoUrl` absolute http(s) ≤2048, about ≤4000,
  terms/privacy ≤8000). Because it is an upsert, the admin form doubles as
  the first-time setup form.

### 3.7 Payments — `api/payments` (Auth; refund Admin-only)

- `POST /api/payments/intent` `{amount (major units, dollars), currency="usd",
  customerId?, orderId?}` → 200
  `PaymentIntentResultDto{paymentIntentId, clientSecret, amount, currency,
  status, refundId?}`. With `orderId`: Stripe uses deterministic key
  `order:{orderId}:intent`, the order id lands in Stripe metadata, and the
  intent id is persisted on the order (retry path for checkouts whose
  post-commit linkage failed — checkout stays `Pending` with null intent and
  the client retries here). Unknown `orderId` → 404.
- `POST /api/payments/orders/{id}/refund` (Admin) body `RefundPaymentDto{amount?}`
  — the body is **nullable**: send `{}` or nothing for a **full** refund,
  `{amount}` for partial. Stripe key: `order:{id}:refund:{amount ?? "full"}`
  (amount formatted invariant `0.##`). Full → status `Refunded` **+ stock
  restored**; partial → `PartiallyRefunded`, **no stock restore** (partial =
  discount/adjustment on kept goods, not a return — ADR-0007). Unknown order
  → empty 404; order with no intent → 400 `ProblemDetails`; illegal refund
  transition (e.g. `Pending → Refunded`) → 409. Only offer refund when the
  machine (§3.5) allows the target state.

### 3.8 Stripe webhook — `api/stripe/webhook` (**server-only, never call from the frontend**)

Anonymous + `Stripe-Signature` header, raw Stripe JSON body, `webhook`
rate budget. Mapping: `payment_intent.succeeded` → `PaymentReceived`;
`payment_intent.payment_failed` / `charge.refunded` → `Cancelled`; unknown
types acked without state change; duplicate event ids (30-day dedup) acked
`{duplicate: true}` with no state change. **Always 200** on valid signature —
even when the order transition fails (logged server-side so Stripe never
retry-storms); bad signature → 400. Frontend consequence: after Stripe.js
confirms payment, **poll** `GET /api/orders/{id}` until `PaymentReceived`
(webhook delivery is eventually consistent), with a timeout and a "still
processing" state — there is no synchronous success callback from this API.

---

## 4. Stripe.js handoff via `clientSecret`

Backend owns intents and linkage; the frontend only confirms:

1. Checkout: `POST /api/orders` (+ `Idempotency-Key`) → order `Pending`.
2. `POST /api/payments/intent { amount: order.totalAmount, currency: "usd",
   orderId: order.id }` → take **`clientSecret`** (and `paymentIntentId` for
   support display). Amount echoed back in dollars.
3. `stripe.confirmCardPayment(clientSecret, { payment_method: { card:
   cardElement } })` (Stripe Elements). Never send card details to this API —
   no endpoint accepts them.
4. On `paymentIntent.status === "succeeded"`, poll `GET /api/orders/{id}`
   (owner-readable) until `status === "PaymentReceived"` or timeout; the
   webhook (§3.8) performs the transition. On timeout show "payment received,
   order updating" — do not re-post the order (idempotency key makes a retry
   safe anyway).
5. Admin refunds (§3.7) surface as `stripeRefundId/stripeRefundAmount` on the
   order; `status` tells full vs partial.

---

## 5. Implementation checklist (build order)

1. **HTTP client**: base URL selector (HTTPS dev), `credentials: 'include'`
   default, JSON headers, `ProblemDetails` parser tolerating **empty bodies**
   (404/403 cases, §1.4), `Retry-After` reader for 429 with backoff.
2. **Auth shell**: in-memory token, login form (+2FA code step on
   `requiresTwoFactor`), `exp−90s` silent-refresh timer, single-flight
   401→refresh→retry-once, reuse-401 → global logout; remember the
   `expiresAt` trap (§1.2) and HTTPS-only cookie (§1.1).
3. **Guards**: role guard from decoded `role` claims (Customer/Manager/Admin
   strings); admin-only users/enterprise/refund/status routes; owner-or-admin
   order detail (empty-403 state).
4. **Catalog (anonymous)**: product list (mind the `isActive=true` default),
   product detail (empty-404), category list (**no parent filter** — tree
   client-side), enterprise public view (404 = unconfigured).
5. **Cart**: add (1–99), update (raw number, client-clamped 1–99), remove,
   clear; handle 409-missing-cart by re-adding.
6. **Checkout**: address form (lengths §3.5), UUID `Idempotency-Key` per
   attempt, server-total display (ignore echoed client math), Stripe Elements
   confirm, poll-to-`PaymentReceived`.
7. **Account**: profile from `GET /api/me/export`, change-password (policy +
   forced re-login), GDPR export download + purge (typed confirm, forced
   logout).
8. **Admin**: user CRUD (200-on-create, roles list, soft-delete), product/
   category CRUD, order-status machine UI (legal transitions only), refund UI
   (full vs partial copy, stock-restore note, transition-gated), enterprise
   upsert form (doubles as setup).

---

## 6. React appendix

### 6.1 `AuthContext` — silent-refresh scheduler, single-flight 401→retry-once

```tsx
// Token lives in memory only. exp comes from decoding the JWT payload.
import { createContext, useContext, useEffect, useRef, useState } from 'react';

type AuthState = { accessToken: string | null; roles: string[] };
const AuthCtx = createContext<{
  auth: AuthState; login: (email: string, password: string, otpCode?: string) => Promise<{ twoFactor: boolean }>;
  logout: () => Promise<void>; getToken: () => Promise<string | null>;
}>({} as any);

function rolesFrom(token: string): string[] {
  const payload = JSON.parse(atob(token.split('.')[1]));
  const r = payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'];
  return Array.isArray(r) ? r : r ? [r] : [];
}
function msUntilRefresh(token: string) {
  const exp = JSON.parse(atob(token.split('.')[1])).exp * 1000;
  return Math.max(0, exp - Date.now() - 90_000); // exp − 90s (§1.2)
}

let inflight: Promise<string | null> | null = null; // single-flight refresh

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [auth, setAuth] = useState<AuthState>({ accessToken: null, roles: [] });
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const schedule = (token: string) => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => void refresh(), msUntilRefresh(token));
  };
  const refresh = async (): Promise<string | null> => {
    if (!inflight) inflight = (async () => {
      try {
        const res = await fetch('/api/auth/refresh-token', { method: 'POST', credentials: 'include' });
        if (!res.ok) { setAuth({ accessToken: null, roles: [] }); return null; } // 401 (incl. reuse) → logged out
        const body = await res.json();
        setAuth({ accessToken: body.accessToken, roles: rolesFrom(body.accessToken) });
        schedule(body.accessToken);
        return body.accessToken as string;
      } finally { inflight = null; }
    })();
    return inflight;
  };
  // 401 → refresh → retry ONCE; reuse-401 lands here as null → caller logs out.
  const getToken = async () => auth.accessToken ?? refresh();

  const login = async (email: string, password: string, otpCode?: string) => {
    const res = await fetch('/api/auth/login', {
      method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password, ...(otpCode ? { otpCode } : {}) }),
    });
    if (!res.ok) throw await toApiError(res);
    const body = await res.json();
    if (body.requiresTwoFactor) return { twoFactor: true };
    setAuth({ accessToken: body.accessToken, roles: rolesFrom(body.accessToken) });
    schedule(body.accessToken);
    return { twoFactor: false };
  };
  const logout = async () => {
    if (auth.accessToken) {
      await fetch('/api/auth/logout', {
        method: 'POST', credentials: 'include',
        headers: { Authorization: `Bearer ${auth.accessToken}` },
      }).catch(() => {});
    }
    if (timer.current) clearTimeout(timer.current);
    setAuth({ accessToken: null, roles: [] });
  };
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);
  return <AuthCtx.Provider value={{ auth, login, logout, getToken }}>{children}</AuthCtx.Provider>;
}
export const useAuth = () => useContext(AuthCtx);
```

### 6.2 `useApi` fetcher — `credentials: 'include'`, empty-body tolerance, 429 backoff

```ts
export class ApiError extends Error {
  constructor(public status: number, public title: string, public errors?: Record<string, string[]>, public traceId?: string) {
    super(`${status} ${title}`);
  }
}
export async function toApiError(res: Response): Promise<ApiError> {
  const text = await res.text();
  if (!text) return new ApiError(res.status, res.status === 403 ? 'Forbidden' : 'Not Found'); // empty 403/404 (§1.4)
  try {
    const b = JSON.parse(text);
    return new ApiError(res.status, b.title ?? 'Error', b.errors, b.traceId);
  } catch { return new ApiError(res.status, text); }
}

export async function api<T>(getToken: () => Promise<string | null>, path: string, init: RequestInit = {}, retry = true): Promise<T> {
  const token = await getToken();
  const doFetch = (t: string | null) => fetch(path, {
    ...init, credentials: 'include',
    headers: { 'Content-Type': 'application/json', ...(t ? { Authorization: `Bearer ${t}` } : {}), ...(init.headers ?? {}) },
  });
  let res = await doFetch(token);
  if (res.status === 401 && retry && token) {
    // Single-flight refresh then retry-once. (A global event/redirect on
    // null handles reuse-detection logout; wire to useAuth().logout.)
    const next = await getToken();
    if (next && next !== token) res = await doFetch(next);
  }
  if (res.status === 429) {
    const wait = Number(res.headers.get('Retry-After') ?? '5'); // seconds (§1.4.4)
    await new Promise(r => setTimeout(r, Math.min(wait, 60) * 1000));
    if (retry) return api<T>(getToken, path, init, false);
  }
  if (res.status === 204) return undefined as T;
  if (!res.ok) throw await toApiError(res);
  return res.json() as Promise<T>;
}
```

### 6.3 Role route guard

```tsx
import { Navigate } from 'react-router-dom';
import { useAuth } from './auth';
export function RequireRoles({ roles, children }: { roles: string[]; children: JSX.Element }) {
  const { auth } = useAuth();
  if (!auth.accessToken) return <Navigate to="/login" replace />;
  // roles are exact strings: "Customer" | "Manager" | "Admin" (§1.7)
  if (!roles.some(r => auth.roles.includes(r))) return <Navigate to="/forbidden" replace />;
  return children;
}
// usage: <RequireRoles roles={['Admin']}><UsersPage /></RequireRoles>
// order detail: owner-or-admin is enforced server-side (empty 403, §1.4) —
// render the API's 403 as "not your order", no client id check needed.
```

### 6.4 Validation hints mirroring server rules

Pre-validate with the same limits so 400s are rare (server still decides):

- email: required + format; username 3–50; password ≥8 + upper + lower +
  digit (`/^(?=.*[A-Z])(?=.*[a-z])(?=.*\d).{8,}$/`); change-password additionally
  new ≠ current.
- product name ≤200, description ≤2000, price ≥0, stock ≥0, category required,
  currency 3 letters; image: single URL input (no gallery).
- cart/order quantity 1–99 (`/add` validated server-side; `/update` **not** —
  clamp before sending the raw number, §3.4).
- address: street ≤200, city/state/country ≤100, zip ≤20, all required.
- category name ≤100, description ≤500; enterprise lengths §3.6.
- idempotency: `crypto.randomUUID()` per checkout attempt into the
  `Idempotency-Key` header; surface 409 mismatch as "duplicate order attempt —
  check your orders" rather than retrying.

### 6.5 Stripe Elements minimal wiring (§4)

```tsx
import { loadStripe } from '@stripe/stripe-js';
import { Elements, CardElement, useStripe, useElements } from '@stripe/react-stripe-js';
const stripePromise = loadStripe(import.meta.env.VITE_STRIPE_PUBLISHABLE_KEY);

function CheckoutForm({ orderId, total }: { orderId: string; total: number }) {
  const stripe = useStripe(), elements = useElements();
  const [state, setState] = useState<'idle' | 'processing' | 'polling' | 'done' | 'error'>('idle');
  const pay = async () => {
    setState('processing');
    const intent = await api<{ clientSecret: string }>(getToken, '/api/payments/intent', {
      method: 'POST', body: JSON.stringify({ amount: total, currency: 'usd', orderId }),
    });
    const card = elements!.getElement(CardElement)!;
    const result = await stripe!.confirmCardPayment(intent.clientSecret, { payment_method: { card } });
    if (result.error) { setState('error'); return; }
    setState('polling'); // webhook applies PaymentReceived asynchronously (§3.8)
    const deadline = Date.now() + 30_000;
    while (Date.now() < deadline) {
      const order = await api<{ status: string }>(getToken, `/api/orders/${orderId}`);
      if (order.status === 'PaymentReceived') { setState('done'); return; }
      await new Promise(r => setTimeout(r, 2000));
    }
    setState('error'); // show "payment received, order updating" (§3.8)
  };
  return (<><CardElement /><button onClick={pay} disabled={!stripe || state === 'processing' || state === 'polling'}>Pay</button></>);
}
```

> Framework-agnostic core (§1–§5) applies to any client. The React snippets
> above are one idiomatic wiring; keep the state machine (token in memory,
> exp-based refresh, retry-once, empty-body tolerance) whatever you build with.

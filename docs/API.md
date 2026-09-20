# API.md — VirtualStore HTTP reference

Base URL (dev): `https://localhost:7038` · Interactive docs: `/scalar/v1` (dev-only, JWT via **Authorize** button — wired by `BearerSecuritySchemeTransformer`).
**37 routes**: 36 controller endpoints + `GET /health`. Auth column: `Anonymous` | `Auth` (any JWT) | role list.

## Envelope & errors

Success bodies are the DTO named per route. Failures use RFC 7807 `ProblemDetails`:

```json
{
  "title": "Not Found",
  "status": 404,
  "detail": "The requested resource was not found.",
  "instance": "/api/products/abc",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0b6a7a2d-01",
  "errors": { "Email": ["'Email' must not be empty."] }
}
```

`errors` appears only for 400 validation failures. Mapping (`ApiExceptionHandler`): FluentValidation → **400**, `UnauthorizedAccessException` → **401**, `EmailNotConfirmedException` → **403** (`"Email Not Confirmed"`), `KeyNotFoundException` → **404**, `InvalidOperationException` → **409**, `AccountLockedException` → **423** (`"Locked"`), unhandled → **500** (message details only in Development). `traceId` = `Activity.Current.Id ?? HttpContext.TraceIdentifier` — include it in bug reports.

| Status | When |
|---|---|
| 400 | Validation failure (`errors` map), missing refresh cookie, order without payment intent on refund, bad webhook signature |
| 401 | Missing/invalid/expired JWT, bad credentials, bad/locked OTP, inactive refresh token, wrong current password |
| 403 | Authenticated but not owner and not `Admin` (`GET /api/orders/{id}`); login with unconfirmed email (`Email Not Confirmed`) |
| 404 | Unknown id (`KeyNotFoundException`), missing enterprise info |
| 409 | Illegal order-status transition, order/cart state conflicts, invalid/expired confirm/reset token |
| 423 | Account temporarily locked after ≥ 5 failed logins (`Locked`, 15 min window) |
| 429 | **Reserved — not emitted yet.** Rate limiting is in progress (wave 2f); clients SHOULD handle 429 with `Retry-After` for forward compatibility |
| 500 | Unexpected error |

## Auth — `api/auth`

| Method | Path | Auth | Body | Responses |
|---|---|---|---|---|
| POST | `/api/auth/login` | Anonymous | `{ "email": "string", "password": "string", "otpCode?": "string" }` | `200 TokenResponse{accessToken, refreshToken, expiresAt, requiresTwoFactor}` + `refreshToken` HttpOnly/Secure/SameSite=Strict cookie (7 d). If 2FA enabled and no `otpCode`: `200 { requiresTwoFactor: true }` and OTP emailed. `401` bad credentials/OTP. `403` email unconfirmed. `423` account locked (≥ 5 failures, 15 min) |
| POST | `/api/auth/refresh-token` | Anonymous (cookie) | — (reads `refreshToken` cookie) | `200 TokenResponse` + rotated cookie (old token revoked, `ReplacedByToken` linked). `400` cookie missing. `401` invalid/inactive/**reuse detected** (reuse revokes all active tokens) |
| POST | `/api/auth/logout` | Auth | — (reads `refreshToken` cookie) | `200 { message }` + cookie cleared. `400` cookie missing |
| POST | `/api/auth/confirm-email` | Anonymous | `{ "email": "string", "token": "string" }` | `200 { message }`. `409` invalid/expired token. `400` validation |
| POST | `/api/auth/resend-confirmation` | Anonymous | `{ "email": "string" }` | ALWAYS `200 { message }` (generic — no enumeration; sends only when registered + unconfirmed) |
| POST | `/api/auth/change-password` | Auth | `{ "currentPassword": "string", "newPassword": "string" }` (new: min 8, upper+lower+digit, ≠ current) | `200 { message }` + all refresh tokens revoked. `401` wrong current. `400` validation |
| POST | `/api/auth/forgot-password` | Anonymous | `{ "email": "string" }` | ALWAYS `200 { message }` (generic — no enumeration; stores 1 h token + emails only when registered) |
| POST | `/api/auth/reset-password` | Anonymous | `{ "email": "string", "token": "string", "newPassword": "string" }` | `200 { message }` + all refresh tokens revoked. `409` invalid/expired token. `400` validation |

## Users — `api/users` (all **Admin-only**)

| Method | Path | Auth | Body | Responses |
|---|---|---|---|---|
| GET | `/api/users?search?&roles?=&pageNumber=1&pageSize=20` | Admin | — | `200 PagedResult<UserDto>` |
| POST | `/api/users` | Admin | `CreateUserDto{email, username, password, firstName?, lastName?, phoneNumber?, roles[]}` | `200 UserDto` (password stored as BCrypt hash). `400` validation |
| PUT | `/api/users/{id}` | Admin | `UpdateUserDto{username?, firstName?, lastName?, phoneNumber?, roles[]?, emailConfirmed?, twoFactorEnabled?}` | `200 UserDto`. `404` unknown id |
| DELETE | `/api/users/{id}` | Admin | — | `204`. `404` unknown id |

> Permission note: there is no self-service registration — every users endpoint requires `Admin`.

## Products — `api/products`

| Method | Path | Auth | Body | Responses |
|---|---|---|---|---|
| GET | `/api/products?search?&categoryId?&minPrice?&maxPrice?&isActive=true&pageNumber=1&pageSize=20` | Anonymous | — | `200 PagedResult<ProductDto>` |
| GET | `/api/products/{id}` | Anonymous | — | `200 ProductDto`. `404` |
| POST | `/api/products` | Admin, Manager | `CreateProductDto{name, description, price, currency="usd", stockQuantity, imageUrl?, categoryId, tags[]}` | `201 ProductDto` (+ `Location`) |
| PUT | `/api/products/{id}` | Admin, Manager | `UpdateProductDto` (all-optional incl. `isActive?`) | `200 ProductDto`. `404` |
| DELETE | `/api/products/{id}` | Admin | — | `204`. `404` |

## Cart — `api/cart` (all Authenticated, current user from claims)

| Method | Path | Auth | Body | Responses |
|---|---|---|---|---|
| GET | `/api/cart` | Auth | — | `200 CartDto{items[], total}`. `401` identity missing |
| POST | `/api/cart/items` | Auth | `AddToCartDto{productId, quantity=1}` | `200 CartDto`. `400/404/409` per product state |
| PUT | `/api/cart/items/{productId}` | Auth | **raw JSON number** (e.g. `3`) | `200 CartDto` |
| DELETE | `/api/cart/items/{productId}` | Auth | — | `200 CartDto` |
| DELETE | `/api/cart` | Auth | — | `204` |

## Orders — `api/orders` (all Authenticated)

| Method | Path | Auth | Body | Responses |
|---|---|---|---|---|
| POST | `/api/orders` | Auth | `CreateOrderDto{items[{productId, quantity}], shippingAddress{street, city, state, zipCode, country}, stripePaymentMethodId?}` — client prices ignored | `201 OrderDto` (server-priced `totalAmount`, status `Pending`, stock decremented, cart cleared). `400/409` bad items/state. `404` unknown product |
| GET | `/api/orders/my?pageNumber=1&pageSize=20` | Auth | — | `200 PagedResult<OrderDto>` (own orders, newest first) |
| GET | `/api/orders/{id}` | Auth | — | `200 OrderDto` (owner or Admin). `403` otherwise. `404` |
| PATCH | `/api/orders/{id}/status` | Admin | `UpdateOrderStatusDto{status}` (`Pending\|PaymentReceived\|Processing\|Shipped\|Delivered\|Cancelled\|Refunded\|PartiallyRefunded`) | `200 OrderDto`. `409` illegal transition. `404` |

## Enterprise info — `api/enterprise-info`

| Method | Path | Auth | Body | Responses |
|---|---|---|---|---|
| GET | `/api/enterprise-info` | Anonymous | — | `200 EnterpriseInfoDto`. `404` not configured |
| PUT | `/api/enterprise-info` | Admin | `UpdateEnterpriseInfoDto` (all-optional: companyName, address, phone, email, logoUrl, aboutUs, termsAndConditions, privacyPolicy) | `200 EnterpriseInfoDto` (upsert singleton). `400` validation |

## Payments — `api/payments` (all Authenticated)

| Method | Path | Auth | Body | Responses |
|---|---|---|---|---|
| POST | `/api/payments/intent` | Auth | `CreatePaymentIntentDto{amount, currency="usd", customerId?, orderId?}` | `200 PaymentIntentResultDto{paymentIntentId, clientSecret, amount, currency, status, refundId?}` (`orderId` stored in Stripe metadata; when supplied the intent uses the deterministic key `order:{orderId}:intent` and the intent id is persisted on the order — retry path for checkouts whose post-commit linkage failed). `400`. `404` unknown `orderId` |
| POST | `/api/payments/orders/{id}/refund` | Admin | `RefundPaymentDto{amount?}` (optional; omitted = full refund) | `200 PaymentIntentResultDto` (refund, incl. `refundId`). Full refund → order `Refunded` + stock restored + `stripeRefundId/Amount` persisted; partial → `PartiallyRefunded`, no stock restore (partial = discount/adjustment, not a return). Idempotency key `order:{id}:refund:{amount ?? "full"}`. `404` unknown order. `400` (ProblemDetails) order has no payment intent. `409` illegal refund transition |

## Stripe webhook — `api/stripe/webhook`

| Method | Path | Auth | Body | Responses |
|---|---|---|---|---|
| POST | `/api/stripe/webhook` | Anonymous (**`Stripe-Signature` header**) | raw Stripe JSON | `200 StripeWebhookResultDto{eventType, paymentIntentId?, orderId?, succeeded, duplicate}`. `400` (ProblemDetails) bad signature. Mapping: `payment_intent.succeeded` → `PaymentReceived`; `payment_intent.payment_failed`/`charge.refunded` → `Cancelled`; unknown types acked without state change. Duplicate Stripe event ids are acked with `duplicate: true` and no state change (30-day dedup window). Webhook **always answers 200** on valid signature even if the order update fails or the handler throws |

## Categories — `api/categories`

| Method | Path | Auth | Body | Responses |
|---|---|---|---|---|
| GET | `/api/categories?search?&parentCategoryId?&pageNumber=1&pageSize=20` | Anonymous | — | `200 PagedResult<CategoryDto>` |
| GET | `/api/categories/{id}` | Anonymous | — | `200 CategoryDto`. `404` |
| POST | `/api/categories` | Admin, Manager | `CreateCategoryDto{name, description?, parentCategoryId?}` | `201 CategoryDto` (+ `Location`) |
| PUT | `/api/categories/{id}` | Admin, Manager | `UpdateCategoryDto{name?, description?, parentCategoryId?}` | `200 CategoryDto`. `404` |
| DELETE | `/api/categories/{id}` | Admin | — | `204` (soft-delete). `404` |

## Health

| Method | Path | Auth | Responses |
|---|---|---|---|
| GET | `/health` | Anonymous | `200` Healthy (includes MongoDB `ready` check) |

`PagedResult<T>` shape: `{ items[], totalCount, pageNumber, pageSize, totalPages, hasPrevious, hasNext }` (`pageSize` clamped to 100).

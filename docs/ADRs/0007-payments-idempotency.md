# ADR 0007 — Payments hardening: intent linkage, idempotency, refund states, webhook dedup

- Status: Accepted
- Date: 2026-09-20 (wave 3c, Epic B)

## Context

Checkout became transactional with order idempotency keys (ADR-0006), but payments
had four gaps:

1. No `Refunded`/`PartiallyRefunded` order states — refunds were invisible to the
   state machine (`charge.refunded` webhooks mapped to `Cancelled`).
2. Stripe calls carried no idempotency keys — client retries could double-create
   intents or refunds.
3. The Stripe payment intent was never linked to the order server-side — the
   `orderId` metadata existed only when the caller supplied it, and
   `Order.StripePaymentIntentId` stayed null after checkout.
4. Webhook redeliveries (Stripe retries until 2xx) could re-apply transitions, and
   any unhandled exception in the webhook path returned 5xx, causing retry storms.

## Decision

- **Refund states.** `OrderStatus` gains `Refunded` + `PartiallyRefunded`.
  `AllowedTransitions` extends to:
  `PaymentReceived|Processing → Refunded`,
  `PaymentReceived|Processing|Shipped → PartiallyRefunded` (partial only),
  `PartiallyRefunded → Refunded`, `Delivered → Refunded`.
  `Shipped → Refunded` is deliberately absent: a shipped order is refunded via a
  partial adjustment first, then a full refund. `Refunded`/`Cancelled` are terminal.
- **Deterministic Stripe idempotency keys** (`Application/Common/StripeIdempotency`):
  intent = `order:{orderId}:intent`, refund = `order:{orderId}:refund:{amount ?? full}`
  (amount formatted invariantly `0.##`). Keys are caller-computed — no key state is
  persisted (Stripe retains keys 24h). `CreatePaymentIntentAsync`/`RefundPaymentAsync`
  accept an optional key and pass `RequestOptions { IdempotencyKey = key }`.
- **Intent↔order linkage, never inside the transaction.** After the checkout
  transaction commits, `OrderService` (optional `IStripePaymentService`) creates the
  intent with `metadata { orderId }` + deterministic key, then persists
  `StripePaymentIntentId` via `UpdateAsync`. Stripe I/O never enlists in the mongo
  transaction. On Stripe failure the order stays `Pending` with a null intent id;
  the client retries via `POST /api/payments/intent { orderId }`, which uses the
  same key/metadata and persists the intent id (`AttachPaymentIntentAsync`).
- **Refund application** (`ApplyRefundAsync`): full → `Refunded` + stock restore
  (each `OrderItem.Quantity` added back via the product repo; missing products are
  skipped with a warning); partial → `PartiallyRefunded` with NO stock restore —
  a partial is a discount/adjustment on kept goods, not a return. Refund id/amount
  persist on `Order.StripeRefundId`/`StripeRefundAmount` (additive; mirrored on
  `OrderDto`; `PaymentIntentResultDto.RefundId` carries the `re_…` id).
  One partial is supported per order: `PartiallyRefunded → PartiallyRefunded` is not
  a legal transition (second partial yields 409; follow with a full refund).
- **Webhook dedup.** New `ProcessedWebhookEvent { EventId unique, Type, ReceivedAt }`
  entity + `ux_webhookevent_eventId` unique index + 30-day TTL index
  `ttl_webhookevent_receivedAt`. `HandleWebhookEventAsync` does check-then-insert
  (duplicate-key race → duplicate); duplicates return `{ Duplicate: true }` and the
  controller acks 200 with no state change. The controller body is wrapped in a
  catch-all (unexpected `Exception` → log + 200 with a warning result) so Stripe
  never retry-storms on 5xx. Signature failures stay 400 with a `ProblemDetails` body.
- **Rate limiting.** New `webhook` policy (60/min per IP, same sliding-window shape
  as `auth`) in the wave-2f region; `StripeWebhookController` moves to it.
  `AuthController` stays on `auth` (5/min).
- **ProblemDetails consistency.** Legacy `BadRequest(new { message })` shapes in
  `PaymentsController.RefundOrder` (missing intent), `StripeWebhookController`
  (bad signature), and `AuthController` (missing refresh cookie) now return
  `BadRequest(new ProblemDetails { Status = 400, Title = "Bad Request", … })`.

## Consequences

- (+) Retried intents/refunds collapse into single Stripe operations; retried
  checkouts link the same intent key.
- (+) Refund state is explicit and stock semantics are documented (full restores,
  partial does not).
- (+) Redelivered webhooks are no-ops; unexpected handler failures ack 200.
- (−) Stripe keys live only 24h — replays after expiry could double-execute at the
  Stripe level (order-level guards still prevent duplicate orders).
- (−) Soft-deleted webhook records still occupy the unique index until TTL expiry;
  acceptable for a 30-day dedup window.
- (−) `charge.refunded` webhooks still map to `Cancelled` (unchanged legacy mapping);
  refund-driven transitions go through `ApplyRefundAsync`. Aligning the webhook
  mapping to `Refunded` is a follow-up.

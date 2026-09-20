# ADR 0004 — Server-side pricing & guarded status machine

- Status: Accepted
- Date: 2026-09-20 (waves 1–2)

## Context

`CreateOrderDto` arrives with client-side items that could carry tampered prices; order status was updatable without transition rules, and payment callbacks could force arbitrary states.

## Decision

- `OrderService.CreateOrderAsync` **re-prices every line from MongoDB** (`Product.Price`), validates active/stock, decrements stock, totals server-side, creates the order `Pending`, and only then clears the cart (order insert precedes cart delete so failures never lose the order).
- Status changes funnel through `AllowedTransitions` (`Pending → PaymentReceived → Processing → Shipped → Delivered`, `Cancelled` from the first three); violations throw `InvalidOperationException` → 409. Only `Admin` may PATCH status; the Stripe webhook uses the same guard (`succeeded → PaymentReceived`, `failed/refunded → Cancelled`, unknown types acked with no change, always HTTP 200).

## Consequences

- (+) Price tampering is structurally impossible; stock oversell is checked per line.
- (+) Webhook and admin paths cannot diverge — one machine, one guard.
- (−) No reservation/transaction across product-stock and order insert (separate `UpdateAsync` calls) — oversell under high concurrency is possible; true transactions are future work.
- (−) Partial-failure modes (stock decremented, later line fails) need compensation — currently surfaces as 409 with partially-updated stock; acceptable at current scale.

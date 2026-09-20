# ADR 0006 — Transactional checkout & order idempotency keys

- Status: Accepted
- Date: 2026-09-20 (wave 3b, Epic A)

## Context

`OrderService.CreateOrderAsync` ran validate → stock-decrement → order-insert → cart-clear as
separate driver calls with no transaction (ADR-0004 accepted the oversell/partial-failure risk as
future work). Under concurrency two checkouts could both pass the stock check and oversell, and a
crash between the stock update and the order insert left decremented stock with no order.
Separately, client retries of `POST /api/orders` (timeout, double-click, replayed webhook-side
calls) created duplicate orders — there was no idempotency mechanism. Order history
(`GetUserOrdersAsync`) and category listing (`CategoryService.GetPagedAsync`) also paged
in-memory (`FindAsync`/`GetAllAsync` + `Skip`/`Take`), ignoring the 100-cap for large datasets.

Single-node replica set is now guaranteed (compose/CI/Testcontainers use the
`mongodb-community-server` image), so multi-document transactions are available everywhere.

## Decision

- **Ambient session, no `IRepository` changes.** `MongoDbContext` (singleton, shared by all scoped
  repos) gains a public `Client` property, an `AsyncLocal`-backed `CurrentSession`, and
  `TransactAsync(Func<Task> work, CancellationToken ct)` (fresh session +
  `WithTransactionAsync`, sets/clears the ambient session, re-entrant). `MongoRepository<T>`
  passes `CurrentSession` into every driver call via the session-accepting overloads only when a
  session is present — zero behavior change outside transactions, and `IRepository<T>` is
  untouched so all existing Moq setups keep compiling.
- **Atomic checkout.** `CreateOrderAsync` runs validate + decrement + insert + cart-clear inside
  `TransactAsync` (method `CancellationToken` forwarded everywhere, CT overloads throughout the
  service). Commit-conflict retry inside `WithTransactionAsync` covers oversell: concurrent
  checkouts serialize on the documents, losers re-run and fail the stock check with 409. **No
  reservation/TTL scheme** — rejected as extra state machinery for a problem transactions solve
  directly at current scale.
- **Idempotency-key replay.** Optional `IdempotencyKey` on `Order` + `CreateOrderDto`; the
  controller prefers the `Idempotency-Key` header over the body value. Same `(userId, key)`
  replays return the existing order (check-then-act inside the transaction, plus a
  `MongoWriteException` duplicate-key catch → fetch + return the winner for lost races).
  Same key with a different payload (items/address compared, order-sensitive, prices ignored
  since the server re-prices) throws `InvalidOperationException` → 409. Backed by unique sparse
  index `ux_order_userIdempotency` on `(UserId, IdempotencyKey)`; null keys are omitted from the
  document (`BsonIgnoreIfNull`) so key-less orders never collide.
- **Server-side paging.** `GetUserOrdersAsync` and `CategoryService.GetPagedAsync` go through
  `PagedAsync`/`CountAsync` (100-cap honored; category keeps `Name` asc, orders keep
  `CreatedAt` desc). DTO shapes unchanged.

## Consequences

- (+) Oversell window closed without reservation state; partial checkout failures roll back
  instead of stranding decremented stock.
- (+) Safe client retries: same key returns the same order; key reuse across different orders
  fails closed with 409 instead of silently duplicating.
- (+) No repository interface churn: the ambient-session pattern confines transaction awareness
  to `MongoDbContext` + `MongoRepository` internals.
- (−) Transaction aborts surface as `MongoException` → 500 via `ApiExceptionHandler`.
  No new exception types were introduced; mapping aborts to 409/503 with retry guidance is a
  follow-up (also: `WithTransactionAsync` may re-execute the transactional delegate on
  transient errors — the flow tolerates it via the idempotency check, but handlers must stay
  side-effect-light inside the transaction).
- (−) Soft-deleted orders still occupy the unique index (sparse ≠ partial): reusing a key whose
  order was soft-deleted returns the deleted order's payload check path. Mitigation (partial
  index on `IsDeleted == false`) is a follow-up if key reuse after deletion matters.
- (−) Payload comparison is order-sensitive: semantically equal item lists in different order
  under the same key yield 409. Deliberate (fail closed) — clients must send stable ordering.

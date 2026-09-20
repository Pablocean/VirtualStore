# ARCHITECTURE.md — VirtualStore system design

## Layers & dependency rule

```mermaid
graph TD
    API["API (controllers, middleware, DI, seeder)"] --> APP["Application (DTOs, interfaces, validators, maps)"]
    API --> INFRA["Infrastructure (Mongo, services, Stripe, email, jobs)"]
    APP --> DOM["Domain (entities, enums, IRepository, settings)"]
    INFRA --> APP
    INFRA --> DOM
```

`Domain` references nothing. `Application` references `Domain` only. `Infrastructure` implements Application interfaces. `API` wires everything and owns the request pipeline. Cross-layer shortcuts (e.g. controllers using `MongoRepository` directly, Stripe SDK types in DTOs) are forbidden.

## DI graph (representative)

```mermaid
graph LR
    subgraph Config["Options"]
        MS["MongoDbSettings"]:::s
        JS["JwtSettings"]:::s
        ES["EmailSettings"]:::s
        SS["StripeSettings"]:::s
    end
    CTX[("MongoDbContext\n(singleton)")]
    REPO["MongoRepository<T>\n(scoped, open generic)"]
    SVC["Services (scoped):\nAuth, Token, User, Product,\nCart, Order, EnterpriseInfo,\nEmail, StripePayment"]
    CACHE[("CacheService\n(singleton) +\nIMemoryCache")]
    Q["Quartz:\nRefreshTokenCleanupJob\n03:00 daily"]

    MS --> CTX
    CTX --> REPO
    REPO --> SVC
    JS --> SVC
    ES --> SVC
    SS --> SVC
    CACHE --> SVC
    REPO --> Q
    classDef s fill:#eef,stroke:#99c;
```

AutoMapper is a singleton built from `MappingProfile` with `AssertConfigurationIsValid()` at startup — a broken map crashes boot, not a random request. `DatabaseSeeder` is scoped and runs after index creation.

## Request pipeline order (`Program.cs`)

```mermaid
graph LR
    A["UseExceptionHandler\n(ApiExceptionHandler)"] --> B["UseStatusCodePages"]
    B --> C["Request logging\n(inline middleware)"]
    C --> D["UseHttpsRedirection"]
    D --> E["UseCors(CorsPolicy)"]
    E --> F["UseAuthentication"]
    F --> G["UseAuthorization"]
    G --> H["MapHealthChecks(/health)"]
    H --> I["MapControllers"]
```

Notes: exception handling is registered both as `AddProblemDetails` + `AddExceptionHandler` (services) and `UseExceptionHandler` (pipeline) so unhandled exceptions become RFC 7807 bodies. `MapOpenApi` + `MapScalarApiReference` run **only in Development**; the JWT Bearer scheme on that doc comes from `BearerSecuritySchemeTransformer` (also dev-only registration). Startup then ensures MongoDB indexes (non-fatal, warning on failure) and seeds the admin.

## Data & indexes

Collections are named `typeof(T).Name` — `User`, `Product`, `Category`, `Cart`, `Order`, `EnterpriseInfo`. `MongoRepository<T>` is a thin driver wrapper; paging goes through `PagedAsync(predicate, page, size, sortBy, desc)` with server-side count.

| Index | Collection.field | Type | Why |
|---|---|---|---|
| `ux_user_email` | `User.Email` | unique asc | login lookup + duplicate prevention |
| `ux_cart_userId` | `Cart.UserId` | unique asc | one cart per user |
| `ix_order_userId` | `Order.UserId` | asc | order history (`GetUserOrdersAsync`) |
| `ux_order_userIdempotency` | `Order.(UserId, IdempotencyKey)` | unique sparse asc | idempotent checkout replay (ADR-0006) |
| `ix_product_categoryId` | `Product.CategoryId` | asc | catalog filtering |
| `ix_category_parentCategoryId` | `Category.ParentCategoryId` | asc | category tree traversal |

`EnsureIndexesAsync` runs at every boot (idempotent `CreateOneAsync`). Money is `decimal` in Mongo; Stripe conversion to minor units happens only at the Stripe boundary.

## Order status machine

```mermaid
stateDiagram-v2
    [*] --> Pending: POST /api/orders
    Pending --> PaymentReceived: webhook payment_intent.succeeded
    Pending --> Cancelled: webhook failed/refunded\nor Admin PATCH
    PaymentReceived --> Processing: Admin PATCH
    PaymentReceived --> Cancelled: Admin PATCH / refund flow
    Processing --> Shipped: Admin PATCH
    Processing --> Cancelled: Admin PATCH
    Shipped --> Delivered: Admin PATCH
    Delivered --> [*]
    Cancelled --> [*]
```

Enforced by `OrderService.AllowedTransitions`; illegal moves throw `InvalidOperationException` → `409`. Webhook-driven moves (`PaymentReceived`/`Cancelled`) and admin moves share the same guard, so the machine cannot be bypassed.

## Checkout (transactional, idempotent)

```mermaid
sequenceDiagram
    participant C as Client
    participant O as OrdersController
    participant S as OrderService
    participant M as Mongo (replica set)
    C->>O: POST /api/orders (+ Idempotency-Key header?)
    O->>S: CreateOrderAsync(userId, dto)
    S->>M: BEGIN transaction (ambient session)
    S->>M: idempotency check → return existing / 409 on payload mismatch
    S->>M: re-price + validate + decrement stock
    S->>M: insert order → clear cart
    S->>M: COMMIT (abort → 500, see ADR-0006)
```

`MongoDbContext.TransactAsync` exposes the session ambiently (`AsyncLocal.CurrentSession`);
`MongoRepository<T>` enlists automatically — `IRepository<T>` is unchanged. Same `(userId, key)`
replays return the existing order; duplicate-key on insert (lost race) falls back to fetching
the winner. Order history and category listings page server-side via `PagedAsync` (100-cap).
Full rationale in ADR-0006 (supersedes the non-transactional caveats of ADR-0004).

## Cross-cutting notes

- **Validation**: 13 FluentValidators + `AddFluentValidationAutoValidation` — invalid DTOs never reach services; `ValidationException` → `400` with `errors` map.
- **Caching**: single-node `IMemoryCache`; OTP (10 min) + OTP attempts (10 min, 5 max) + `CacheService` default 5-min sliding. No stampede protection — acceptable per ADR 0005; a distributed cache is the documented next step if the API scales out.
- **Observability (current merged state)**: Serilog (console + daily file), `/health` (Mongo `ready`), `traceId` on every error. OpenTelemetry/metrics are **in progress (wave 2f)** — see `docs/OPERATIONS.md`.
- **Rate limiting**: **in progress (wave 2f)** — `429` is reserved in the API contract but no limiter is merged yet.

# AGENTS.md — VirtualStore contributor guide

> Scope: `.NET 10` Clean Architecture ecommerce API (`VirtualStore.slnx`).
> Read this before changing code. Docs live in `docs/`; domain language in `CONTEXT.md`.

## Stack

| Area | Choice |
|---|---|
| Runtime | .NET 10 (`net10.0`), nullable + implicit usings enabled |
| DB | MongoDB 7 (`MongoDB.Driver`); collections named `typeof(T).Name` |
| Auth | JWT Bearer access token (15 min) + opaque refresh token in HttpOnly cookie (7 d) |
| Passwords | `BCrypt.Net-Next` (`BCrypt.Verify` / `HashPassword`) |
| Email / 2FA | `MailKit` SMTP + 6-digit OTP in `IMemoryCache` (10 min TTL, max 5 attempts) |
| Payments | `Stripe.net` (PaymentIntent, refunds, webhook signature verification) |
| Jobs | `Quartz.NET` — daily 03:00 AM refresh-token cleanup (`0 0 3 * * ?`) |
| Cache | `IMemoryCache` via `ICacheService` (absolute TTL when given, else 5 min sliding) |
| Validation | `FluentValidation` × 13 validators, auto-validation enabled |
| Mapping | `AutoMapper` (`MappingProfile`, config validated at startup) |
| Errors | `IExceptionHandler` (`ApiExceptionHandler`) → RFC 7807 `ProblemDetails` + `traceId` |
| API docs | Native OpenAPI (`Microsoft.AspNetCore.OpenApi`) + `Scalar` (dev-only) |
| Config | `appsettings.json` + `.env` via `dotenv.net` (`__` = `:` separator) |
| Health | `AspNetCore.HealthChecks.MongoDb` at `GET /health` |
| Tests | xUnit (wave 2e owns the suites — see `docs/TESTING.md`); integration tests need Testcontainers/MongoDB |

## Layers (dependency direction: API → Application → Domain ← Infrastructure)

- **`VirtualStore.Domain`** — entities (`User`, `Product`, `Category`, `Cart`/`CartItem`, `Order`/`OrderItem`, `EnterpriseInfo`), enums (`UserRole`, `OrderStatus`, `ProductType`), `IRepository<T>`, `Settings/*`. No dependencies on other projects.
- **`VirtualStore.Application`** — DTOs, service interfaces (`I*Service`), `Common/PagedResult<T>`, `Common/TokenClaimTypes`, `Validators/*` (13), `Mappings/MappingProfile`. Depends on Domain only.
- **`VirtualStore.Infrastructure`** — `MongoRepository<T>`, `MongoDbContext` (+ indexes), `Services/*`, `Stripe/StripePaymentService`, `Email/EmailService`, `BackgroundServices/RefreshTokenCleanupJob`. Implements Application interfaces.
- **`VirtualStore.API`** — 9 controllers, `Middlewares/ApiExceptionHandler`, `Extensions/ServiceExtensions`, `Data/DatabaseSeeder`, `OpenApi/BearerSecuritySchemeTransformer` (dev-only), `Program.cs`.

## How to add a module slice (entity → tests)

1. **Entity** (`Domain/Entities/`): extend `BaseEntity` (`Id`, `CreatedAt`, `UpdatedAt`). Keep Stripe SDK / BSON types out.
2. **Repository**: no new code — use generic `IRepository<T>` (`GetByIdAsync`, `FindAsync`, `FindOneAsync`, `AddAsync`, `UpdateAsync`, `DeleteAsync`, `PagedAsync`, `CountAsync`, `ExistsAsync`). Collection name = entity class name; never rename without a migration plan. Add indexes in `MongoDbContext.EnsureIndexesAsync` (`ux_*` unique, `ix_*` non-unique).
3. **Service**: interface in `Application/Interfaces`, implementation in `Infrastructure/Services`. Throw `KeyNotFoundException` (→404), `UnauthorizedAccessException` (→401), `InvalidOperationException` (→409), `FluentValidation.ValidationException` (→400). Never return client-supplied money — re-price server-side (see `OrderService`).
4. **DTOs** (`Application/DTOs/`): `Create*` (all required), `Update*` (nullable fields), response DTO, `*FilterDto` (paging defaults `pageNumber=1`, `pageSize=20`, cap 100 via `PagedResult<T>`).
5. **Validators** (`Application/Validators/`): one `AbstractValidator<T>` per DTO; registered by assembly scan (`AddValidatorsFromAssemblyContaining<LoginRequestValidator>`).
6. **Maps** (`Application/Mappings/MappingProfile`): add both directions; startup asserts the config is valid, so a bad map fails fast.
7. **Controller** (`API/Controllers/`): attribute routing `api/[controller]` (or explicit like `api/enterprise-info`), explicit `[Authorize(Roles=...)]` / `[AllowAnonymous]`, use `User.GetUserId()` (sub → NameIdentifier fallback) — never trust a userId from the body for ownership.
8. **DI** (`API/Extensions/ServiceExtensions`): scoped service + `IOptions<T>` settings binding; keep parallel-wave regions intact.
9. **Tests** (wave 2e layout): unit test the service (mock `IRepository<T>`), validator test per rule, integration test for the controller happy path + one 4xx. See `docs/TESTING.md`.

## DI / Options patterns

```csharp
services.Configure<StripeSettings>(config.GetSection("StripeSettings")); // + MongoDb/Jwt/Email
services.AddScoped<IOrderService, OrderService>();        // services: scoped
services.AddSingleton<ICacheService, CacheService>();    // cache + MongoDbContext: singleton
services.AddScoped(typeof(IRepository<>), typeof(MongoRepository<>));
services.AddMemoryCache();                               // required by CacheService/AuthService OTP
```

Quartz: `AddQuartz` + cron `0 0 3 * * ?` + `AddQuartzHostedService`. JWT: symmetric key, `ClockSkew = TimeSpan.Zero`. CORS: allowlist from `CorsSettings:AllowedOrigins` + `AllowCredentials`.

## Claim helper

```csharp
using VirtualStore.Application.Common;
string? userId = User.GetUserId(); // "sub" claim first, falls back to ClaimTypes.NameIdentifier
```

Access tokens always carry both (`TokenService`: `sub` + `NameIdentifier` + `email` + `jti` + `role` per role). `OrdersController` uses `ClaimTypes.NameIdentifier` directly; prefer `GetUserId()` in new code.

## ProblemDetails contract

Every exception flows through `ApiExceptionHandler`:

| Exception | Status | `title` |
|---|---|---|
| `FluentValidation.ValidationException` | 400 | `Validation Failed` (+ `errors: {field: [msgs]}`) |
| `UnauthorizedAccessException` | 401 | `Unauthorized` |
| `KeyNotFoundException` | 404 | `Not Found` |
| `InvalidOperationException` | 409 | `Conflict` |
| anything else | 500 | `Internal Server Error` (message only in Development) |

Envelope: `{ status, title, detail, instance (path), traceId, errors? }`. `traceId` = `Activity.Current.Id ?? HttpContext.TraceIdentifier`. `429 Too Many Requests` is **reserved** — rate limiting lands in wave 2f (not enforced yet).

## What NOT to do

- ❌ `UserRole` is a **list, not flags**: never use `|`, `&`, `HasFlag`. Validate with `UserRoles.EnsureValid` (distinct + `Enum.IsDefined`, non-empty).
- ❌ Never trust client prices/totals — re-read `Product.Price` from MongoDB (`OrderService` pattern).
- ❌ Never put `userId`/ownership data in request bodies — read it from claims.
- ❌ Never leak Stripe SDK types past `StripePaymentService` — return `PaymentIntentResultDto` / `StripeWebhookResultDto`.
- ❌ Webhook must return `200` even when the order transition can't be applied (log + swallow `KeyNotFoundException`/`InvalidOperationException`) — otherwise Stripe retries forever.
- ❌ Never commit `.env` — secrets only via env/`.env` (see `.env.example`). `appsettings.json` ships empty secrets by design.
- ❌ Don't depend on wave 2f (rate-limiting/OTel) or wave 2e (tests) APIs — both are parallel waves; reference them generically.
- ❌ Don't add `.cs` behavior changes in docs waves — transformer + registration only.
- ❌ Don't rename MongoDB collections (`typeof(T).Name`) or change the `ux_*`/`ix_*` index names casually.
- ❌ `CartController.UpdateItem` takes a **raw JSON number** body (`[FromBody] int quantity`) — don't "fix" it to a DTO without a versioning plan.

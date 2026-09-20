# ADR 0011 — HybridCache backing with Redis L2 opt-in + distributed OTP storage

- Status: Accepted
- Date: 2026-09-20 (wave 3a-hybridcache2, Epic F-lite)
- Supersedes: ADR 0005 (in-memory cache limits) for OTP placement; keeps its key names and TTLs.

## Context

ADR 0005 put OTP codes (`otp_{email}`, 10 min) and OTP attempt counters
(`otp_attempts_{email}`, 10 min, lockout at 5) plus hot catalog reads in a
single-node `IMemoryCache`. That breaks multi-instance deployments (an OTP minted
on node A is unknown on node B) and forces sticky sessions to scale out. The
documented next step was a Redis-backed `IDistributedCache` for OTP + catalog
reads while keeping the `ICacheService` surface stable for
`ProductService`/`EnterpriseInfoService` call sites.

## Decision

1. **Hot-read cache:** re-back `ICacheService`/`CacheService` on
   `Microsoft.Extensions.Caching.Hybrid` (`HybridCache`). Interface unchanged
   (`Get`/`Set`/`Remove`/`TryGet`; absolute TTL when the caller passes one,
   else a 5-minute default). The adapter blocks on the async HybridCache API
   (safe: no single-threaded `SynchronizationContext` in ASP.NET Core/xUnit)
   and never writes misses (read path disables cache writes), preserving the
   "nulls are not cached" contract.
2. **L2 opt-in:** DI (`ServiceExtensions`, `#region Wave 3a-F`) always registers
   `AddDistributedMemoryCache()` (single-instance default, zero ops), and only
   when `Redis:ConnectionString` is non-empty additionally registers
   `AddStackExchangeRedisCache` — last `IDistributedCache` wins, so Redis
   becomes both the OTP store and the HybridCache L2. Then `AddHybridCache()`.
   `Redis:ConnectionString` defaults to `""` in `appsettings.json`, mirrored as
   `Redis__ConnectionString=` in `.env.example`.
3. **OTP storage:** `AuthService` takes `IDistributedCache` instead of
   `IMemoryCache`. Codes via `SetStringAsync`/`GetStringAsync`/`RemoveAsync`
   with `AbsoluteExpirationRelativeToNow = 10 min`; attempt counter as a
   string-encoded int. Key names (`otp_{email}`, `otp_attempts_{email}`,
   lowercase-email normalized), 10-minute window, and 5-attempt lockout are
   identical to ADR 0005.

## Consequences

- (+) OTP works across instances as soon as Redis is configured — no sticky
  sessions; single-instance deploys keep working with no new infrastructure.
- (+) Catalog reads (`product:{id}`, `enterprise:info`) gain stampede
  protection and an optional shared L2 via HybridCache, with no call-site
  changes.
- (−) `ICacheService` default TTL changes from 5-minute **sliding** to 5-minute
  **absolute** (HybridCache has no sliding expiration). Slightly higher MongoDB
  read rate on warm keys; acceptable and documented on `CacheService`.
- (−) Sync-over-async blocking inside `CacheService` (thread-pool block per
  call, no deadlock risk here). If call sites ever go async, prefer native
  `HybridCache` async APIs directly.
- (−) HybridCache L2 values are serialized (System.Text.Json); cached DTOs must
  stay serialization-friendly POCOs.
- (−) No Redis health check / failover yet: if the configured Redis is
  unreachable, distributed calls fail (same exposure as MongoDB). Consider a
  Redis health probe and HybridCache `Flags` tuning in a follow-up.

using Microsoft.Extensions.Caching.Hybrid;
using VirtualStore.Application.Interfaces;

namespace VirtualStore.Infrastructure.Services;

/// <summary>
/// Wave 3a-F (ADR-0011): <see cref="ICacheService"/> backed by <see cref="HybridCache"/>
/// (in-memory L1, Redis L2 when <c>Redis:ConnectionString</c> is configured).
/// The interface stays synchronous, so the async HybridCache calls are blocked on.
/// Safe here: ASP.NET Core / xUnit have no single-threaded SynchronizationContext.
/// Cache misses are never written (read path disables cache writes), preserving the
/// "nulls are not cached" contract relied on by ProductService/EnterpriseInfoService.
/// TTL: absolute <paramref name="expiration"/> when given, else a 5-minute absolute
/// default (HybridCache has no sliding expiration — documented approximation of the
/// previous 5-minute sliding default).
/// Wave T0: HybridCache I/O goes through the injected <see cref="IHybridCacheAdapter"/>
/// async seam (mockable in tests); the sync <see cref="ICacheService"/> surface is unchanged.
/// </summary>
public class CacheService : ICacheService
{
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(5);

    // Read path must not cache misses: GetOrCreateAsync with a default-valued factory
    // would otherwise poison the cache with nulls. Disable both cache writes on reads.
    private static readonly HybridCacheEntryOptions NoWriteOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite
            | HybridCacheEntryFlags.DisableDistributedCacheWrite
    };

    private readonly IHybridCacheAdapter _adapter;

    // Single DI ctor: MS.DI throws on multiple public ctors without
    // [ActivatorUtilitiesConstructor] (found by Wave T1-c DI suite).
    public CacheService(IHybridCacheAdapter adapter)
    {
        _adapter = adapter;
    }

    /// <summary>
    /// Back-compat convenience for hand-construction in tests/tools: wraps
    /// <paramref name="hybridCache"/> in the default <see cref="HybridCacheAdapter"/>.
    /// Not used by DI. Kept non-public so the container has exactly one ctor.
    /// </summary>
    internal CacheService(HybridCache hybridCache)
        : this(new HybridCacheAdapter(hybridCache))
    {
    }

    public T? Get<T>(string key) =>
        _adapter.GetOrCreateAsync<T?>(
            key,
            _ => ValueTask.FromResult<T?>(default),
            NoWriteOptions).GetAwaiter().GetResult();

    public void Set<T>(string key, T value, TimeSpan? expiration = null)
    {
        var options = new HybridCacheEntryOptions
        {
            Expiration = expiration ?? DefaultExpiration
        };
        _adapter.SetAsync(key, value, options).GetAwaiter().GetResult();
    }

    public void Remove(string key) =>
        _adapter.RemoveAsync(key).GetAwaiter().GetResult();

    public bool TryGet<T>(string key, out T? value) where T : class
    {
        value = Get<T>(key);
        return value is not null;
    }
}

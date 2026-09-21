using Microsoft.Extensions.Caching.Hybrid;

namespace VirtualStore.Infrastructure.Services;

/// <summary>
/// Wave T0 testability seam: thin async passthrough over <see cref="HybridCache"/>
/// (<c>GetOrCreate</c>/<c>Set</c>/<c>Remove</c>). The sync <c>ICacheService</c>
/// surface is unchanged; this adapter is the async seam tests mock while
/// production delegates to the real <see cref="HybridCache"/> (memory L1,
/// Redis L2 when configured).
/// </summary>
public interface IHybridCacheAdapter
{
    ValueTask<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T?>> factory,
        HybridCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask SetAsync<T>(
        string key,
        T value,
        HybridCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default production implementation: delegates every call to the injected
/// <see cref="HybridCache"/> with identical arguments.
/// </summary>
public sealed class HybridCacheAdapter : IHybridCacheAdapter
{
    private readonly HybridCache _cache;

    public HybridCacheAdapter(HybridCache cache)
    {
        _cache = cache;
    }

    public ValueTask<T?> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T?>> factory,
        HybridCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
        => _cache.GetOrCreateAsync(key, factory, options, cancellationToken: cancellationToken);

    public ValueTask SetAsync<T>(
        string key,
        T value,
        HybridCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
        => _cache.SetAsync(key, value, options, cancellationToken: cancellationToken);

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        => _cache.RemoveAsync(key, cancellationToken);
}

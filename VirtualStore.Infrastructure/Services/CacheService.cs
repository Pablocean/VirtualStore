using Microsoft.Extensions.Caching.Memory;
using VirtualStore.Application.Interfaces;

namespace VirtualStore.Infrastructure.Services;

public class CacheService : ICacheService
{
    private readonly IMemoryCache _memoryCache;

    public CacheService(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
    }

    public T? Get<T>(string key) => _memoryCache.TryGetValue(key, out T? value) ? value : default;

    public void Set<T>(string key, T value, TimeSpan? expiration = null)
    {
        var options = new MemoryCacheEntryOptions();
        if (expiration.HasValue)
            options.SetAbsoluteExpiration(expiration.Value);
        else
            options.SetSlidingExpiration(TimeSpan.FromMinutes(5));
        _memoryCache.Set(key, value, options);
    }

    public void Remove(string key) => _memoryCache.Remove(key);

    public bool TryGet<T>(string key, out T? value) => _memoryCache.TryGetValue(key, out value);
}
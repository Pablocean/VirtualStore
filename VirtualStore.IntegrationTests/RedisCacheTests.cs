using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using VirtualStore.Application.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// <see cref="ICacheService"/> (HybridCache: memory L1 + Redis L2) and the
/// <see cref="IDistributedCache"/> OTP path against a real Redis container
/// (<c>redis:7-alpine</c>), plus the memory-only branch (no
/// <c>Redis:ConnectionString</c>) asserting identical behavior.
/// Mirrors the <c>AddWave3aFHybridCache</c> wiring in ServiceExtensions.
/// Docker-gated; skipped without a daemon (see <see cref="RequiresDockerFactAttribute"/>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisCacheTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _redis;

    public RedisCacheTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [RequiresDockerFact]
    public async Task Redis_L2_RoundTrip_Through_CacheService_And_Otp_Path()
    {
        using var provider = BuildCacheProvider(_redis.ConnectionString);
        var cache = provider.GetRequiredService<ICacheService>();
        var dist = provider.GetRequiredService<IDistributedCache>();

        AssertCacheContract(cache);
        await AssertDistributedContractAsync(dist);

        // Redis-backed IDistributedCache resolves to the StackExchangeRedis
        // implementation (not in-memory): proves the L2 wiring is live.
        dist.GetType().FullName.Should().Contain("Redis");
    }

    [RequiresDockerFact]
    public async Task Memory_Only_Branch_Behaves_Identically()
    {
        using var provider = BuildCacheProvider(redisConnectionString: null);
        var cache = provider.GetRequiredService<ICacheService>();
        var dist = provider.GetRequiredService<IDistributedCache>();

        AssertCacheContract(cache);
        await AssertDistributedContractAsync(dist);

        dist.GetType().FullName.Should().Contain("Memory");
    }

    /// <summary>
    /// Builds the same cache stack as production: in-memory
    /// <see cref="IDistributedCache"/> by default, StackExchangeRedis when a
    /// connection string is supplied (last registration wins, also backing the
    /// HybridCache L2), then HybridCache + the production adapter + service.
    /// </summary>
    private static ServiceProvider BuildCacheProvider(string? redisConnectionString)
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = "t2:";
            });
        }
        services.AddHybridCache();
        services.AddSingleton<IHybridCacheAdapter, HybridCacheAdapter>();
        services.AddSingleton<ICacheService, CacheService>();
        return services.BuildServiceProvider();
    }

    private static void AssertCacheContract(ICacheService cache)
    {
        var key = $"t2:{Guid.NewGuid():N}";
        cache.TryGet<string>(key, out _).Should().BeFalse("a fresh key must miss");

        cache.Set(key, "hello");
        cache.Get<string>(key).Should().Be("hello");
        cache.TryGet<string>(key, out var hit).Should().BeTrue();
        hit.Should().Be("hello");

        cache.Set(key, "updated", TimeSpan.FromMinutes(5));
        cache.Get<string>(key).Should().Be("updated");

        cache.Remove(key);
        cache.TryGet<string>(key, out _).Should().BeFalse("removed keys must miss again");
        cache.Get<string>(key).Should().BeNull("nulls are never cached");
    }

    private static async Task AssertDistributedContractAsync(IDistributedCache dist)
    {
        // OTP-style usage: 10-minute absolute TTL, single read, removal.
        var key = $"otp_{Guid.NewGuid():N}@example.com".ToLowerInvariant();
        await dist.SetStringAsync(key, "482916", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
        });

        (await dist.GetStringAsync(key)).Should().Be("482916");

        await dist.RemoveAsync(key);
        (await dist.GetStringAsync(key)).Should().BeNull();
    }
}

/// <summary>
/// Redis container fixture (<c>redis:7-alpine</c>, port 6379).
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"{_container?.Hostname ?? "localhost"}:{_container?.GetMappedPublicPort(6379) ?? 6379}";

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage("redis:7-alpine")
                .WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
                .Build();
            await _container.StartAsync();
        }
        catch (Exception)
        {
            // Docker unavailable: callers are RequiresDockerFact-gated and skip.
            if (_container is not null)
            {
                try { await _container.DisposeAsync(); } catch { /* ignore */ }
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }
}

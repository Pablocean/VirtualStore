using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

public class CacheServiceTests
{
    private static (CacheService Service, Mock<IHybridCacheAdapter> Adapter) Build()
    {
        var adapter = new Mock<IHybridCacheAdapter>();
        return (new CacheService(adapter.Object), adapter);
    }

    [Fact]
    public void Get_Delegates_To_Adapter_With_NoWrite_Flags_And_Returns_Value()
    {
        var (svc, adapter) = Build();
        HybridCacheEntryOptions? seenOptions = null;
        adapter.Setup(a => a.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, ValueTask<string?>>>(),
                It.IsAny<HybridCacheEntryOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string k, Func<CancellationToken, ValueTask<string?>> f,
                HybridCacheEntryOptions? o, CancellationToken _) =>
            {
                seenOptions = o;
                return "stored";
            });

        var result = svc.Get<string>("k1");

        result.Should().Be("stored");
        seenOptions.Should().NotBeNull();
        seenOptions!.Flags.Should().HaveFlag(HybridCacheEntryFlags.DisableLocalCacheWrite);
        seenOptions.Flags.Should().HaveFlag(HybridCacheEntryFlags.DisableDistributedCacheWrite);
    }

    [Fact]
    public void Get_Miss_Returns_Null_And_Does_Not_Write()
    {
        var (svc, adapter) = Build();
        Func<CancellationToken, ValueTask<string?>>? seenFactory = null;
        adapter.Setup(a => a.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, ValueTask<string?>>>(),
                It.IsAny<HybridCacheEntryOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string k, Func<CancellationToken, ValueTask<string?>> f,
                HybridCacheEntryOptions? o, CancellationToken ct) =>
            {
                seenFactory = f;
                return f(ct).GetAwaiter().GetResult();
            });

        svc.Get<string>("missing").Should().BeNull();
        seenFactory.Should().NotBeNull("read path must supply a factory");
    }

    [Fact]
    public void Set_With_Explicit_Expiration_Passes_Absolute_Ttl()
    {
        var (svc, adapter) = Build();
        HybridCacheEntryOptions? seen = null;
        adapter.Setup(a => a.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<HybridCacheEntryOptions?>(), It.IsAny<CancellationToken>()))
            .Callback((string k, string v, HybridCacheEntryOptions? o, CancellationToken _) => seen = o)
            .Returns(ValueTask.CompletedTask);

        svc.Set("k", "v", TimeSpan.FromMinutes(10));

        seen.Should().NotBeNull();
        seen!.Expiration.Should().NotBeNull();
        Math.Abs((seen.Expiration!.Value - TimeSpan.FromMinutes(10)).TotalSeconds).Should().BeLessThan(5);
    }

    [Fact]
    public void Set_Without_Expiration_Falls_Back_To_Five_Minute_Default()
    {
        var (svc, adapter) = Build();
        HybridCacheEntryOptions? seen = null;
        adapter.Setup(a => a.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<HybridCacheEntryOptions?>(), It.IsAny<CancellationToken>()))
            .Callback((string k, string v, HybridCacheEntryOptions? o, CancellationToken _) => seen = o)
            .Returns(ValueTask.CompletedTask);

        svc.Set("k", "v");

        seen.Should().NotBeNull();
        seen!.Expiration.Should().NotBeNull();
        Math.Abs((seen.Expiration!.Value - TimeSpan.FromMinutes(5)).TotalSeconds).Should().BeLessThan(5);
    }

    [Fact]
    public void TryGet_Hit_Returns_True_With_Value()
    {
        var (svc, adapter) = Build();
        adapter.Setup(a => a.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, ValueTask<string?>>>(),
                It.IsAny<HybridCacheEntryOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("hello");

        var ok = svc.TryGet<string>("k", out var value);

        ok.Should().BeTrue();
        value.Should().Be("hello");
    }

    [Fact]
    public void TryGet_Miss_Returns_False_With_Null()
    {
        var (svc, adapter) = Build();
        adapter.Setup(a => a.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, ValueTask<string?>>>(),
                It.IsAny<HybridCacheEntryOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var ok = svc.TryGet<string>("missing", out var value);

        ok.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void Remove_Delegates_To_Adapter()
    {
        var (svc, adapter) = Build();
        adapter.Setup(a => a.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        svc.Remove("k");

        adapter.Verify(a => a.RemoveAsync("k", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void BackCompat_Ctor_Wraps_HybridCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<HybridCache>();

        var svc = new CacheService(hybrid);

        svc.Get<string>("t1b:missing").Should().BeNull();
        svc.Set("t1b:key", "v", TimeSpan.FromMinutes(1));
        svc.Get<string>("t1b:key").Should().Be("v");
        svc.Remove("t1b:key");
        svc.Get<string>("t1b:key").Should().BeNull();
    }
}

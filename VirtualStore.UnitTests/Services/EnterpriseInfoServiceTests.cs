using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

public class EnterpriseInfoServiceTests
{
    private static IMapper RealMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private sealed record Harness(
        EnterpriseInfoService Service,
        Mock<IRepository<EnterpriseInfo>> Repo,
        Mock<ICacheService> Cache);

    private static Harness Build(List<EnterpriseInfo>? store = null)
    {
        store ??= new List<EnterpriseInfo>();
        var repo = new Mock<IRepository<EnterpriseInfo>>();
        var cache = new Mock<ICacheService>();

        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(store.AsEnumerable());
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(store.AsEnumerable());

        return new Harness(new EnterpriseInfoService(repo.Object, RealMapper(), cache.Object), repo, cache);
    }

    private static void SetupCacheMiss(Harness h)
    {
        EnterpriseInfoDto? miss = null;
        h.Cache.Setup(c => c.TryGet<EnterpriseInfoDto>(It.IsAny<string>(), out miss)).Returns(false);
    }

    private static EnterpriseInfo Info(string id = "info1") => new()
    {
        Id = id,
        CompanyName = "Acme",
        Address = "123 Main St",
        Phone = "555-1234",
        Email = "info@acme.test"
    };

    [Fact]
    public async Task Get_Cancelled_Throws_Before_Cache()
    {
        var h = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => h.Service.GetEnterpriseInfoAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Get_Cache_Hit_Returns_Cached_Without_Repo()
    {
        var h = Build();
        var cached = new EnterpriseInfoDto { Id = "info1", CompanyName = "Cached" };
        EnterpriseInfoDto? hit = cached;
        h.Cache.Setup(c => c.TryGet<EnterpriseInfoDto>(It.IsAny<string>(), out hit)).Returns(true);

        var result = await h.Service.GetEnterpriseInfoAsync();

        result.Should().BeSameAs(cached);
        h.Repo.Verify(r => r.GetAllAsync(), Times.Never);
        h.Cache.Verify(c => c.Set(It.IsAny<string>(), It.IsAny<EnterpriseInfoDto>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Fact]
    public async Task Get_Empty_Store_Returns_Null_Without_Caching()
    {
        var h = Build(new List<EnterpriseInfo>());
        SetupCacheMiss(h);

        var result = await h.Service.GetEnterpriseInfoAsync();

        result.Should().BeNull();
        h.Cache.Verify(c => c.Set(It.IsAny<string>(), It.IsAny<EnterpriseInfoDto>(), It.IsAny<TimeSpan?>()), Times.Never);
    }

    [Fact]
    public async Task Get_Singleton_First_Caches_With_Ten_Minute_Lifetime()
    {
        var h = Build(new List<EnterpriseInfo> { Info("first"), Info("second") });
        SetupCacheMiss(h);
        TimeSpan? seenLifetime = null;
        h.Cache.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<EnterpriseInfoDto>(), It.IsAny<TimeSpan?>()))
            .Callback((string k, EnterpriseInfoDto v, TimeSpan? e) => seenLifetime = e);

        var result = await h.Service.GetEnterpriseInfoAsync();

        result.Should().NotBeNull();
        result!.Id.Should().Be("first");
        result.CompanyName.Should().Be("Acme");
        seenLifetime.Should().NotBeNull();
        Math.Abs((seenLifetime!.Value - TimeSpan.FromMinutes(10)).TotalSeconds).Should().BeLessThan(5);
    }

    [Fact]
    public async Task Update_Cancelled_Throws_Before_Repo()
    {
        var h = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => h.Service.UpdateEnterpriseInfoAsync(new UpdateEnterpriseInfoDto { CompanyName = "x" }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Repo.Verify(r => r.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task Update_Empty_Store_Takes_Add_Path_And_Invalidates()
    {
        var store = new List<EnterpriseInfo>();
        var h = Build(store);
        EnterpriseInfo? added = null;
        h.Repo.Setup(r => r.AddAsync(It.IsAny<EnterpriseInfo>()))
            .Callback((EnterpriseInfo e) => { added = e; store.Add(e); }).Returns(Task.CompletedTask);

        var result = await h.Service.UpdateEnterpriseInfoAsync(new UpdateEnterpriseInfoDto { CompanyName = "Brand New" });

        result.CompanyName.Should().Be("Brand New");
        added.Should().NotBeNull();
        h.Repo.Verify(r => r.AddAsync(It.IsAny<EnterpriseInfo>()), Times.Once);
        h.Repo.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<EnterpriseInfo>()), Times.Never);
        h.Cache.Verify(c => c.Remove("enterprise:info"), Times.Once);
    }

    [Fact]
    public async Task Update_Existing_Takes_Update_Path_And_Invalidates()
    {
        var h = Build(new List<EnterpriseInfo> { Info("info1") });

        var result = await h.Service.UpdateEnterpriseInfoAsync(new UpdateEnterpriseInfoDto { CompanyName = "Renamed" });

        result.Id.Should().Be("info1");
        result.CompanyName.Should().Be("Renamed");
        h.Repo.Verify(r => r.UpdateAsync("info1", It.IsAny<EnterpriseInfo>()), Times.Once);
        h.Repo.Verify(r => r.AddAsync(It.IsAny<EnterpriseInfo>()), Times.Never);
        h.Cache.Verify(c => c.Remove("enterprise:info"), Times.Once);
    }
}

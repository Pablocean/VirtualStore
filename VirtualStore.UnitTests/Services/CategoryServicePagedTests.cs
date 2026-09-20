using System.Linq.Expressions;
using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

/// <summary>Guards the server-side paging move (ADR-0006, item 5): no more
/// GetAllAsync + in-memory Skip/Take, 100-cap honored, Name-asc order kept.</summary>
public class CategoryServicePagedTests
{
    private static IMapper RealMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    [Fact]
    public async Task GetPaged_Uses_PagedAsync_With_Name_Sort_And_100_Cap()
    {
        var repo = new Mock<IRepository<Category>>();
        repo.Setup(r => r.PagedAsync(
                It.IsAny<Expression<Func<Category, bool>>>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<Category>)new List<Category>
            {
                new() { Id = "c1", Name = "Books", IsActive = true }
            }, 1L));
        var service = new CategoryService(repo.Object, RealMapper());

        var result = await service.GetPagedAsync(new CategoryFilterDto { PageNumber = 1, PageSize = 500 });

        result.Items.Should().ContainSingle(i => i.Name == "Books");
        result.TotalCount.Should().Be(1);
        result.PageSize.Should().Be(100);
        repo.Verify(r => r.PagedAsync(
            It.IsAny<Expression<Func<Category, bool>>>(),
            1, 100, "Name", false, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task GetPaged_Maps_Filter_Values_And_Defaults_Page()
    {
        var repo = new Mock<IRepository<Category>>();
        repo.Setup(r => r.PagedAsync(
                It.IsAny<Expression<Func<Category, bool>>>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<Category>)new List<Category>(), 0L));
        var service = new CategoryService(repo.Object, RealMapper());

        var result = await service.GetPagedAsync(new CategoryFilterDto { PageNumber = 0, PageSize = 0 });

        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(20);
        repo.Verify(r => r.PagedAsync(
            It.IsAny<Expression<Func<Category, bool>>>(),
            1, 20, "Name", false, It.IsAny<CancellationToken>()), Times.Once);
    }
}

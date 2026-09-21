using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using VirtualStore.API.Controllers;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using Xunit;

namespace VirtualStore.UnitTests.Controllers;

public class CategoriesControllerTests
{
    private static (CategoriesController Controller, Mock<ICategoryService> Categories) Create()
    {
        var categories = new Mock<ICategoryService>();
        var controller = new CategoriesController(categories.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (controller, categories);
    }

    [Fact]
    public void Ctor_NullService_Throws()
    {
        var act = () => new CategoriesController(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetCategories_Returns_Ok_And_Passes_Token()
    {
        var (controller, categories) = Create();
        var filter = new CategoryFilterDto { Search = "x" };
        var paged = new PagedResult<CategoryDto> { Items = new List<CategoryDto> { new() { Id = "c1" } }, TotalCount = 1 };
        using var cts = new CancellationTokenSource();
        categories.Setup(c => c.GetPagedAsync(filter, cts.Token)).ReturnsAsync(paged);

        var result = await controller.GetCategories(filter, cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(paged);
    }

    [Fact]
    public async Task GetCategory_UnknownId_Returns_NotFound()
    {
        var (controller, categories) = Create();
        categories.Setup(c => c.GetByIdAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((CategoryDto?)null);

        var result = await controller.GetCategory("missing", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetCategory_Found_Returns_Ok_And_Passes_Token()
    {
        var (controller, categories) = Create();
        var dto = new CategoryDto { Id = "c1" };
        using var cts = new CancellationTokenSource();
        categories.Setup(c => c.GetByIdAsync("c1", cts.Token)).ReturnsAsync(dto);

        var result = await controller.GetCategory("c1", cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task CreateCategory_Returns_CreatedAtAction_And_Passes_Token()
    {
        var (controller, categories) = Create();
        var dto = new CreateCategoryDto { Name = "n" };
        var created = new CategoryDto { Id = "c1", Name = "n" };
        using var cts = new CancellationTokenSource();
        categories.Setup(c => c.CreateAsync(dto, cts.Token)).ReturnsAsync(created);

        var result = await controller.CreateCategory(dto, cts.Token);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.ActionName.Should().Be(nameof(CategoriesController.GetCategory));
        createdResult.RouteValues.Should().ContainKey("id").WhoseValue.Should().Be("c1");
        createdResult.Value.Should().BeSameAs(created);
    }

    [Fact]
    public async Task UpdateCategory_Returns_Ok_And_Passes_Token()
    {
        var (controller, categories) = Create();
        var dto = new UpdateCategoryDto { Name = "n2" };
        var updated = new CategoryDto { Id = "c1", Name = "n2" };
        using var cts = new CancellationTokenSource();
        categories.Setup(c => c.UpdateAsync("c1", dto, cts.Token)).ReturnsAsync(updated);

        var result = await controller.UpdateCategory("c1", dto, cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(updated);
    }

    [Fact]
    public async Task DeleteCategory_Returns_NoContent_And_Passes_Token()
    {
        var (controller, categories) = Create();
        using var cts = new CancellationTokenSource();

        var result = await controller.DeleteCategory("c1", cts.Token);

        result.Should().BeOfType<NoContentResult>();
        categories.Verify(c => c.DeleteAsync("c1", cts.Token), Times.Once);
    }
}

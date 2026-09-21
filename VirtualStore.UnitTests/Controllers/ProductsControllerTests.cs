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

public class ProductsControllerTests
{
    private static (ProductsController Controller, Mock<IProductService> Products) Create()
    {
        var products = new Mock<IProductService>();
        var controller = new ProductsController(products.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (controller, products);
    }

    [Fact]
    public void Ctor_NullService_Throws()
    {
        var act = () => new ProductsController(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetProducts_Returns_Ok()
    {
        var (controller, products) = Create();
        var filter = new ProductFilterDto { Search = "x" };
        var paged = new PagedResult<ProductDto> { Items = new List<ProductDto> { new() { Id = "p1" } }, TotalCount = 1 };
        products.Setup(p => p.GetProductsAsync(filter)).ReturnsAsync(paged);

        var result = await controller.GetProducts(filter);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(paged);
    }

    [Fact]
    public async Task GetProduct_UnknownId_Returns_NotFound()
    {
        var (controller, products) = Create();
        products.Setup(p => p.GetProductByIdAsync("missing")).ReturnsAsync((ProductDto?)null);

        var result = await controller.GetProduct("missing");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetProduct_Found_Returns_Ok()
    {
        var (controller, products) = Create();
        var dto = new ProductDto { Id = "p1" };
        products.Setup(p => p.GetProductByIdAsync("p1")).ReturnsAsync(dto);

        var result = await controller.GetProduct("p1");

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task CreateProduct_Returns_CreatedAtAction()
    {
        var (controller, products) = Create();
        var dto = new CreateProductDto { Name = "n" };
        var created = new ProductDto { Id = "p1", Name = "n" };
        products.Setup(p => p.CreateProductAsync(dto)).ReturnsAsync(created);

        var result = await controller.CreateProduct(dto);

        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.ActionName.Should().Be(nameof(ProductsController.GetProduct));
        createdResult.RouteValues.Should().ContainKey("id").WhoseValue.Should().Be("p1");
        createdResult.Value.Should().BeSameAs(created);
    }

    [Fact]
    public async Task UpdateProduct_Returns_Ok()
    {
        var (controller, products) = Create();
        var dto = new UpdateProductDto { Name = "n2" };
        var updated = new ProductDto { Id = "p1", Name = "n2" };
        products.Setup(p => p.UpdateProductAsync("p1", dto)).ReturnsAsync(updated);

        var result = await controller.UpdateProduct("p1", dto);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(updated);
    }

    [Fact]
    public async Task DeleteProduct_Returns_NoContent()
    {
        var (controller, products) = Create();

        var result = await controller.DeleteProduct("p1");

        result.Should().BeOfType<NoContentResult>();
        products.Verify(p => p.DeleteProductAsync("p1"), Times.Once);
    }
}

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using VirtualStore.API.Controllers;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using Xunit;

namespace VirtualStore.UnitTests.Controllers;

public class CartControllerTests
{
    private static (CartController Controller, Mock<ICartService> Cart) Create(params Claim[] claims)
    {
        var cart = new Mock<ICartService>();
        var controller = new CartController(cart.Object);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, cart);
    }

    private static Claim Sub(string id = "user-1") => new("sub", id);

    [Fact]
    public async Task GetCart_NoIdentity_Returns_401()
    {
        var (controller, cart) = Create();

        var result = await controller.GetCart();

        var unauthorized = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        cart.Verify(c => c.GetCartAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetCart_SubClaim_Returns_Ok()
    {
        var (controller, cart) = Create(Sub());
        var dto = new CartDto { Id = "c1", UserId = "user-1" };
        cart.Setup(c => c.GetCartAsync("user-1")).ReturnsAsync(dto);

        var result = await controller.GetCart();

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task GetCart_NameIdentifierFallback_Returns_Ok()
    {
        var (controller, cart) = Create(new Claim(ClaimTypes.NameIdentifier, "user-9"));
        cart.Setup(c => c.GetCartAsync("user-9")).ReturnsAsync(new CartDto());

        var result = await controller.GetCart();

        result.Result.Should().BeOfType<OkObjectResult>();
        cart.Verify(c => c.GetCartAsync("user-9"), Times.Once);
    }

    [Fact]
    public async Task AddToCart_NoIdentity_Returns_401()
    {
        var (controller, cart) = Create();

        var result = await controller.AddToCart(new AddToCartDto { ProductId = "p1", Quantity = 2 });

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        cart.Verify(c => c.AddToCartAsync(It.IsAny<string>(), It.IsAny<AddToCartDto>()), Times.Never);
    }

    [Fact]
    public async Task AddToCart_Passes_UserId_And_Dto()
    {
        var (controller, cart) = Create(Sub());
        var dto = new AddToCartDto { ProductId = "p1", Quantity = 2 };
        var expected = new CartDto { Id = "c1" };
        cart.Setup(c => c.AddToCartAsync("user-1", dto)).ReturnsAsync(expected);

        var result = await controller.AddToCart(dto);

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task UpdateItem_NoIdentity_Returns_401()
    {
        var (controller, cart) = Create();

        var result = await controller.UpdateItem("p1", 3);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        cart.Verify(c => c.UpdateCartItemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task UpdateItem_Passes_Raw_Quantity_Through()
    {
        var (controller, cart) = Create(Sub());
        var expected = new CartDto { Id = "c1" };
        cart.Setup(c => c.UpdateCartItemAsync("user-1", "p1", 7)).ReturnsAsync(expected);

        var result = await controller.UpdateItem("p1", 7);

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(expected);
        cart.Verify(c => c.UpdateCartItemAsync("user-1", "p1", 7), Times.Once);
    }

    [Fact]
    public async Task RemoveItem_NoIdentity_Returns_401()
    {
        var (controller, cart) = Create();

        var result = await controller.RemoveItem("p1");

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        cart.Verify(c => c.RemoveFromCartAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveItem_Passes_UserId_And_ProductId()
    {
        var (controller, cart) = Create(Sub());
        var expected = new CartDto { Id = "c1" };
        cart.Setup(c => c.RemoveFromCartAsync("user-1", "p1")).ReturnsAsync(expected);

        var result = await controller.RemoveItem("p1");

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task ClearCart_NoIdentity_Returns_401()
    {
        var (controller, cart) = Create();

        var result = await controller.ClearCart();

        result.Should().BeOfType<UnauthorizedObjectResult>();
        cart.Verify(c => c.ClearCartAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ClearCart_Returns_NoContent()
    {
        var (controller, cart) = Create(Sub());

        var result = await controller.ClearCart();

        result.Should().BeOfType<NoContentResult>();
        cart.Verify(c => c.ClearCartAsync("user-1"), Times.Once);
    }
}

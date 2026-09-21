using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using VirtualStore.API.Controllers;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Enums;
using Xunit;

namespace VirtualStore.UnitTests.Controllers;

public class OrdersControllerTests
{
    private static (OrdersController Controller, Mock<IOrderService> Orders, DefaultHttpContext Http) Create(
        string? userId = "user-1",
        bool admin = false,
        string? idempotencyHeader = null)
    {
        var orders = new Mock<IOrderService>();
        var controller = new OrdersController(orders.Object);
        var http = new DefaultHttpContext();
        var claims = new List<Claim>();
        if (userId is not null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        if (admin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        if (idempotencyHeader is not null)
            http.Request.Headers["Idempotency-Key"] = idempotencyHeader;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, orders, http);
    }

    private static CreateOrderDto ValidDto(string? key = null) => new()
    {
        Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 1 } },
        ShippingAddress = new AddressDto { Street = "s", City = "c", State = "st", ZipCode = "z", Country = "co" },
        IdempotencyKey = key
    };

    [Fact]
    public void Ctor_NullService_Throws()
    {
        var act = () => new OrdersController(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateOrder_NoHeader_Preserves_BodyKey()
    {
        var (controller, orders, _) = Create();
        var dto = ValidDto("body-key");
        var created = new OrderDto { Id = "o1", UserId = "user-1" };
        orders.Setup(o => o.CreateOrderAsync("user-1", dto, It.IsAny<CancellationToken>())).ReturnsAsync(created);

        var result = await controller.CreateOrder(dto, CancellationToken.None);

        dto.IdempotencyKey.Should().Be("body-key");
        var createdResult = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.ActionName.Should().Be(nameof(OrdersController.GetOrder));
        createdResult.RouteValues.Should().ContainKey("id").WhoseValue.Should().Be("o1");
        createdResult.Value.Should().BeSameAs(created);
    }

    [Fact]
    public async Task CreateOrder_WhitespaceHeader_Preserves_BodyKey()
    {
        var (controller, orders, _) = Create(idempotencyHeader: "   ");
        var dto = ValidDto("body-key");
        orders.Setup(o => o.CreateOrderAsync(It.IsAny<string>(), It.IsAny<CreateOrderDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderDto { Id = "o1" });

        await controller.CreateOrder(dto, CancellationToken.None);

        dto.IdempotencyKey.Should().Be("body-key");
    }

    [Fact]
    public async Task CreateOrder_MissingHeaderAndBody_KeyStaysNull()
    {
        var (controller, orders, _) = Create();
        var dto = ValidDto();
        string? captured = "sentinel";
        orders.Setup(o => o.CreateOrderAsync(It.IsAny<string>(), It.IsAny<CreateOrderDto>(), It.IsAny<CancellationToken>()))
            .Callback((string _, CreateOrderDto d, CancellationToken _) => captured = d.IdempotencyKey)
            .ReturnsAsync(new OrderDto { Id = "o1" });

        await controller.CreateOrder(dto, CancellationToken.None);

        captured.Should().BeNull();
    }

    [Fact]
    public async Task CreateOrder_HeaderWins_Trimmed()
    {
        var (controller, orders, _) = Create(idempotencyHeader: "  hdr-key  ");
        var dto = ValidDto("body-key");
        string? captured = null;
        orders.Setup(o => o.CreateOrderAsync("user-1", It.IsAny<CreateOrderDto>(), It.IsAny<CancellationToken>()))
            .Callback((string _, CreateOrderDto d, CancellationToken _) => captured = d.IdempotencyKey)
            .ReturnsAsync(new OrderDto { Id = "o1" });

        await controller.CreateOrder(dto, CancellationToken.None);

        captured.Should().Be("hdr-key");
    }

    [Fact]
    public async Task CreateOrder_Passes_CancellationToken()
    {
        var (controller, orders, _) = Create(idempotencyHeader: "k");
        var dto = ValidDto();
        using var cts = new CancellationTokenSource();
        orders.Setup(o => o.CreateOrderAsync(It.IsAny<string>(), It.IsAny<CreateOrderDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderDto { Id = "o1" });

        await controller.CreateOrder(dto, cts.Token);

        orders.Verify(o => o.CreateOrderAsync("user-1", dto, cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetMyOrders_Returns_Ok_With_Paging_And_Token()
    {
        var (controller, orders, _) = Create();
        var paged = new PagedResult<OrderDto> { Items = new List<OrderDto> { new() { Id = "o1" } }, TotalCount = 1 };
        using var cts = new CancellationTokenSource();
        orders.Setup(o => o.GetUserOrdersAsync("user-1", 2, 10, cts.Token)).ReturnsAsync(paged);

        var result = await controller.GetMyOrders(2, 10, cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(paged);
    }

    [Fact]
    public async Task GetOrder_UnknownId_Returns_NotFound()
    {
        var (controller, orders, _) = Create();
        orders.Setup(o => o.GetOrderByIdAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((OrderDto?)null);

        var result = await controller.GetOrder("missing", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetOrder_Owner_Returns_Ok()
    {
        var (controller, orders, _) = Create(userId: "user-1");
        var order = new OrderDto { Id = "o1", UserId = "user-1" };
        orders.Setup(o => o.GetOrderByIdAsync("o1", It.IsAny<CancellationToken>())).ReturnsAsync(order);

        var result = await controller.GetOrder("o1", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(order);
    }

    [Fact]
    public async Task GetOrder_NonOwner_NonAdmin_Returns_Forbid()
    {
        var (controller, orders, _) = Create(userId: "user-2");
        orders.Setup(o => o.GetOrderByIdAsync("o1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderDto { Id = "o1", UserId = "user-1" });

        var result = await controller.GetOrder("o1", CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task GetOrder_NonOwner_Admin_Returns_Ok()
    {
        var (controller, orders, _) = Create(userId: "user-2", admin: true);
        var order = new OrderDto { Id = "o1", UserId = "user-1" };
        orders.Setup(o => o.GetOrderByIdAsync("o1", It.IsAny<CancellationToken>())).ReturnsAsync(order);

        var result = await controller.GetOrder("o1", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(order);
    }

    [Fact]
    public async Task GetOrder_Passes_CancellationToken()
    {
        var (controller, orders, _) = Create();
        using var cts = new CancellationTokenSource();
        orders.Setup(o => o.GetOrderByIdAsync("o1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderDto { Id = "o1", UserId = "user-1" });

        await controller.GetOrder("o1", cts.Token);

        orders.Verify(o => o.GetOrderByIdAsync("o1", cts.Token), Times.Once);
    }

    [Fact]
    public async Task UpdateOrderStatus_Returns_Ok_And_Passes_Token()
    {
        var (controller, orders, _) = Create();
        var updated = new OrderDto { Id = "o1", Status = OrderStatus.Shipped };
        using var cts = new CancellationTokenSource();
        orders.Setup(o => o.UpdateOrderStatusAsync("o1", OrderStatus.Shipped, cts.Token)).ReturnsAsync(updated);

        var result = await controller.UpdateOrderStatus("o1", new UpdateOrderStatusDto { Status = OrderStatus.Shipped }, cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(updated);
    }
}

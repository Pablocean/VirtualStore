using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

public class OrderServiceTests
{
    private static IMapper RealMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private static AddressDto ValidAddress() => new()
    {
        Street = "123 Main St",
        City = "Springfield",
        State = "IL",
        ZipCode = "62701",
        Country = "USA"
    };

    private sealed record Harness(
        OrderService Service,
        Mock<IRepository<Order>> Orders,
        Mock<IRepository<Cart>> Carts,
        Mock<IRepository<Product>> Products,
        List<Order> AddedOrders);

    private static Harness Build(
        Dictionary<string, Product>? products = null,
        Cart? cart = null)
    {
        products ??= new Dictionary<string, Product>();
        var orders = new Mock<IRepository<Order>>();
        var carts = new Mock<IRepository<Cart>>();
        var productRepo = new Mock<IRepository<Product>>();
        var added = new List<Order>();

        productRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => products.TryGetValue(id, out var p) ? p : null);
        productRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => products.TryGetValue(id, out var p) ? p : null);

        carts.Setup(r => r.FindOneAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Cart, bool>>>()))
            .ReturnsAsync(cart);
        carts.Setup(r => r.FindOneAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Cart, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        orders.Setup(r => r.AddAsync(It.IsAny<Order>()))
            .Callback((Order o) => added.Add(o))
            .Returns(Task.CompletedTask);
        orders.Setup(r => r.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback((Order o, CancellationToken _) => added.Add(o))
            .Returns(Task.CompletedTask);

        var service = new OrderService(orders.Object, carts.Object, productRepo.Object, RealMapper(), context: null, paymentService: null);
        return new Harness(service, orders, carts, productRepo, added);
    }

    private static Product P(string id, string name, decimal price, int stock, bool active = true) => new()
    {
        Id = id,
        Name = name,
        Price = price,
        Currency = "usd",
        StockQuantity = stock,
        CategoryId = "c1",
        IsActive = active
    };

    [Fact]
    public async Task CreateOrder_Reprices_From_Database_Ignoring_Client_Prices()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", "Widget", 25.00m, 10) });
        var dto = new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "p1", ProductName = "Hacked", UnitPrice = 0.01m, Quantity = 2 } },
            ShippingAddress = ValidAddress()
        };

        var result = await h.Service.CreateOrderAsync("u1", dto);

        result.TotalAmount.Should().Be(50.00m);
        result.Items.Should().ContainSingle(i => i.UnitPrice == 25.00m && i.ProductName == "Widget");
        result.Status.Should().Be(OrderStatus.Pending);
        result.UserId.Should().Be("u1");
        h.AddedOrders.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateOrder_Decrements_Stock_And_Persists_Product_Update()
    {
        var product = P("p1", "Widget", 10m, 5);
        var h = Build(new Dictionary<string, Product> { ["p1"] = product });
        var dto = new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 3, UnitPrice = 10m } },
            ShippingAddress = ValidAddress()
        };

        await h.Service.CreateOrderAsync("u1", dto);

        product.StockQuantity.Should().Be(2);
        h.Products.Verify(r => r.UpdateAsync("p1", It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateOrder_Clears_Cart_After_Insert()
    {
        var cart = new Cart { Id = "cart1", UserId = "u1" };
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", "Widget", 10m, 5) }, cart);
        var dto = new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 1, UnitPrice = 10m } },
            ShippingAddress = ValidAddress()
        };

        await h.Service.CreateOrderAsync("u1", dto);

        h.Carts.Verify(r => r.DeleteAsync("cart1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateOrder_Empty_UserId_Throws_KeyNotFound()
    {
        var h = Build();
        var act = () => h.Service.CreateOrderAsync("", new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 1 } },
            ShippingAddress = ValidAddress()
        });
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CreateOrder_Null_Dto_Throws_ArgumentNull()
    {
        var h = Build();
        var act = () => h.Service.CreateOrderAsync("u1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateOrder_No_Items_Throws_InvalidOperation()
    {
        var h = Build();
        var act = () => h.Service.CreateOrderAsync("u1", new CreateOrderDto
        {
            Items = new List<OrderItemDto>(),
            ShippingAddress = ValidAddress()
        });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at least one item*");
    }

    [Fact]
    public async Task CreateOrder_Zero_Quantity_Throws_InvalidOperation()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", "Widget", 10m, 5) });
        var act = () => h.Service.CreateOrderAsync("u1", new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 0, UnitPrice = 10m } },
            ShippingAddress = ValidAddress()
        });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid quantity*");
    }

    [Fact]
    public async Task CreateOrder_Missing_Product_Throws_KeyNotFound()
    {
        var h = Build();
        var act = () => h.Service.CreateOrderAsync("u1", new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "ghost", Quantity = 1, UnitPrice = 10m } },
            ShippingAddress = ValidAddress()
        });
        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*ghost*");
    }

    [Fact]
    public async Task CreateOrder_Inactive_Product_Throws_InvalidOperation()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", "Widget", 10m, 5, active: false) });
        var act = () => h.Service.CreateOrderAsync("u1", new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 1, UnitPrice = 10m } },
            ShippingAddress = ValidAddress()
        });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not available*");
    }

    [Fact]
    public async Task CreateOrder_Insufficient_Stock_Throws_InvalidOperation()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", "Widget", 10m, 2) });
        var act = () => h.Service.CreateOrderAsync("u1", new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 5, UnitPrice = 10m } },
            ShippingAddress = ValidAddress()
        });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Insufficient stock*");
    }

    [Fact]
    public async Task CreateOrder_Multiplies_Mixed_Line_Items()
    {
        var h = Build(new Dictionary<string, Product>
        {
            ["p1"] = P("p1", "A", 10m, 10),
            ["p2"] = P("p2", "B", 7.5m, 10)
        });
        var dto = new CreateOrderDto
        {
            Items = new List<OrderItemDto>
            {
                new() { ProductId = "p1", Quantity = 2, UnitPrice = 0m },
                new() { ProductId = "p2", Quantity = 4, UnitPrice = 0m }
            },
            ShippingAddress = ValidAddress()
        };

        var result = await h.Service.CreateOrderAsync("u1", dto);

        result.TotalAmount.Should().Be(2 * 10m + 4 * 7.5m);
    }
}

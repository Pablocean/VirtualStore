using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Orders;

/// <summary>Refund flow (ADR-0007): full → Refunded + stock restore, partial → PartiallyRefunded without restore.</summary>
public class OrderRefundTests
{
    private static IMapper RealMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private static AddressDto ValidAddress() => new()
    {
        Street = "123 Main St", City = "Springfield", State = "IL", ZipCode = "62701", Country = "USA"
    };

    private static Order RefundableOrder(OrderStatus status) => new()
    {
        Id = "o1",
        UserId = "u1",
        Status = status,
        TotalAmount = 50m,
        Currency = "usd",
        StripePaymentIntentId = "pi_123",
        Items = new List<OrderItem>
        {
            new() { ProductId = "p1", ProductName = "Widget", UnitPrice = 25m, Quantity = 2 }
        },
        ShippingAddress = new Address
        {
            Street = "123 Main St", City = "Springfield", State = "IL", ZipCode = "62701", Country = "USA"
        }
    };

    private static OrderService RefundService(Order order)
        => RefundService(order, null, out _, out _);

    private static OrderService RefundService(
        Order order,
        Dictionary<string, Product>? products,
        out Mock<IRepository<Product>> productMock,
        out Mock<IRepository<Order>> orderMock)
    {
        var orders = new Mock<IRepository<Order>>();
        orders.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(order);
        orders.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync(order);
        var carts = new Mock<IRepository<Cart>>();
        var prods = new Mock<IRepository<Product>>();
        products ??= new Dictionary<string, Product>();
        prods.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => products.TryGetValue(id, out var p) ? p : null);
        productMock = prods;
        orderMock = orders;
        return new OrderService(orders.Object, carts.Object, prods.Object, RealMapper(),
            context: null, paymentService: Mock.Of<IStripePaymentService>());
    }

    [Fact]
    public async Task Full_Refund_Moves_To_Refunded_Restores_Stock_And_Persists_Refund_Id()
    {
        var order = RefundableOrder(OrderStatus.PaymentReceived);
        var product = new Product { Id = "p1", Name = "Widget", Price = 25m, StockQuantity = 3, CategoryId = "c1", IsActive = true };
        var service = RefundService(order, new Dictionary<string, Product> { ["p1"] = product }, out var prods, out var orders);

        var result = await service.ApplyRefundAsync("o1", "re_1", 50m, fullRefund: true);

        result.Status.Should().Be(OrderStatus.Refunded);
        result.StripeRefundId.Should().Be("re_1");
        result.StripeRefundAmount.Should().Be(50m);
        product.StockQuantity.Should().Be(5, "full refund restores the 2 reserved units");
        prods!.Verify(r => r.UpdateAsync("p1", It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Once);
        orders!.Verify(r => r.UpdateAsync("o1", It.Is<Order>(o => o.Status == OrderStatus.Refunded && o.StripeRefundId == "re_1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Partial_Refund_Moves_To_PartiallyRefunded_Without_Stock_Restore()
    {
        var order = RefundableOrder(OrderStatus.Processing);
        var product = new Product { Id = "p1", Name = "Widget", Price = 25m, StockQuantity = 3, CategoryId = "c1", IsActive = true };
        var service = RefundService(order, new Dictionary<string, Product> { ["p1"] = product }, out var prods, out _);

        var result = await service.ApplyRefundAsync("o1", "re_2", 10m, fullRefund: false);

        result.Status.Should().Be(OrderStatus.PartiallyRefunded);
        result.StripeRefundAmount.Should().Be(10m);
        product.StockQuantity.Should().Be(3, "partial refund is a discount/adjustment, not a return — stock untouched");
        prods!.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Full_Refund_After_Partial_Succeeds()
    {
        var order = RefundableOrder(OrderStatus.PartiallyRefunded);
        order.StripeRefundId = "re_2";
        order.StripeRefundAmount = 10m;
        var product = new Product { Id = "p1", Name = "Widget", Price = 25m, StockQuantity = 3, CategoryId = "c1", IsActive = true };
        var service = RefundService(order, new Dictionary<string, Product> { ["p1"] = product }, out _, out _);

        var result = await service.ApplyRefundAsync("o1", "re_3", 50m, fullRefund: true);

        result.Status.Should().Be(OrderStatus.Refunded);
        product.StockQuantity.Should().Be(5);
    }

    [Fact]
    public async Task Refund_From_Pending_Throws_InvalidOperation()
    {
        var service = RefundService(RefundableOrder(OrderStatus.Pending));

        var act = () => service.ApplyRefundAsync("o1", "re_x", 50m, fullRefund: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cannot transition order from Pending to Refunded*");
    }

    [Fact]
    public async Task Refund_Unknown_Order_Throws_KeyNotFound()
    {
        var orders = new Mock<IRepository<Order>>();
        orders.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Order?)null);
        var service = new OrderService(orders.Object, new Mock<IRepository<Cart>>().Object,
            new Mock<IRepository<Product>>().Object, RealMapper(),
            context: null, paymentService: Mock.Of<IStripePaymentService>());

        var act = () => service.ApplyRefundAsync("missing", "re_x", 1m, fullRefund: false);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task AttachPaymentIntent_Persists_Intent_Id()
    {
        var order = RefundableOrder(OrderStatus.Pending);
        order.StripePaymentIntentId = null;
        var service = RefundService(order);

        var result = await service.AttachPaymentIntentAsync("o1", "pi_new");

        result.StripePaymentIntentId.Should().Be("pi_new");
        order.StripePaymentIntentId.Should().Be("pi_new");
    }

    [Fact]
    public void Refund_Key_Is_Deterministic_Per_Order_And_Amount()
    {
        StripeIdempotency.IntentKey("o1").Should().Be("order:o1:intent");
        StripeIdempotency.RefundKey("o1", null).Should().Be("order:o1:refund:full");
        StripeIdempotency.RefundKey("o1", 10.50m).Should().Be(StripeIdempotency.RefundKey("o1", 10.5m));
        StripeIdempotency.RefundKey("o1", 10m).Should().NotBe(StripeIdempotency.RefundKey("o1", null));
        StripeIdempotency.RefundKey("o1", 10m).Should().NotBe(StripeIdempotency.RefundKey("o2", 10m));
    }

    [Fact]
    public async Task CreateOrder_Calls_Stripe_With_Deterministic_Intent_Key_And_Persists_Intent_Id()
    {
        var product = new Product { Id = "p1", Name = "Widget", Price = 25m, StockQuantity = 10, CategoryId = "c1", IsActive = true };
        var orders = new Mock<IRepository<Order>>();
        Order? added = null;
        orders.Setup(r => r.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback((Order o, CancellationToken _) => added = o).Returns(Task.CompletedTask);
        orders.Setup(r => r.FindOneAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Order, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);
        // Post-commit linkage re-reads the order.
        orders.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => added);
        var carts = new Mock<IRepository<Cart>>();
        carts.Setup(r => r.FindOneAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Cart, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Cart?)null);
        var prods = new Mock<IRepository<Product>>();
        prods.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(product);

        var payments = new Mock<IStripePaymentService>();
        string? seenKey = null;
        payments.Setup(p => p.CreatePaymentIntentAsync(It.IsAny<decimal>(), It.IsAny<string>(), null, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((decimal amt, string cur, string? cust, string? oid, string? key, CancellationToken _) => seenKey = key)
            .ReturnsAsync((decimal amt, string cur, string? cust, string? oid, string? key, CancellationToken _) => new PaymentIntentResultDto
            {
                PaymentIntentId = "pi_det", Amount = amt, Currency = cur, Status = "requires_payment_method"
            });

        var service = new OrderService(orders.Object, carts.Object, prods.Object, RealMapper(),
            context: null, paymentService: payments.Object);

        var result = await service.CreateOrderAsync("u1", new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 2, UnitPrice = 0m } },
            ShippingAddress = ValidAddress()
        });

        seenKey.Should().Be($"order:{result.Id}:intent");
        result.StripePaymentIntentId.Should().Be("pi_det");
        added!.StripePaymentIntentId.Should().Be("pi_det");
        payments.Verify(p => p.CreatePaymentIntentAsync(50m, "usd", null, result.Id, $"order:{result.Id}:intent", It.IsAny<CancellationToken>()), Times.Once);
        orders.Verify(r => r.UpdateAsync(added!.Id, It.Is<Order>(o => o.StripePaymentIntentId == "pi_det"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateOrder_Stripe_Failure_Leaves_Pending_Order_Without_Intent_Id()
    {
        var product = new Product { Id = "p1", Name = "Widget", Price = 10m, StockQuantity = 10, CategoryId = "c1", IsActive = true };
        var orders = new Mock<IRepository<Order>>();
        Order? added = null;
        orders.Setup(r => r.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback((Order o, CancellationToken _) => added = o).Returns(Task.CompletedTask);
        orders.Setup(r => r.FindOneAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Order, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);
        var carts = new Mock<IRepository<Cart>>();
        carts.Setup(r => r.FindOneAsync(It.IsAny<System.Linq.Expressions.Expression<Func<Cart, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Cart?)null);
        var prods = new Mock<IRepository<Product>>();
        prods.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(product);

        var payments = new Mock<IStripePaymentService>();
        payments.Setup(p => p.CreatePaymentIntentAsync(It.IsAny<decimal>(), It.IsAny<string>(), null, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("stripe down"));

        var service = new OrderService(orders.Object, carts.Object, prods.Object, RealMapper(),
            context: null, paymentService: payments.Object);

        var result = await service.CreateOrderAsync("u1", new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 1, UnitPrice = 0m } },
            ShippingAddress = ValidAddress()
        });

        result.Status.Should().Be(OrderStatus.Pending);
        result.StripePaymentIntentId.Should().BeNull();
        added!.Status.Should().Be(OrderStatus.Pending);
    }
}

using System.Linq.Expressions;
using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Orders;

public class OrderStatusTransitionTests
{
    private static IMapper RealMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private static OrderService ServiceFor(Order order, Mock<IRepository<Order>>? orderRepoMock = null)
    {
        orderRepoMock ??= new Mock<IRepository<Order>>();
        orderRepoMock.Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync(order);
        orderRepoMock.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        var cartRepo = new Mock<IRepository<Cart>>();
        var productRepo = new Mock<IRepository<Product>>();
        return new OrderService(
            orderRepoMock.Object, cartRepo.Object, productRepo.Object, RealMapper(),
            context: null, paymentService: Mock.Of<IStripePaymentService>());
    }

    public static TheoryData<OrderStatus, OrderStatus> LegalTransitions => new()
    {
        { OrderStatus.Pending, OrderStatus.PaymentReceived },
        { OrderStatus.Pending, OrderStatus.Cancelled },
        { OrderStatus.PaymentReceived, OrderStatus.Processing },
        { OrderStatus.PaymentReceived, OrderStatus.Cancelled },
        { OrderStatus.PaymentReceived, OrderStatus.Refunded },
        { OrderStatus.PaymentReceived, OrderStatus.PartiallyRefunded },
        { OrderStatus.Processing, OrderStatus.Shipped },
        { OrderStatus.Processing, OrderStatus.Cancelled },
        { OrderStatus.Processing, OrderStatus.Refunded },
        { OrderStatus.Processing, OrderStatus.PartiallyRefunded },
        { OrderStatus.Shipped, OrderStatus.Delivered },
        { OrderStatus.Shipped, OrderStatus.PartiallyRefunded },
        { OrderStatus.Delivered, OrderStatus.Refunded },
        { OrderStatus.PartiallyRefunded, OrderStatus.Refunded },
    };

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public async Task Legal_Transition_Succeeds_And_Persists(OrderStatus from, OrderStatus to)
    {
        var order = new Order { Id = "o1", UserId = "u1", Status = from };
        var repoMock = new Mock<IRepository<Order>>();
        var service = ServiceFor(order, repoMock);

        var result = await service.UpdateOrderStatusAsync("o1", to);

        result.Status.Should().Be(to);
        order.Status.Should().Be(to);
        repoMock.Verify(r => r.UpdateAsync("o1", It.Is<Order>(o => o.Status == to), It.IsAny<CancellationToken>()), Times.Once);
    }

    public static TheoryData<OrderStatus, OrderStatus> IllegalTransitions => new()
    {
        { OrderStatus.Pending, OrderStatus.Processing },
        { OrderStatus.Pending, OrderStatus.Shipped },
        { OrderStatus.Pending, OrderStatus.Delivered },
        { OrderStatus.Pending, OrderStatus.Pending },
        { OrderStatus.Pending, OrderStatus.Refunded },
        { OrderStatus.Pending, OrderStatus.PartiallyRefunded },
        { OrderStatus.PaymentReceived, OrderStatus.PaymentReceived },
        { OrderStatus.PaymentReceived, OrderStatus.Shipped },
        { OrderStatus.PaymentReceived, OrderStatus.Delivered },
        { OrderStatus.PaymentReceived, OrderStatus.Pending },
        { OrderStatus.Processing, OrderStatus.Processing },
        { OrderStatus.Processing, OrderStatus.Delivered },
        { OrderStatus.Processing, OrderStatus.PaymentReceived },
        { OrderStatus.Processing, OrderStatus.Pending },
        { OrderStatus.Shipped, OrderStatus.Cancelled },
        { OrderStatus.Shipped, OrderStatus.Processing },
        { OrderStatus.Shipped, OrderStatus.Pending },
        { OrderStatus.Shipped, OrderStatus.Shipped },
        { OrderStatus.Shipped, OrderStatus.Refunded },
        { OrderStatus.Shipped, OrderStatus.PaymentReceived },
        { OrderStatus.Delivered, OrderStatus.Cancelled },
        { OrderStatus.Delivered, OrderStatus.Shipped },
        { OrderStatus.Delivered, OrderStatus.Pending },
        { OrderStatus.Delivered, OrderStatus.Delivered },
        { OrderStatus.Delivered, OrderStatus.PartiallyRefunded },
        { OrderStatus.Delivered, OrderStatus.Processing },
        { OrderStatus.PartiallyRefunded, OrderStatus.PartiallyRefunded },
        { OrderStatus.PartiallyRefunded, OrderStatus.Processing },
        { OrderStatus.PartiallyRefunded, OrderStatus.Shipped },
        { OrderStatus.PartiallyRefunded, OrderStatus.Delivered },
        { OrderStatus.PartiallyRefunded, OrderStatus.Cancelled },
        { OrderStatus.PartiallyRefunded, OrderStatus.Pending },
        { OrderStatus.PartiallyRefunded, OrderStatus.PaymentReceived },
        { OrderStatus.Refunded, OrderStatus.Pending },
        { OrderStatus.Refunded, OrderStatus.Refunded },
        { OrderStatus.Refunded, OrderStatus.PartiallyRefunded },
        { OrderStatus.Refunded, OrderStatus.Delivered },
        { OrderStatus.Cancelled, OrderStatus.Pending },
        { OrderStatus.Cancelled, OrderStatus.Processing },
        { OrderStatus.Cancelled, OrderStatus.Delivered },
        { OrderStatus.Cancelled, OrderStatus.Refunded },
        { OrderStatus.Cancelled, OrderStatus.PartiallyRefunded },
    };

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public async Task Illegal_Transition_Throws_InvalidOperation(OrderStatus from, OrderStatus to)
    {
        var order = new Order { Id = "o1", UserId = "u1", Status = from };
        var service = ServiceFor(order);

        var act = () => service.UpdateOrderStatusAsync("o1", to);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*Cannot transition order from {from} to {to}*");
    }

    [Fact]
    public async Task UpdateOrderStatus_Unknown_Order_Throws_KeyNotFound()
    {
        var repoMock = new Mock<IRepository<Order>>();
        repoMock.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync((Order?)null);
        repoMock.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Order?)null);
        var service = new OrderService(repoMock.Object, new Mock<IRepository<Cart>>().Object,
            new Mock<IRepository<Product>>().Object, RealMapper(),
            context: null, paymentService: Mock.Of<IStripePaymentService>());

        var act = () => service.UpdateOrderStatusAsync("missing", OrderStatus.Cancelled);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}

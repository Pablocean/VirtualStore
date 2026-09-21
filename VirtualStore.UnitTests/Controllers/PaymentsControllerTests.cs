using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using VirtualStore.API.Controllers;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using Xunit;

namespace VirtualStore.UnitTests.Controllers;

public class PaymentsControllerTests
{
    private static (PaymentsController Controller, Mock<IStripePaymentService> Payments, Mock<IOrderService> Orders) Create()
    {
        var payments = new Mock<IStripePaymentService>();
        var orders = new Mock<IOrderService>();
        var controller = new PaymentsController(payments.Object, orders.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (controller, payments, orders);
    }

    [Fact]
    public void Ctor_NullPaymentService_Throws()
    {
        var act = () => new PaymentsController(null!, new Mock<IOrderService>().Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOrderService_Throws()
    {
        var act = () => new PaymentsController(new Mock<IStripePaymentService>().Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateIntent_WithoutOrderId_Uses_NullKey_And_Skips_Attach()
    {
        var (controller, payments, orders) = Create();
        var dto = new CreatePaymentIntentDto { Amount = 10m, Currency = "usd", CustomerId = "cus_1" };
        var result = new PaymentIntentResultDto { PaymentIntentId = "pi_1", Amount = 10m, Currency = "usd" };
        using var cts = new CancellationTokenSource();
        payments.Setup(p => p.CreatePaymentIntentAsync(10m, "usd", "cus_1", null, null, cts.Token)).ReturnsAsync(result);

        var action = await controller.CreatePaymentIntent(dto, cts.Token);

        action.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(result);
        orders.Verify(o => o.AttachPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateIntent_WithOrderId_Uses_Deterministic_Key_And_Attaches()
    {
        var (controller, payments, orders) = Create();
        var dto = new CreatePaymentIntentDto { Amount = 20m, Currency = "usd", OrderId = "o1" };
        var result = new PaymentIntentResultDto { PaymentIntentId = "pi_9", Amount = 20m, Currency = "usd" };
        using var cts = new CancellationTokenSource();
        payments.Setup(p => p.CreatePaymentIntentAsync(20m, "usd", null, "o1", "order:o1:intent", cts.Token)).ReturnsAsync(result);
        orders.Setup(o => o.AttachPaymentIntentAsync("o1", "pi_9", cts.Token)).ReturnsAsync(new OrderDto { Id = "o1" });

        var action = await controller.CreatePaymentIntent(dto, cts.Token);

        action.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(result);
        orders.Verify(o => o.AttachPaymentIntentAsync("o1", "pi_9", cts.Token), Times.Once);
    }

    [Fact]
    public async Task Refund_OrderNull_Returns_NotFound()
    {
        var (controller, _, orders) = Create();
        orders.Setup(o => o.GetOrderByIdAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((OrderDto?)null);

        var result = await controller.RefundOrder("missing", null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refund_MissingIntentId_Returns_400(string? intentId)
    {
        var (controller, payments, orders) = Create();
        orders.Setup(o => o.GetOrderByIdAsync("o1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderDto { Id = "o1", StripePaymentIntentId = intentId });

        var result = await controller.RefundOrder("o1", null, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<ProblemDetails>().Subject.Detail.Should().Be("Order has no payment intent to refund.");
        payments.Verify(p => p.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refund_NullDto_Is_FullRefund_With_Full_Key()
    {
        var (controller, payments, orders) = Create();
        using var cts = new CancellationTokenSource();
        orders.Setup(o => o.GetOrderByIdAsync("o1", cts.Token))
            .ReturnsAsync(new OrderDto { Id = "o1", StripePaymentIntentId = "pi_1" });
        var refund = new PaymentIntentResultDto { PaymentIntentId = "pi_1", RefundId = "re_1", Amount = 25m };
        payments.Setup(p => p.RefundPaymentAsync("pi_1", null, "order:o1:refund:full", cts.Token)).ReturnsAsync(refund);
        orders.Setup(o => o.ApplyRefundAsync("o1", "re_1", 25m, true, cts.Token)).ReturnsAsync(new OrderDto { Id = "o1" });

        var result = await controller.RefundOrder("o1", null, cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(refund);
        orders.Verify(o => o.ApplyRefundAsync("o1", "re_1", 25m, true, cts.Token), Times.Once);
    }

    [Fact]
    public async Task Refund_WithAmount_Is_PartialRefund_With_Amount_Key()
    {
        var (controller, payments, orders) = Create();
        using var cts = new CancellationTokenSource();
        orders.Setup(o => o.GetOrderByIdAsync("o1", cts.Token))
            .ReturnsAsync(new OrderDto { Id = "o1", StripePaymentIntentId = "pi_1" });
        var refund = new PaymentIntentResultDto { PaymentIntentId = "pi_1", RefundId = "re_2", Amount = 5m };
        payments.Setup(p => p.RefundPaymentAsync("pi_1", 5m, "order:o1:refund:5", cts.Token)).ReturnsAsync(refund);
        orders.Setup(o => o.ApplyRefundAsync("o1", "re_2", 5m, false, cts.Token)).ReturnsAsync(new OrderDto { Id = "o1" });

        var result = await controller.RefundOrder("o1", new RefundPaymentDto { Amount = 5m }, cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(refund);
        orders.Verify(o => o.ApplyRefundAsync("o1", "re_2", 5m, false, cts.Token), Times.Once);
    }
}

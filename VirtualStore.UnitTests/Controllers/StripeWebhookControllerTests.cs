using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Stripe;
using VirtualStore.API.Controllers;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Enums;
using Xunit;

namespace VirtualStore.UnitTests.Controllers;

public class StripeWebhookControllerTests
{
    private static (StripeWebhookController Controller, Mock<IStripePaymentService> Payments, Mock<IOrderService> Orders, DefaultHttpContext Http) Create(
        string json = "{}",
        string? signature = "sig")
    {
        var payments = new Mock<IStripePaymentService>();
        var orders = new Mock<IOrderService>();
        var logger = new Mock<ILogger<StripeWebhookController>>();
        var controller = new StripeWebhookController(payments.Object, orders.Object, logger.Object);
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        if (signature is not null)
            http.Request.Headers["Stripe-Signature"] = signature;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, payments, orders, http);
    }

    [Fact]
    public void Ctor_NullPaymentService_Throws()
    {
        var act = () => new StripeWebhookController(null!, new Mock<IOrderService>().Object, new Mock<ILogger<StripeWebhookController>>().Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOrderService_Throws()
    {
        var act = () => new StripeWebhookController(new Mock<IStripePaymentService>().Object, null!, new Mock<ILogger<StripeWebhookController>>().Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var act = () => new StripeWebhookController(new Mock<IStripePaymentService>().Object, new Mock<IOrderService>().Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task InvalidSignature_Returns_400()
    {
        var (controller, payments, _, _) = Create();
        payments.Setup(p => p.HandleWebhookEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException("bad sig"));

        var result = await controller.HandleWebhook(CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var problem = bad.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Detail.Should().Be("Invalid webhook signature.");
    }

    [Fact]
    public async Task Duplicate_Is_Acked_Without_State_Change()
    {
        var (controller, payments, orders, _) = Create();
        var dup = new StripeWebhookResultDto { EventType = "payment_intent.succeeded", Succeeded = true, OrderId = "o1", Duplicate = true };
        payments.Setup(p => p.HandleWebhookEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(dup);

        var result = await controller.HandleWebhook(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(dup);
        orders.Verify(o => o.UpdateOrderStatusAsync(It.IsAny<string>(), It.IsAny<OrderStatus>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Succeeded_With_OrderId_Marks_PaymentReceived()
    {
        var (controller, payments, orders, _) = Create();
        using var cts = new CancellationTokenSource();
        var dto = new StripeWebhookResultDto { EventType = "payment_intent.succeeded", Succeeded = true, OrderId = "o1" };
        payments.Setup(p => p.HandleWebhookEventAsync("{}", "sig", cts.Token)).ReturnsAsync(dto);
        orders.Setup(o => o.UpdateOrderStatusAsync("o1", OrderStatus.PaymentReceived, cts.Token)).ReturnsAsync(new OrderDto { Id = "o1" });

        var result = await controller.HandleWebhook(cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(dto);
        orders.Verify(o => o.UpdateOrderStatusAsync("o1", OrderStatus.PaymentReceived, cts.Token), Times.Once);
    }

    [Theory]
    [InlineData("payment_intent.payment_failed")]
    [InlineData("charge.refunded")]
    public async Task Failed_Or_Refunded_With_OrderId_Marks_Cancelled(string eventType)
    {
        var (controller, payments, orders, _) = Create();
        var dto = new StripeWebhookResultDto { EventType = eventType, Succeeded = false, OrderId = "o1" };
        payments.Setup(p => p.HandleWebhookEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(dto);
        orders.Setup(o => o.UpdateOrderStatusAsync("o1", OrderStatus.Cancelled, It.IsAny<CancellationToken>())).ReturnsAsync(new OrderDto { Id = "o1" });

        var result = await controller.HandleWebhook(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        orders.Verify(o => o.UpdateOrderStatusAsync("o1", OrderStatus.Cancelled, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Succeeded_Without_OrderId_Applies_No_State_Change()
    {
        var (controller, payments, orders, _) = Create();
        var dto = new StripeWebhookResultDto { EventType = "payment_intent.succeeded", Succeeded = true, OrderId = null };
        payments.Setup(p => p.HandleWebhookEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(dto);

        var result = await controller.HandleWebhook(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(dto);
        orders.Verify(o => o.UpdateOrderStatusAsync(It.IsAny<string>(), It.IsAny<OrderStatus>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unknown_Failed_Event_Applies_No_State_Change_But_Still_200()
    {
        var (controller, payments, orders, _) = Create();
        var dto = new StripeWebhookResultDto { EventType = "customer.created", Succeeded = false, OrderId = "o1" };
        payments.Setup(p => p.HandleWebhookEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(dto);

        var result = await controller.HandleWebhook(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(dto);
        orders.Verify(o => o.UpdateOrderStatusAsync(It.IsAny<string>(), It.IsAny<OrderStatus>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failed_Without_OrderId_Applies_No_State_Change()
    {
        var (controller, payments, orders, _) = Create();
        var dto = new StripeWebhookResultDto { EventType = "payment_intent.payment_failed", Succeeded = false, OrderId = null };
        payments.Setup(p => p.HandleWebhookEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(dto);

        var result = await controller.HandleWebhook(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        orders.Verify(o => o.UpdateOrderStatusAsync(It.IsAny<string>(), It.IsAny<OrderStatus>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Order_Transition_Failures_Are_Swallowed_With_200(Type exceptionType)
    {
        var (controller, payments, orders, _) = Create();
        var dto = new StripeWebhookResultDto { EventType = "payment_intent.succeeded", Succeeded = true, OrderId = "o1" };
        payments.Setup(p => p.HandleWebhookEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(dto);
        orders.Setup(o => o.UpdateOrderStatusAsync(It.IsAny<string>(), It.IsAny<OrderStatus>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "nope")!);

        var result = await controller.HandleWebhook(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task Unexpected_Failure_Is_Acked_With_200_Unknown()
    {
        var (controller, payments, _, _) = Create();
        payments.Setup(p => p.HandleWebhookEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        var result = await controller.HandleWebhook(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<StripeWebhookResultDto>().Subject;
        dto.EventType.Should().Be("unknown");
        dto.Succeeded.Should().BeFalse();
        dto.OrderId.Should().BeNull();
    }
}

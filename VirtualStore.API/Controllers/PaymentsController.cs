using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;

namespace VirtualStore.API.Controllers;

/// <summary>Payment intent creation and refund endpoints.</summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly IStripePaymentService _paymentService;
    private readonly IOrderService _orderService;

    public PaymentsController(IStripePaymentService paymentService, IOrderService orderService)
    {
        _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
        _orderService = orderService ?? throw new ArgumentNullException(nameof(orderService));
    }

    /// <summary>
    /// Creates a Stripe payment intent. When <c>orderId</c> is supplied the intent
    /// is created with the deterministic key <c>order:{orderId}:intent</c>, the
    /// order id is stored in Stripe metadata, and the intent id is persisted on
    /// the order (retry path for checkouts whose post-commit linkage failed).
    /// </summary>
    [HttpPost("intent")]
    [ProducesResponseType(typeof(PaymentIntentResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePaymentIntent([FromBody] CreatePaymentIntentDto dto, CancellationToken cancellationToken)
    {
        string? key = dto.OrderId is not null ? StripeIdempotency.IntentKey(dto.OrderId) : null;
        var result = await _paymentService.CreatePaymentIntentAsync(dto.Amount, dto.Currency, dto.CustomerId, dto.OrderId, key, cancellationToken);

        if (dto.OrderId is not null)
            await _orderService.AttachPaymentIntentAsync(dto.OrderId, result.PaymentIntentId, cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Refunds the payment attached to an order. Admins only. Full refunds move
    /// the order to <c>Refunded</c> and restore stock; partial refunds move it to
    /// <c>PartiallyRefunded</c> with no stock restore (partial = discount/
    /// adjustment on kept goods, not a return — see ADR-0007).
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPost("orders/{id}/refund")]
    [ProducesResponseType(typeof(PaymentIntentResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RefundOrder(string id, [FromBody] RefundPaymentDto? dto, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrderByIdAsync(id, cancellationToken);
        if (order is null) return NotFound();
        if (string.IsNullOrWhiteSpace(order.StripePaymentIntentId))
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = "Order has no payment intent to refund.",
                Instance = HttpContext.Request.Path
            });

        var fullRefund = dto?.Amount is null;
        var key = StripeIdempotency.RefundKey(id, dto?.Amount);
        var result = await _paymentService.RefundPaymentAsync(order.StripePaymentIntentId, dto?.Amount, key, cancellationToken);

        await _orderService.ApplyRefundAsync(id, result.RefundId, dto?.Amount ?? result.Amount, fullRefund, cancellationToken);

        return Ok(result);
    }
}

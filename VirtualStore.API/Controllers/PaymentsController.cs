using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    /// <summary>Creates a Stripe payment intent for an amount. Any authenticated user.</summary>
    [HttpPost("intent")]
    [ProducesResponseType(typeof(PaymentIntentResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePaymentIntent([FromBody] CreatePaymentIntentDto dto, CancellationToken cancellationToken)
    {
        var result = await _paymentService.CreatePaymentIntentAsync(dto.Amount, dto.Currency, dto.CustomerId, dto.OrderId, cancellationToken);
        return Ok(result);
    }

    /// <summary>Refunds the payment attached to an order. Admins only.</summary>
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
            return BadRequest(new { message = "Order has no payment intent to refund." });

        var result = await _paymentService.RefundPaymentAsync(order.StripePaymentIntentId, dto?.Amount, cancellationToken);
        return Ok(result);
    }
}

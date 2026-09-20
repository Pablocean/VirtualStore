using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stripe;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Enums;

namespace VirtualStore.API.Controllers;

/// <summary>Stripe webhook receiver. Verifies the signature and mirrors payment state into orders.</summary>
[ApiController]
[Route("api/stripe/webhook")]
public class StripeWebhookController : ControllerBase
{
    private readonly IStripePaymentService _paymentService;
    private readonly IOrderService _orderService;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(
        IStripePaymentService paymentService,
        IOrderService orderService,
        ILogger<StripeWebhookController> logger)
    {
        _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
        _orderService = orderService ?? throw new ArgumentNullException(nameof(orderService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Handles incoming Stripe webhook events (anonymous; authenticated via signature).</summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(StripeWebhookResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> HandleWebhook(CancellationToken cancellationToken)
    {
        string json;
        using (var reader = new StreamReader(Request.Body))
            json = await reader.ReadToEndAsync(cancellationToken);

        var signature = Request.Headers["Stripe-Signature"].ToString();

        StripeWebhookResultDto result;
        try
        {
            result = await _paymentService.HandleWebhookEventAsync(json, signature, cancellationToken);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Rejected Stripe webhook with invalid signature.");
            return BadRequest(new { message = "Invalid webhook signature." });
        }

        // Mirror payment state into the linked order. Webhooks must answer 200 even when
        // the order update cannot be applied, so failures are logged, not re-thrown.
        try
        {
            if (result.Succeeded && result.OrderId is not null)
            {
                await _orderService.UpdateOrderStatusAsync(result.OrderId, OrderStatus.PaymentReceived, cancellationToken);
            }
            else if (!result.Succeeded && result.OrderId is not null &&
                (result.EventType == "payment_intent.payment_failed" || result.EventType == "charge.refunded"))
            {
                await _orderService.UpdateOrderStatusAsync(result.OrderId, OrderStatus.Cancelled, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Stripe webhook {EventType} for order {OrderId} could not be applied.", result.EventType, result.OrderId);
        }

        return Ok(result);
    }
}

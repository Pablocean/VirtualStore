namespace VirtualStore.Application.DTOs;

/// <summary>Result of a Stripe payment intent creation.</summary>
public class PaymentIntentResultDto
{
    public string PaymentIntentId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>Normalized result of a Stripe webhook event (no Stripe SDK types leak).</summary>
public class StripeWebhookResultDto
{
    public string EventType { get; set; } = string.Empty;
    public string? PaymentIntentId { get; set; }
    public string? OrderId { get; set; }
    public bool Succeeded { get; set; }
}

/// <summary>Payload for creating a Stripe payment intent.</summary>
public class CreatePaymentIntentDto
{
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "usd";
    public string? CustomerId { get; set; }
    public string? OrderId { get; set; }
}

/// <summary>Payload for refunding a payment. Null amount means full refund.</summary>
public class RefundPaymentDto
{
    public decimal? Amount { get; set; }
}

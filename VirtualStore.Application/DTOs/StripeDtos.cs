namespace VirtualStore.Application.DTOs;

/// <summary>Result of a Stripe payment intent creation.</summary>
public class PaymentIntentResultDto
{
    public string PaymentIntentId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// Stripe refund id (<c>re_…</c>) when this result represents a refund
    /// (ADR-0007). Null for payment-intent creations.
    /// </summary>
    public string? RefundId { get; set; }
}

/// <summary>Normalized result of a Stripe webhook event (no Stripe SDK types leak).</summary>
public class StripeWebhookResultDto
{
    public string EventType { get; set; } = string.Empty;
    public string? PaymentIntentId { get; set; }
    public string? OrderId { get; set; }
    public bool Succeeded { get; set; }
    /// <summary>
    /// True when this delivery duplicates an already-processed Stripe event id
    /// (ADR-0007). Duplicates are acked with 200 and apply no state change.
    /// </summary>
    public bool Duplicate { get; set; }
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

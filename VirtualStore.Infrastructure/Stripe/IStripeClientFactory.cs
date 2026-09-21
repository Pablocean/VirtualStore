using Stripe;

namespace VirtualStore.Infrastructure.Stripe;

/// <summary>
/// Wave T0 testability seam over <c>new StripeClient/PaymentIntentService/RefundService</c>
/// and <see cref="EventUtility.ConstructEvent"/> signature validation.
/// The default implementation preserves production behavior exactly;
/// tests inject a fake to avoid Stripe SDK I/O.
/// </summary>
public interface IStripeClientFactory
{
    PaymentIntentService CreatePaymentIntentService(string secretKey);

    RefundService CreateRefundService(string secretKey);

    Event ConstructEvent(string json, string signatureHeader, string webhookSecret);
}

/// <summary>
/// Default production implementation: real Stripe SDK clients + real signature validation.
/// </summary>
public sealed class StripeClientFactory : IStripeClientFactory
{
    public PaymentIntentService CreatePaymentIntentService(string secretKey)
        => new(new StripeClient(secretKey));

    public RefundService CreateRefundService(string secretKey)
        => new(new StripeClient(secretKey));

    public Event ConstructEvent(string json, string signatureHeader, string webhookSecret)
        => EventUtility.ConstructEvent(json, signatureHeader, webhookSecret);
}

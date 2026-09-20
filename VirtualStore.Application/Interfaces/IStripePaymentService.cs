using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Interfaces;

public interface IStripePaymentService
{
    Task<PaymentIntentResultDto> CreatePaymentIntentAsync(decimal amount, string currency, string? customerId = null, string? orderId = null, CancellationToken cancellationToken = default);
    Task<bool> ConfirmPaymentAsync(string paymentIntentId, CancellationToken cancellationToken = default);
    Task<PaymentIntentResultDto> RefundPaymentAsync(string paymentIntentId, decimal? amount = null, CancellationToken cancellationToken = default);
    Task<StripeWebhookResultDto> HandleWebhookEventAsync(string json, string signatureHeader, CancellationToken cancellationToken = default);
}

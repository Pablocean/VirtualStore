
using Stripe;

namespace VirtualStore.Application.Interfaces;

public interface IStripePaymentService
{
    Task<PaymentIntent> CreatePaymentIntentAsync(decimal amount, string currency, string? customerId = null);
    Task<bool> ConfirmPaymentAsync(string paymentIntentId);
}
using Microsoft.Extensions.Options;
using Stripe;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Settings;

namespace VirtualStore.Infrastructure.Stripe;

public class StripePaymentService : IStripePaymentService
{
    public StripePaymentService(IOptions<StripeSettings> options)
    {
        StripeConfiguration.ApiKey = options.Value.SecretKey;
    }

    public async Task<PaymentIntent> CreatePaymentIntentAsync(decimal amount, string currency, string? customerId = null)
    {
        var options = new PaymentIntentCreateOptions
        {
            Amount = (long)(amount * 100), // Stripe works in cents
            Currency = currency,
            Customer = customerId,
            PaymentMethodTypes = new List<string> { "card" }
        };
        var service = new PaymentIntentService();
        return await service.CreateAsync(options);
    }

    public async Task<bool> ConfirmPaymentAsync(string paymentIntentId)
    {
        var service = new PaymentIntentService();
        var paymentIntent = await service.GetAsync(paymentIntentId);
        return paymentIntent.Status == "succeeded";
    }
}
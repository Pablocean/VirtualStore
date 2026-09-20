using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Data;
using VirtualStore.Infrastructure.Stripe;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// Webhook redelivery dedup against a live replica-set container (ADR-0007).
/// Same Stripe event delivered twice → single order transition.
/// Skipped when Docker is unavailable (see <see cref="RequiresDockerFactAttribute"/>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class WebhookRedeliveryTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string WebhookSecret = "whsec_integration_dedup";

    private readonly CustomWebApplicationFactory _factory;

    public WebhookRedeliveryTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string Sign(string payload, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signed = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
        return $"t={timestamp},v1={sig}";
    }

    [RequiresDockerFact]
    public async Task Webhook_Redelivery_Twice_Applies_Single_Transition()
    {
        using var scope = _factory.Services.CreateScope();
        var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var products = scope.ServiceProvider.GetRequiredService<IRepository<Product>>();
        var orderRepo = scope.ServiceProvider.GetRequiredService<IRepository<Order>>();
        var webhookRepo = scope.ServiceProvider.GetRequiredService<IRepository<ProcessedWebhookEvent>>();
        var context = scope.ServiceProvider.GetRequiredService<MongoDbContext>();

        await context.EnsureIndexesAsync();

        var product = new Product
        {
            Name = "Webhook Widget",
            Price = 20m,
            StockQuantity = 5,
            CategoryId = "cat1",
            IsActive = true
        };
        await products.AddAsync(product);

        var order = await orders.CreateOrderAsync($"webhook-user-{Guid.NewGuid():N}", new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = product.Id, Quantity = 1, UnitPrice = 0m } },
            ShippingAddress = new AddressDto
            {
                Street = "123 Main St", City = "Springfield", State = "IL", ZipCode = "62701", Country = "USA"
            }
        });
        order.Status.Should().Be(OrderStatus.Pending);

        var eventId = $"evt_{Guid.NewGuid():N}";
        var paymentIntentId = $"pi_{Guid.NewGuid():N}";
        var json = "{\"id\":\"" + eventId + "\",\"object\":\"event\",\"api_version\":\"2024-12-18.acacia\",\"created\":1680000000,"
            + "\"data\":{\"object\":{\"id\":\"" + paymentIntentId + "\",\"object\":\"payment_intent\",\"amount\":2000,"
            + "\"currency\":\"usd\",\"status\":\"succeeded\",\"client_secret\":\"" + paymentIntentId + "_secret\","
            + "\"metadata\":{\"orderId\":\"" + order.Id + "\"}}},"
            + "\"livemode\":false,\"pending_webhooks\":1,"
            + "\"request\":{\"id\":null,\"idempotency_key\":null},\"type\":\"payment_intent.succeeded\"}";
        var sig = Sign(json, WebhookSecret);

        var stripe = new StripePaymentService(
            Options.Create(new StripeSettings { SecretKey = "sk_test_dummy", WebhookSecret = WebhookSecret }),
            webhookRepo);

        var first = await stripe.HandleWebhookEventAsync(json, sig);
        first.Duplicate.Should().BeFalse();
        first.Succeeded.Should().BeTrue();
        first.OrderId.Should().Be(order.Id);

        await orders.UpdateOrderStatusAsync(order.Id, OrderStatus.PaymentReceived);

        var second = await stripe.HandleWebhookEventAsync(json, sig);
        second.Duplicate.Should().BeTrue("redelivery of the same Stripe event id must be ignored");

        var after = await orderRepo.GetByIdAsync(order.Id);
        after!.Status.Should().Be(OrderStatus.PaymentReceived, "the transition must be applied exactly once");

        var seen = await webhookRepo.FindAsync(e => e.EventId == eventId);
        seen.Should().ContainSingle();
    }
}

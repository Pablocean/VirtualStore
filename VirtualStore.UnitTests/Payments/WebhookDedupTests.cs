using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Stripe;
using Xunit;

namespace VirtualStore.UnitTests.Payments;

/// <summary>Webhook dedup (ADR-0007): same Stripe event id delivered twice → second is Duplicate.</summary>
public class WebhookDedupTests
{
    private const string WebhookSecret = "whsec_test_dedup";

    private static string Sign(string payload, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signed = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
        return $"t={timestamp},v1={sig}";
    }

    private static string EventJson(string eventId, string orderId) =>
        "{\"id\":\"" + eventId + "\",\"object\":\"event\",\"api_version\":\"2024-12-18.acacia\",\"created\":1680000000,"
        + "\"data\":{\"object\":{\"id\":\"pi_123\",\"object\":\"payment_intent\",\"amount\":2000,"
        + "\"currency\":\"usd\",\"status\":\"succeeded\",\"client_secret\":\"pi_123_secret\","
        + "\"metadata\":{\"orderId\":\"" + orderId + "\"}}},"
        + "\"livemode\":false,\"pending_webhooks\":1,"
        + "\"request\":{\"id\":null,\"idempotency_key\":null},\"type\":\"payment_intent.succeeded\"}";

    private static StripePaymentService ServiceWithStore(
        List<ProcessedWebhookEvent> store, out Mock<IRepository<ProcessedWebhookEvent>> repoMock)
    {
        var repo = new Mock<IRepository<ProcessedWebhookEvent>>();
        repo.Setup(r => r.FindOneAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<ProcessedWebhookEvent, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((System.Linq.Expressions.Expression<Func<ProcessedWebhookEvent, bool>> pred, CancellationToken _) =>
                store.FirstOrDefault(pred.Compile()));
        repo.Setup(r => r.AddAsync(It.IsAny<ProcessedWebhookEvent>(), It.IsAny<CancellationToken>()))
            .Callback((ProcessedWebhookEvent e, CancellationToken _) => store.Add(e))
            .Returns(Task.CompletedTask);
        repoMock = repo;
        return new StripePaymentService(Options.Create(new StripeSettings { SecretKey = "sk_test_x", WebhookSecret = WebhookSecret }), repo.Object);
    }

    [Fact]
    public async Task Redelivered_Event_Returns_Duplicate_Without_Reprocessing()
    {
        var store = new List<ProcessedWebhookEvent>();
        var service = ServiceWithStore(store, out var repo);

        var json = EventJson("evt_dup_1", "o1");
        var sig = Sign(json, WebhookSecret);

        var first = await service.HandleWebhookEventAsync(json, sig);
        first.Duplicate.Should().BeFalse();
        first.Succeeded.Should().BeTrue("first delivery processes normally");
        first.OrderId.Should().Be("o1");
        store.Should().ContainSingle(e => e.EventId == "evt_dup_1");

        var second = await service.HandleWebhookEventAsync(json, sig);
        second.Duplicate.Should().BeTrue();
        second.OrderId.Should().BeNull("duplicates apply no state change");
        store.Should().ContainSingle("event recorded exactly once");
        repo.Verify(r => r.AddAsync(It.IsAny<ProcessedWebhookEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Distinct_Events_Are_Each_Recorded()
    {
        var store = new List<ProcessedWebhookEvent>();
        var service = ServiceWithStore(store, out _);

        foreach (var id in new[] { "evt_a", "evt_b" })
        {
            var json = EventJson(id, "o1");
            var result = await service.HandleWebhookEventAsync(json, Sign(json, WebhookSecret));
            result.Duplicate.Should().BeFalse();
        }

        store.Should().HaveCount(2);
    }
}

using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using MongoDB.Driver;
using Stripe;
using VirtualStore.Application.DTOs;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Stripe;
using Xunit;

namespace VirtualStore.UnitTests.Services;

public class WebhookHandlerTests
{
    private static readonly StripeSettings Settings = new()
    {
        SecretKey = "sk_test_x",
        WebhookSecret = "whsec_test"
    };

    private sealed record Harness(
        StripePaymentService Service,
        Mock<IStripeClientFactory> Factory,
        Mock<IRepository<ProcessedWebhookEvent>> Repo,
        List<ProcessedWebhookEvent> Store);

    private static Harness Build(Event evt, bool withRepo = true)
    {
        var factory = new Mock<IStripeClientFactory>();
        factory.Setup(f => f.ConstructEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(evt);

        var store = new List<ProcessedWebhookEvent>();
        var repo = new Mock<IRepository<ProcessedWebhookEvent>>();
        repo.Setup(r => r.FindOneAsync(
                It.IsAny<Expression<Func<ProcessedWebhookEvent, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<ProcessedWebhookEvent, bool>> pred, CancellationToken _) =>
                store.FirstOrDefault(pred.Compile()));
        repo.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<ProcessedWebhookEvent, bool>>>()))
            .ReturnsAsync((Expression<Func<ProcessedWebhookEvent, bool>> pred) =>
                store.FirstOrDefault(pred.Compile()));
        repo.Setup(r => r.AddAsync(It.IsAny<ProcessedWebhookEvent>(), It.IsAny<CancellationToken>()))
            .Callback((ProcessedWebhookEvent e, CancellationToken _) => store.Add(e))
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.AddAsync(It.IsAny<ProcessedWebhookEvent>()))
            .Callback((ProcessedWebhookEvent e) => store.Add(e))
            .Returns(Task.CompletedTask);

        var service = withRepo
            ? new StripePaymentService(Options.Create(Settings), repo.Object, stripeFactory: factory.Object)
            : new StripePaymentService(Options.Create(Settings), stripeFactory: factory.Object);
        return new Harness(service, factory, repo, store);
    }

    private static Event SucceededEvent(string eventId, string? orderId, object? rawObject = null)
    {
        var metadata = orderId is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["orderId"] = orderId };
        var pi = new PaymentIntent
        {
            Id = "pi_123",
            ClientSecret = "pi_123_secret",
            Amount = 2000,
            Currency = "usd",
            Status = "succeeded",
            Metadata = metadata
        };
        return new Event
        {
            Id = eventId,
            Type = "payment_intent.succeeded",
            Data = new EventData { Object = (IHasObject)(rawObject ?? pi) }
        };
    }

    private static Event FailedEvent(string eventId, string? orderId)
    {
        var pi = new PaymentIntent
        {
            Id = "pi_456",
            Amount = 1500,
            Currency = "usd",
            Status = "requires_payment_method",
            Metadata = orderId is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["orderId"] = orderId }
        };
        return new Event
        {
            Id = eventId,
            Type = "payment_intent.payment_failed",
            Data = new EventData { Object = pi }
        };
    }

    private static Event RefundedEvent(string eventId, string? orderId, object? rawObject = null)
    {
        var charge = new Charge
        {
            Id = "ch_123",
            PaymentIntentId = "pi_789",
            Metadata = orderId is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["orderId"] = orderId }
        };
        return new Event
        {
            Id = eventId,
            Type = "charge.refunded",
            Data = new EventData { Object = (IHasObject)(rawObject ?? charge) }
        };
    }

    private static Event UnknownEvent(string eventId) => new()
    {
        Id = eventId,
        Type = "customer.created",
        Data = new EventData { Object = new PaymentIntent { Id = "pi_x" } }
    };

    // ---------- cancellation ----------

    [Fact]
    public async Task Cancelled_Token_Throws_Before_Factory()
    {
        var h = Build(SucceededEvent("evt_c", "o1"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => h.Service.HandleWebhookEventAsync("{}", "sig", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Factory.Verify(f => f.ConstructEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ---------- dedup skip arms ----------

    [Fact]
    public async Task Null_Repo_Skips_Dedup_And_Processes()
    {
        var h = Build(SucceededEvent("evt_norepo", "o1"), withRepo: false);

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Duplicate.Should().BeFalse();
        result.Succeeded.Should().BeTrue();
        result.OrderId.Should().Be("o1");
        result.PaymentIntentId.Should().Be("pi_123");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_Or_Whitespace_EventId_Skips_Dedup_Store_Write(string eventId)
    {
        var h = Build(SucceededEvent(eventId, "o1"));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Duplicate.Should().BeFalse();
        result.Succeeded.Should().BeTrue();
        h.Store.Should().BeEmpty("events without an id cannot be deduplicated");
        h.Repo.Verify(r => r.AddAsync(It.IsAny<ProcessedWebhookEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task First_Delivery_Records_Event_And_Processes()
    {
        var h = Build(SucceededEvent("evt_first", "o1"));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Duplicate.Should().BeFalse();
        result.EventType.Should().Be("payment_intent.succeeded");
        h.Store.Should().ContainSingle(e => e.EventId == "evt_first");
        h.Store.Single().ReceivedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Seen_Event_Returns_Duplicate_Early()
    {
        var h = Build(SucceededEvent("evt_seen", "o1"));
        h.Store.Add(new ProcessedWebhookEvent { EventId = "evt_seen", Type = "payment_intent.succeeded" });

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Duplicate.Should().BeTrue();
        result.EventType.Should().Be("payment_intent.succeeded");
        result.PaymentIntentId.Should().BeNull();
        result.OrderId.Should().BeNull();
        result.Succeeded.Should().BeFalse();
        h.Repo.Verify(r => r.AddAsync(It.IsAny<ProcessedWebhookEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Concurrent_Race_Lost_On_Add_Returns_Duplicate()
    {
        var h = Build(SucceededEvent("evt_race", "o1"));
        h.Repo.Setup(r => r.AddAsync(It.IsAny<ProcessedWebhookEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MongoException("duplicate key"));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Duplicate.Should().BeTrue();
        result.OrderId.Should().BeNull("duplicates apply no state change");
    }

    // ---------- switch matrix ----------

    [Fact]
    public async Task Succeeded_With_OrderId_Maps_All_Fields()
    {
        var h = Build(SucceededEvent("evt_s1", "order-1"));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Should().BeEquivalentTo(new StripeWebhookResultDto
        {
            EventType = "payment_intent.succeeded",
            PaymentIntentId = "pi_123",
            OrderId = "order-1",
            Succeeded = true,
            Duplicate = false
        });
    }

    [Fact]
    public async Task Succeeded_Without_OrderId_Maps_Null_Order()
    {
        var h = Build(SucceededEvent("evt_s2", null));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Succeeded.Should().BeTrue();
        result.OrderId.Should().BeNull();
        result.PaymentIntentId.Should().Be("pi_123");
    }

    [Fact]
    public async Task Failed_With_OrderId_Maps_Not_Succeeded()
    {
        var h = Build(FailedEvent("evt_f1", "order-2"));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.EventType.Should().Be("payment_intent.payment_failed");
        result.PaymentIntentId.Should().Be("pi_456");
        result.OrderId.Should().Be("order-2");
        result.Succeeded.Should().BeFalse();
        result.Duplicate.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_Without_OrderId_Maps_Null_Order()
    {
        var h = Build(FailedEvent("evt_f2", null));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Succeeded.Should().BeFalse();
        result.OrderId.Should().BeNull();
        result.PaymentIntentId.Should().Be("pi_456");
    }

    [Fact]
    public async Task Refunded_With_OrderId_Maps_Charge_Fields()
    {
        var h = Build(RefundedEvent("evt_r1", "order-3"));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.EventType.Should().Be("charge.refunded");
        result.PaymentIntentId.Should().Be("pi_789");
        result.OrderId.Should().Be("order-3");
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Refunded_Without_OrderId_Maps_Null_Order()
    {
        var h = Build(RefundedEvent("evt_r2", null));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.PaymentIntentId.Should().Be("pi_789");
        result.OrderId.Should().BeNull();
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_Type_Falls_To_Default_Arm()
    {
        var h = Build(UnknownEvent("evt_u1"));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.EventType.Should().Be("customer.created");
        result.PaymentIntentId.Should().BeNull();
        result.OrderId.Should().BeNull();
        result.Succeeded.Should().BeFalse();
        result.Duplicate.Should().BeFalse();
    }

    [Fact]
    public async Task Succeeded_Type_With_Non_PaymentIntent_Object_Falls_To_Default()
    {
        var h = Build(SucceededEvent("evt_mm1", "o1",
            rawObject: new Charge { Id = "ch_x", PaymentIntentId = "pi_x" }));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Succeeded.Should().BeFalse("object-type guard must fail");
        result.PaymentIntentId.Should().BeNull();
        result.OrderId.Should().BeNull();
    }

    [Fact]
    public async Task Refunded_Type_With_Non_Charge_Object_Falls_To_Default()
    {
        var h = Build(RefundedEvent("evt_mm2", "o1",
            rawObject: new PaymentIntent { Id = "pi_x" }));

        var result = await h.Service.HandleWebhookEventAsync("{}", "sig");

        result.Succeeded.Should().BeFalse("object-type guard must fail");
        result.PaymentIntentId.Should().BeNull();
        result.OrderId.Should().BeNull();
    }

    // ---------- private pures via reflection ----------

    private static MethodInfo Private(string name) =>
        typeof(StripePaymentService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Private method {name} not found.");

    [Theory]
    [InlineData("10.00", 1000)]
    [InlineData("0", 0)]
    [InlineData("10.005", 1001)]
    [InlineData("10.004", 1000)]
    public void ToMinorUnits_Rounds_Away_From_Zero(string amount, long expected)
    {
        var mi = Private("ToMinorUnits");

        var actual = (long)mi.Invoke(null, new object[] { decimal.Parse(amount) })!;

        actual.Should().Be(expected);
    }

    [Fact]
    public void ToRequestOptions_Null_Or_Whitespace_Returns_Null()
    {
        var mi = Private("ToRequestOptions");

        mi.Invoke(null, new object?[] { null }).Should().BeNull();
        mi.Invoke(null, new object[] { "   " }).Should().BeNull();
    }

    [Fact]
    public void ToRequestOptions_Trims_Key()
    {
        var mi = Private("ToRequestOptions");

        var result = (RequestOptions?)mi.Invoke(null, new object[] { "  key-1  " });

        result.Should().NotBeNull();
        result!.IdempotencyKey.Should().Be("key-1");
    }

    [Fact]
    public void ToResult_Maps_Cents_To_Decimal()
    {
        var mi = Private("ToResult");
        var intent = new PaymentIntent
        {
            Id = "pi_1",
            ClientSecret = "secret_1",
            Amount = 2550,
            Currency = "usd",
            Status = "succeeded"
        };

        var result = (PaymentIntentResultDto)mi.Invoke(null, new object[] { intent })!;

        result.PaymentIntentId.Should().Be("pi_1");
        result.ClientSecret.Should().Be("secret_1");
        result.Amount.Should().Be(25.50m);
        result.Currency.Should().Be("usd");
        result.Status.Should().Be("succeeded");
    }

    [Fact]
    public void ToResult_Null_ClientSecret_Maps_To_Empty()
    {
        var mi = Private("ToResult");
        var intent = new PaymentIntent
        {
            Id = "pi_2",
            ClientSecret = null,
            Amount = 100,
            Currency = "usd",
            Status = "requires_payment_method"
        };

        var result = (PaymentIntentResultDto)mi.Invoke(null, new object[] { intent })!;

        result.ClientSecret.Should().BeEmpty();
        result.Amount.Should().Be(1m);
    }

    // ---------- IsTransient matrix ----------

    [Theory]
    [InlineData(0, true)] // default: no HTTP response (network failure)
    [InlineData(408, true)] // RequestTimeout
    [InlineData(429, true)] // rate limited
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(422, false)]
    public void IsTransient_Matrix(int statusCode, bool expected)
    {
        var ex = new StripeException("t") { HttpStatusCode = (HttpStatusCode)statusCode };

        StripePaymentService.IsTransientStripeError(ex).Should().Be(expected);
    }

    [Fact]
    public void IsTransient_Default_Exception_Is_Transient()
    {
        StripePaymentService.IsTransientStripeError(new StripeException("network-down"))
            .Should().BeTrue("fresh StripeException carries default(0) status: no HTTP response");
    }
}

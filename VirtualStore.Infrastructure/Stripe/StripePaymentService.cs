using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Polly;
using Polly.Retry;
using Stripe;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;

namespace VirtualStore.Infrastructure.Stripe;

public class StripePaymentService : IStripePaymentService
{
    private readonly StripeSettings _settings;
    private readonly IRepository<ProcessedWebhookEvent>? _webhookEvents;
    private readonly ILogger<StripePaymentService> _logger;
    private readonly IStripeClientFactory _stripeFactory;

    // Static pipeline (Stripe SDK is used directly, no IHttpClient): retry 2x on transient
    // Stripe errors (network failure, 408/429/5xx) + 10s per-attempt timeout. Signature
    // verification (HandleWebhookEventAsync) is CPU-bound and is intentionally not retried.
    private static readonly ResiliencePipeline _stripePipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(500),
            UseJitter = true,
            ShouldHandle = new PredicateBuilder().Handle<StripeException>(IsTransientStripeError)
        })
        .AddTimeout(TimeSpan.FromSeconds(10))
        .Build();

    // Stripe.net exposes HttpStatusCode as non-nullable (default 0 when no HTTP
    // response was received, e.g. network failure) — treat that as transient too.
    // Wave T0: internal so unit tests can cover the transient matrix directly.
    internal static bool IsTransientStripeError(StripeException ex) =>
        ex.HttpStatusCode == default
        || ex.HttpStatusCode == System.Net.HttpStatusCode.RequestTimeout
        || ex.HttpStatusCode == (System.Net.HttpStatusCode)429
        || (int)ex.HttpStatusCode >= 500;

    /// <param name="webhookEvents">
    /// Optional so existing constructions keep compiling: without it webhook dedup
    /// is skipped (unit tests). DI always supplies the scoped repository, so
    /// production webhooks are deduplicated. See ADR-0007.
    /// </param>
    public StripePaymentService(
        IOptions<StripeSettings> options,
        IRepository<ProcessedWebhookEvent>? webhookEvents = null,
        ILogger<StripePaymentService>? logger = null,
        IStripeClientFactory? stripeFactory = null)
    {
        _settings = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _webhookEvents = webhookEvents;
        _logger = logger ?? NullLogger<StripePaymentService>.Instance;
        _stripeFactory = stripeFactory ?? new StripeClientFactory();
    }

    private static long ToMinorUnits(decimal amount)
        => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static RequestOptions? ToRequestOptions(string? idempotencyKey) =>
        string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : new RequestOptions { IdempotencyKey = idempotencyKey.Trim() };

    private static PaymentIntentResultDto ToResult(PaymentIntent intent) => new()
    {
        PaymentIntentId = intent.Id,
        ClientSecret = intent.ClientSecret ?? string.Empty,
        Amount = intent.Amount / 100m,
        Currency = intent.Currency,
        Status = intent.Status
    };

    public async Task<PaymentIntentResultDto> CreatePaymentIntentAsync(decimal amount, string currency, string? customerId = null, string? orderId = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        var options = new PaymentIntentCreateOptions
        {
            Amount = ToMinorUnits(amount), // Stripe works in minor units (cents)
            Currency = currency,
            Customer = customerId,
            PaymentMethodTypes = new List<string> { "card" },
            Metadata = string.IsNullOrWhiteSpace(orderId)
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["orderId"] = orderId }
        };
        var service = _stripeFactory.CreatePaymentIntentService(_settings.SecretKey);
        var intent = await _stripePipeline.ExecuteAsync(
            async ct => await service.CreateAsync(options, ToRequestOptions(idempotencyKey), cancellationToken: ct),
            cancellationToken);
        return ToResult(intent);
    }

    public async Task<bool> ConfirmPaymentAsync(string paymentIntentId, CancellationToken cancellationToken = default)
    {
        var service = _stripeFactory.CreatePaymentIntentService(_settings.SecretKey);
        var paymentIntent = await _stripePipeline.ExecuteAsync(
            async ct => await service.GetAsync(paymentIntentId, cancellationToken: ct),
            cancellationToken);
        return paymentIntent.Status == "succeeded";
    }

    public async Task<PaymentIntentResultDto> RefundPaymentAsync(string paymentIntentId, decimal? amount = null, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        var options = new RefundCreateOptions
        {
            PaymentIntent = paymentIntentId
        };
        if (amount.HasValue)
            options.Amount = ToMinorUnits(amount.Value);

        var service = _stripeFactory.CreateRefundService(_settings.SecretKey);
        var refund = await _stripePipeline.ExecuteAsync(
            async ct => await service.CreateAsync(options, ToRequestOptions(idempotencyKey), cancellationToken: ct),
            cancellationToken);
        return new PaymentIntentResultDto
        {
            PaymentIntentId = refund.PaymentIntentId ?? paymentIntentId,
            ClientSecret = string.Empty,
            Amount = refund.Amount / 100m,
            Currency = refund.Currency,
            Status = refund.Status,
            RefundId = refund.Id
        };
    }

    public async Task<StripeWebhookResultDto> HandleWebhookEventAsync(string json, string signatureHeader, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Throws StripeException when the signature is invalid; the controller maps it to 400.
        var stripeEvent = _stripeFactory.ConstructEvent(json, signatureHeader, _settings.WebhookSecret);

        // Check-then-insert dedup (ADR-0007): redeliveries share the Stripe event id.
        // Duplicate → ignored result (controller acks 200, applies no state change).
        if (_webhookEvents is not null && !string.IsNullOrWhiteSpace(stripeEvent.Id))
        {
            var seen = await _webhookEvents.FindOneAsync(e => e.EventId == stripeEvent.Id, cancellationToken).ConfigureAwait(false);
            if (seen is not null)
                return new StripeWebhookResultDto
                {
                    EventType = stripeEvent.Type,
                    PaymentIntentId = null,
                    OrderId = null,
                    Succeeded = false,
                    Duplicate = true
                };

            try
            {
                await _webhookEvents.AddAsync(new ProcessedWebhookEvent
                {
                    EventId = stripeEvent.Id,
                    Type = stripeEvent.Type,
                    ReceivedAt = DateTime.UtcNow
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (MongoException ex)
            {
                // Lost the check-then-insert race: a concurrent delivery won.
                _logger.LogDebug(ex, "Stripe webhook {EventId} already processed by a concurrent delivery.", stripeEvent.Id);
                return new StripeWebhookResultDto
                {
                    EventType = stripeEvent.Type,
                    PaymentIntentId = null,
                    OrderId = null,
                    Succeeded = false,
                    Duplicate = true
                };
            }
        }

        StripeWebhookResultDto result = stripeEvent.Type switch
        {
            "payment_intent.succeeded" when stripeEvent.Data.Object is PaymentIntent succeeded => new StripeWebhookResultDto
            {
                EventType = stripeEvent.Type,
                PaymentIntentId = succeeded.Id,
                OrderId = succeeded.Metadata.TryGetValue("orderId", out var succeededOrderId) ? succeededOrderId : null,
                Succeeded = true
            },
            "payment_intent.payment_failed" when stripeEvent.Data.Object is PaymentIntent failed => new StripeWebhookResultDto
            {
                EventType = stripeEvent.Type,
                PaymentIntentId = failed.Id,
                OrderId = failed.Metadata.TryGetValue("orderId", out var failedOrderId) ? failedOrderId : null,
                Succeeded = false
            },
            "charge.refunded" when stripeEvent.Data.Object is Charge refunded => new StripeWebhookResultDto
            {
                EventType = stripeEvent.Type,
                PaymentIntentId = refunded.PaymentIntentId,
                OrderId = refunded.Metadata.TryGetValue("orderId", out var refundedOrderId) ? refundedOrderId : null,
                Succeeded = false
            },
            _ => new StripeWebhookResultDto
            {
                EventType = stripeEvent.Type,
                PaymentIntentId = null,
                OrderId = null,
                Succeeded = false
            }
        };

        return result;
    }
}

namespace VirtualStore.Domain.Entities;

/// <summary>
/// Deduplication record for Stripe webhook deliveries (Epic B / ADR-0007).
/// Stripe retries deliveries until it receives 2xx; redelivered events share the
/// same <see cref="EventId"/> and must be acked without re-applying the transition.
/// Collection name follows <c>typeof(T).Name</c>. Records expire via a 30-day TTL
/// index on <see cref="ReceivedAt"/> (see <c>MongoDbContext.EnsureIndexesAsync</c>).
/// </summary>
public class ProcessedWebhookEvent : BaseEntity
{
    /// <summary>Stripe event id (<c>evt_…</c>). Unique (<c>ux_webhookevent_eventId</c>).</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>Stripe event type (e.g. <c>payment_intent.succeeded</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>When the event was first received (UTC). TTL source.</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

using MongoDB.Bson.Serialization.Attributes;
using VirtualStore.Domain.Enums;

namespace VirtualStore.Domain.Entities;

public class Order : BaseEntity
{
    public string UserId { get; set; } = string.Empty;
    public List<OrderItem> Items { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "usd";
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public string? StripePaymentIntentId { get; set; }
    /// <summary>
    /// Last Stripe refund id applied to this order (additive, Epic B / ADR-0007).
    /// Null until a refund is processed via <c>POST /api/payments/orders/{id}/refund</c>.
    /// </summary>
    public string? StripeRefundId { get; set; }
    /// <summary>
    /// Amount of the last refund (minor-unit-exact decimal). Full refunds equal
    /// <see cref="TotalAmount"/>; partial refunds are smaller. Null until refunded.
    /// </summary>
    public decimal? StripeRefundAmount { get; set; }
    public Address ShippingAddress { get; set; } = new();
    /// <summary>
    /// Client-supplied idempotency key (header <c>Idempotency-Key</c> wins over body).
    /// Null keys are omitted from the document (<c>BsonIgnoreIfNull</c>) so the
    /// sparse unique index <c>ux_order_userIdempotency</c> never collides on
    /// key-less orders. See ADR-0006.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? IdempotencyKey { get; set; }
}

public class Address
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}
using VirtualStore.Domain.Enums;

namespace VirtualStore.Application.DTOs;

public class OrderDto
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public List<OrderItemDto> Items { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "usd";
    public OrderStatus Status { get; set; }
    public string? StripePaymentIntentId { get; set; }
    /// <summary>Last Stripe refund id (ADR-0007). Null until refunded.</summary>
    public string? StripeRefundId { get; set; }
    /// <summary>Amount of the last refund. Null until refunded.</summary>
    public decimal? StripeRefundAmount { get; set; }
    public AddressDto ShippingAddress { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class OrderItemDto
{
    public string ProductId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
}

public class AddressDto
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

public class CreateOrderDto
{
    public List<OrderItemDto> Items { get; set; } = new();
    public AddressDto ShippingAddress { get; set; } = new();
    public string? StripePaymentMethodId { get; set; } // Optional for Stripe
    /// <summary>
    /// Optional idempotency key. The controller prefers the <c>Idempotency-Key</c>
    /// request header over this body value. Same (user, key) replays return the
    /// existing order; same key with a different payload is a 409. See ADR-0006.
    /// </summary>
    public string? IdempotencyKey { get; set; }
}

public class UpdateOrderStatusDto
{
    public OrderStatus Status { get; set; }
}
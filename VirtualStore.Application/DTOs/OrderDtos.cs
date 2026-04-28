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
}
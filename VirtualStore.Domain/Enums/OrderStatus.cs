namespace VirtualStore.Domain.Enums;

public enum OrderStatus
{
    Pending,
    PaymentReceived,
    Processing,
    Shipped,
    Delivered,
    Cancelled
}
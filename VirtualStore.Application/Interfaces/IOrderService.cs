using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Interfaces;

public interface IOrderService
{
    Task<OrderDto> CreateOrderAsync(string userId, CreateOrderDto dto, CancellationToken cancellationToken = default);
    Task<OrderDto?> GetOrderByIdAsync(string orderId, CancellationToken cancellationToken = default);
    Task<PagedResult<OrderDto>> GetUserOrdersAsync(string userId, int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default);
    Task<OrderDto> UpdateOrderStatusAsync(string orderId, Domain.Enums.OrderStatus status, CancellationToken cancellationToken = default);
    /// <summary>
    /// Persists the Stripe payment-intent id on an order (ADR-0007 retry path:
    /// <c>POST /api/payments/intent</c> with <c>orderId</c>).
    /// </summary>
    Task<OrderDto> AttachPaymentIntentAsync(string orderId, string paymentIntentId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Applies a processed Stripe refund: persists the refund id/amount, moves
    /// <c>PaymentReceived|Processing → Refunded</c> (full),
    /// <c>PaymentReceived|Processing|Shipped → PartiallyRefunded</c> (partial),
    /// <c>PartiallyRefunded|Delivered → Refunded</c> (full after partial / late return).
    /// Full refunds restore stock; partials do not (partial = discount/adjustment,
    /// not a return — see ADR-0007).
    /// </summary>
    Task<OrderDto> ApplyRefundAsync(string orderId, string? refundId, decimal? refundAmount, bool fullRefund, CancellationToken cancellationToken = default);
}

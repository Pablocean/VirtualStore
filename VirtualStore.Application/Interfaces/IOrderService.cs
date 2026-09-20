using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Interfaces;

public interface IOrderService
{
    Task<OrderDto> CreateOrderAsync(string userId, CreateOrderDto dto, CancellationToken cancellationToken = default);
    Task<OrderDto?> GetOrderByIdAsync(string orderId, CancellationToken cancellationToken = default);
    Task<PagedResult<OrderDto>> GetUserOrdersAsync(string userId, int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default);
    Task<OrderDto> UpdateOrderStatusAsync(string orderId, Domain.Enums.OrderStatus status, CancellationToken cancellationToken = default);
}

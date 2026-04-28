using AutoMapper;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepo;
    private readonly IRepository<Cart> _cartRepo;
    private readonly IRepository<Product> _productRepo;
    private readonly IMapper _mapper;

    public OrderService(IRepository<Order> orderRepo, IRepository<Cart> cartRepo, IRepository<Product> productRepo, IMapper mapper)
    {
        _orderRepo = orderRepo;
        _cartRepo = cartRepo;
        _productRepo = productRepo;
        _mapper = mapper;
    }

    public async Task<OrderDto> CreateOrderAsync(string userId, CreateOrderDto dto)
    {
        // Optionally create from cart
        var order = _mapper.Map<Order>(dto);
        order.UserId = userId;
        order.Status = Domain.Enums.OrderStatus.Pending;
        order.TotalAmount = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        // In real implementation, you'd fetch product details and validate stock
        await _orderRepo.AddAsync(order);
        // Clear cart after order
        await _cartRepo.DeleteAsync((await _cartRepo.FindOneAsync(c => c.UserId == userId))?.Id!);
        return _mapper.Map<OrderDto>(order);
    }

    public async Task<OrderDto?> GetOrderByIdAsync(string orderId)
    {
        var order = await _orderRepo.GetByIdAsync(orderId);
        return order == null ? null : _mapper.Map<OrderDto>(order);
    }

    public async Task<PagedResult<OrderDto>> GetUserOrdersAsync(string userId, int pageNumber = 1, int pageSize = 20)
    {
        var orders = await _orderRepo.FindAsync(o => o.UserId == userId);
        var query = orders.AsQueryable().OrderByDescending(o => o.CreatedAt);
        var total = query.Count();
        var items = query.Skip((pageNumber - 1) * pageSize).Take(pageSize).Select(_mapper.Map<OrderDto>).ToList();
        return new PagedResult<OrderDto> { Items = items, TotalCount = total, PageNumber = pageNumber, PageSize = pageSize };
    }

    public async Task<OrderDto> UpdateOrderStatusAsync(string orderId, Domain.Enums.OrderStatus status)
    {
        var order = await _orderRepo.GetByIdAsync(orderId) ?? throw new KeyNotFoundException("Order not found");
        order.Status = status;
        await _orderRepo.UpdateAsync(orderId, order);
        return _mapper.Map<OrderDto>(order);
    }
}
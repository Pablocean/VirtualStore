using AutoMapper;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.Services;

public class OrderService : IOrderService
{
    private static readonly IReadOnlyDictionary<OrderStatus, IReadOnlySet<OrderStatus>> AllowedTransitions =
        new Dictionary<OrderStatus, IReadOnlySet<OrderStatus>>
        {
            [OrderStatus.Pending] = new HashSet<OrderStatus> { OrderStatus.PaymentReceived, OrderStatus.Cancelled },
            [OrderStatus.PaymentReceived] = new HashSet<OrderStatus> { OrderStatus.Processing, OrderStatus.Cancelled },
            [OrderStatus.Processing] = new HashSet<OrderStatus> { OrderStatus.Shipped, OrderStatus.Cancelled },
            [OrderStatus.Shipped] = new HashSet<OrderStatus> { OrderStatus.Delivered },
            [OrderStatus.Delivered] = new HashSet<OrderStatus>(),
            [OrderStatus.Cancelled] = new HashSet<OrderStatus>()
        };

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

    public async Task<OrderDto> CreateOrderAsync(string userId, CreateOrderDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(userId))
            throw new KeyNotFoundException("User not found.");
        if (dto is null)
            throw new ArgumentNullException(nameof(dto));
        if (dto.Items is null || dto.Items.Count == 0)
            throw new InvalidOperationException("Order must contain at least one item.");

        // Re-price every item from the database; never trust client prices.
        var orderItems = new List<OrderItem>(dto.Items.Count);
        decimal total = 0m;
        foreach (var item in dto.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.Quantity <= 0)
                throw new InvalidOperationException($"Invalid quantity ({item.Quantity}) for product '{item.ProductId}'.");

            var product = await _productRepo.GetByIdAsync(item.ProductId)
                ?? throw new KeyNotFoundException($"Product '{item.ProductId}' not found.");
            if (!product.IsActive)
                throw new InvalidOperationException($"Product '{product.Name}' is not available.");
            if (product.StockQuantity < item.Quantity)
                throw new InvalidOperationException($"Insufficient stock for product '{product.Name}'. Requested: {item.Quantity}, available: {product.StockQuantity}.");

            orderItems.Add(new OrderItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                UnitPrice = product.Price,
                Quantity = item.Quantity
            });
            total += product.Price * item.Quantity;

            product.StockQuantity -= item.Quantity;
            await _productRepo.UpdateAsync(product.Id, product);
        }

        var order = _mapper.Map<Order>(dto);
        order.UserId = userId;
        order.Items = orderItems;
        order.TotalAmount = total;
        order.Status = OrderStatus.Pending;

        // Insert the order BEFORE clearing the cart so a cart failure never loses the order.
        await _orderRepo.AddAsync(order);

        var cart = await _cartRepo.FindOneAsync(c => c.UserId == userId);
        if (cart is not null)
            await _cartRepo.DeleteAsync(cart.Id);

        return _mapper.Map<OrderDto>(order);
    }

    public async Task<OrderDto?> GetOrderByIdAsync(string orderId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = await _orderRepo.GetByIdAsync(orderId);
        return order == null ? null : _mapper.Map<OrderDto>(order);
    }

    public async Task<PagedResult<OrderDto>> GetUserOrdersAsync(string userId, int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(userId))
            throw new KeyNotFoundException("User not found.");

        var orders = await _orderRepo.FindAsync(o => o.UserId == userId);
        var query = orders.AsQueryable().OrderByDescending(o => o.CreatedAt);
        var total = query.Count();
        var items = query.Skip((pageNumber - 1) * pageSize).Take(pageSize).Select(_mapper.Map<OrderDto>).ToList();
        return new PagedResult<OrderDto> { Items = items, TotalCount = total, PageNumber = pageNumber, PageSize = pageSize };
    }

    public async Task<OrderDto> UpdateOrderStatusAsync(string orderId, OrderStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var order = await _orderRepo.GetByIdAsync(orderId) ?? throw new KeyNotFoundException("Order not found");

        if (!AllowedTransitions.TryGetValue(order.Status, out var allowed) || !allowed.Contains(status))
            throw new InvalidOperationException($"Cannot transition order from {order.Status} to {status}.");

        order.Status = status;
        await _orderRepo.UpdateAsync(orderId, order);
        return _mapper.Map<OrderDto>(order);
    }
}

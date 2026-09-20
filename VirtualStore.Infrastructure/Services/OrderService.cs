using AutoMapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Data;

namespace VirtualStore.Infrastructure.Services;

public class OrderService : IOrderService
{
    private static readonly IReadOnlyDictionary<OrderStatus, IReadOnlySet<OrderStatus>> AllowedTransitions =
        new Dictionary<OrderStatus, IReadOnlySet<OrderStatus>>
        {
            [OrderStatus.Pending] = new HashSet<OrderStatus> { OrderStatus.PaymentReceived, OrderStatus.Cancelled },
            [OrderStatus.PaymentReceived] = new HashSet<OrderStatus> { OrderStatus.Processing, OrderStatus.Cancelled, OrderStatus.Refunded, OrderStatus.PartiallyRefunded },
            [OrderStatus.Processing] = new HashSet<OrderStatus> { OrderStatus.Shipped, OrderStatus.Cancelled, OrderStatus.Refunded, OrderStatus.PartiallyRefunded },
            [OrderStatus.Shipped] = new HashSet<OrderStatus> { OrderStatus.Delivered, OrderStatus.PartiallyRefunded },
            [OrderStatus.Delivered] = new HashSet<OrderStatus> { OrderStatus.Refunded },
            [OrderStatus.PartiallyRefunded] = new HashSet<OrderStatus> { OrderStatus.Refunded },
            [OrderStatus.Refunded] = new HashSet<OrderStatus>(),
            [OrderStatus.Cancelled] = new HashSet<OrderStatus>()
        };

    private readonly IRepository<Order> _orderRepo;
    private readonly IRepository<Cart> _cartRepo;
    private readonly IRepository<Product> _productRepo;
    private readonly IMapper _mapper;
    private readonly MongoDbContext? _context;
    private readonly IStripePaymentService? _paymentService;
    private readonly ILogger<OrderService> _logger;

    /// <param name="context">
    /// Optional so existing unit-test constructions (<c>new OrderService(repos, mapper)</c>)
    /// keep compiling: without it checkout runs non-transactionally (same statements,
    /// no session). DI always supplies the singleton context, so production checkout
    /// is transactional. See ADR-0006.
    /// </param>
    /// <param name="paymentService">
    /// Optional Stripe linkage (ADR-0007). When present, a payment intent is created
    /// AFTER the checkout transaction commits (never inside it) with the
    /// deterministic key <c>order:{orderId}:intent</c> and persisted on the order.
    /// Null in unit tests that do not cover payments — checkout then skips linkage.
    /// </param>
    public OrderService(
        IRepository<Order> orderRepo,
        IRepository<Cart> cartRepo,
        IRepository<Product> productRepo,
        IMapper mapper,
        MongoDbContext? context = null,
        IStripePaymentService? paymentService = null,
        ILogger<OrderService>? logger = null)
    {
        _orderRepo = orderRepo;
        _cartRepo = cartRepo;
        _productRepo = productRepo;
        _mapper = mapper;
        _context = context;
        _paymentService = paymentService;
        _logger = logger ?? NullLogger<OrderService>.Instance;
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

        var idempotencyKey = NormalizeKey(dto.IdempotencyKey);

        OrderDto created;
        try
        {
            if (_context is null)
            {
                created = await CreateOrderCoreAsync(userId, dto, idempotencyKey, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                OrderDto? result = null;
                await _context.TransactAsync(async () =>
                {
                    result = await CreateOrderCoreAsync(userId, dto, idempotencyKey, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                created = result!;
            }
        }
        catch (MongoWriteException ex) when (idempotencyKey is not null && IsDuplicateKey(ex))
        {
            // Lost the check-then-act race against a concurrent replay: the winner
            // committed, our transaction aborted (enlisting nothing else), so fetch
            // the winner outside the transaction and return it.
            var existing = await _orderRepo.FindOneAsync(o => o.UserId == userId && o.IdempotencyKey == idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                throw;
            EnsureSamePayload(existing, dto);
            return _mapper.Map<OrderDto>(existing);
        }
        // Transaction aborts for any other reason propagate as MongoException
        // (mapped to 500 by ApiExceptionHandler; see ADR-0006 follow-ups).

        // Intent↔order linkage (ADR-0007): Stripe is called OUTSIDE the mongo
        // transaction (network I/O must never enlist). On failure the order stays
        // Pending with a null intent id; the client retries via
        // POST /api/payments/intent { orderId }.
        if (_paymentService is not null)
        {
            try
            {
                var intent = await _paymentService.CreatePaymentIntentAsync(
                    created.TotalAmount,
                    created.Currency,
                    customerId: null,
                    orderId: created.Id,
                    idempotencyKey: StripeIdempotency.IntentKey(created.Id),
                    cancellationToken).ConfigureAwait(false);

                var entity = await _orderRepo.GetByIdAsync(created.Id, cancellationToken).ConfigureAwait(false);
                if (entity is not null)
                {
                    entity.StripePaymentIntentId = intent.PaymentIntentId;
                    await _orderRepo.UpdateAsync(entity.Id, entity, cancellationToken).ConfigureAwait(false);
                    created.StripePaymentIntentId = intent.PaymentIntentId;
                }
            }
            catch (Exception ex)
            {
                // Swallow: checkout already committed; linkage is retried explicitly.
                _logger.LogWarning(ex, "Stripe intent creation failed for order {OrderId}; order stays Pending without intent id.", created.Id);
            }
        }

        return created;
    }

    /// <summary>
    /// The validate → decrement → insert → cart-clear flow. Runs inside
    /// <c>TransactAsync</c> in production; directly in unit tests (no context).
    /// </summary>
    private async Task<OrderDto> CreateOrderCoreAsync(string userId, CreateOrderDto dto, string? idempotencyKey, CancellationToken ct)
    {
        // Idempotent replay: same (user, key) returns the existing order.
        if (idempotencyKey is not null)
        {
            var replay = await _orderRepo.FindOneAsync(o => o.UserId == userId && o.IdempotencyKey == idempotencyKey, ct).ConfigureAwait(false);
            if (replay is not null)
            {
                EnsureSamePayload(replay, dto);
                return _mapper.Map<OrderDto>(replay);
            }
        }

        // Re-price every item from the database; never trust client prices.
        var orderItems = new List<OrderItem>(dto.Items.Count);
        decimal total = 0m;
        foreach (var item in dto.Items)
        {
            ct.ThrowIfCancellationRequested();

            if (item.Quantity <= 0)
                throw new InvalidOperationException($"Invalid quantity ({item.Quantity}) for product '{item.ProductId}'.");

            var product = await _productRepo.GetByIdAsync(item.ProductId, ct).ConfigureAwait(false)
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
            await _productRepo.UpdateAsync(product.Id, product, ct).ConfigureAwait(false);
        }

        var order = _mapper.Map<Order>(dto);
        order.UserId = userId;
        order.IdempotencyKey = idempotencyKey;
        order.Items = orderItems;
        order.TotalAmount = total;
        order.Status = OrderStatus.Pending;

        // Insert the order BEFORE clearing the cart so a cart failure never loses the order.
        // Duplicate (user, key) here means a concurrent replay won the race; the
        // unique index rejects us and the caller falls back to fetching the winner.
        await _orderRepo.AddAsync(order, ct).ConfigureAwait(false);

        var cart = await _cartRepo.FindOneAsync(c => c.UserId == userId, ct).ConfigureAwait(false);
        if (cart is not null)
            await _cartRepo.DeleteAsync(cart.Id, ct).ConfigureAwait(false);

        return _mapper.Map<OrderDto>(order);
    }

    public async Task<OrderDto?> GetOrderByIdAsync(string orderId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var order = await _orderRepo.GetByIdAsync(orderId, cancellationToken).ConfigureAwait(false);
        return order == null ? null : _mapper.Map<OrderDto>(order);
    }

    public async Task<PagedResult<OrderDto>> GetUserOrdersAsync(string userId, int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(userId))
            throw new KeyNotFoundException("User not found.");

        var page = pageNumber < 1 ? 1 : pageNumber;
        var size = pageSize <= 0 ? 20 : Math.Min(pageSize, 100);

        // Server-side filter + paging (CreatedAt desc default); honors the 100-cap via PagedAsync.
        var (items, total) = await _orderRepo.PagedAsync(o => o.UserId == userId, page, size, sortBy: null, desc: true, cancellationToken).ConfigureAwait(false);
        return new PagedResult<OrderDto>
        {
            Items = items.Select(_mapper.Map<OrderDto>).ToList(),
            TotalCount = checked((int)total),
            PageNumber = page,
            PageSize = size
        };
    }

    public async Task<OrderDto> UpdateOrderStatusAsync(string orderId, OrderStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var order = await _orderRepo.GetByIdAsync(orderId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Order not found");

        if (!AllowedTransitions.TryGetValue(order.Status, out var allowed) || !allowed.Contains(status))
            throw new InvalidOperationException($"Cannot transition order from {order.Status} to {status}.");

        order.Status = status;
        await _orderRepo.UpdateAsync(orderId, order, cancellationToken).ConfigureAwait(false);
        return _mapper.Map<OrderDto>(order);
    }

    public async Task<OrderDto> AttachPaymentIntentAsync(string orderId, string paymentIntentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(paymentIntentId))
            throw new InvalidOperationException("Payment intent id must not be empty.");

        var order = await _orderRepo.GetByIdAsync(orderId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Order not found");

        order.StripePaymentIntentId = paymentIntentId.Trim();
        await _orderRepo.UpdateAsync(orderId, order, cancellationToken).ConfigureAwait(false);
        return _mapper.Map<OrderDto>(order);
    }

    public async Task<OrderDto> ApplyRefundAsync(string orderId, string? refundId, decimal? refundAmount, bool fullRefund, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var order = await _orderRepo.GetByIdAsync(orderId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Order not found");

        var target = fullRefund ? OrderStatus.Refunded : OrderStatus.PartiallyRefunded;
        if (!AllowedTransitions.TryGetValue(order.Status, out var allowed) || !allowed.Contains(target))
            throw new InvalidOperationException($"Cannot transition order from {order.Status} to {target}.");

        // Full refunds restore stock (goods come back); partial refunds do NOT —
        // a partial is a discount/adjustment on kept goods, not a return (ADR-0007).
        if (fullRefund)
        {
            foreach (var item in order.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var product = await _productRepo.GetByIdAsync(item.ProductId, cancellationToken).ConfigureAwait(false);
                if (product is null)
                {
                    _logger.LogWarning("Refund stock restore skipped: product {ProductId} of order {OrderId} no longer exists.", item.ProductId, orderId);
                    continue;
                }
                product.StockQuantity += item.Quantity;
                await _productRepo.UpdateAsync(product.Id, product, cancellationToken).ConfigureAwait(false);
            }
        }

        order.Status = target;
        order.StripeRefundId = refundId;
        order.StripeRefundAmount = refundAmount ?? (fullRefund ? order.TotalAmount : null);
        await _orderRepo.UpdateAsync(orderId, order, cancellationToken).ConfigureAwait(false);
        return _mapper.Map<OrderDto>(order);
    }

    private static string? NormalizeKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    private static bool IsDuplicateKey(MongoWriteException ex)
        => ex.WriteError?.Category == ServerErrorCategory.DuplicateKey
            || ex.WriteError?.Code == 11000;

    /// <summary>
    /// Same key, different payload is a client bug (e.g. key reuse across orders),
    /// not a replay: fail closed with 409. Comparison is order-sensitive and ignores
    /// client prices/names (server re-prices from MongoDB).
    /// </summary>
    private static void EnsureSamePayload(Order existing, CreateOrderDto dto)
    {
        var sameItems = existing.Items.Count == dto.Items.Count
            && existing.Items.Zip(dto.Items).All(pair =>
                pair.First.ProductId == pair.Second.ProductId &&
                pair.First.Quantity == pair.Second.Quantity);

        var a = existing.ShippingAddress;
        var b = dto.ShippingAddress;
        var sameAddress = a is not null && b is not null
            && a.Street == b.Street
            && a.City == b.City
            && a.State == b.State
            && a.ZipCode == b.ZipCode
            && a.Country == b.Country;

        if (!sameItems || !sameAddress)
            throw new InvalidOperationException("Idempotency key was already used for a different order payload.");
    }
}

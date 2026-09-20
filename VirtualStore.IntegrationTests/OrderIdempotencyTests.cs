using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Data;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// Idempotent transactional checkout against a single-node replica-set container
/// (ADR-0006). Resolves the REAL services (OrderService + MongoDbContext +
/// MongoRepository&lt;T&gt;) so the <c>TransactAsync</c> path runs with a live session.
/// Skipped when Docker is unavailable (see <see cref="RequiresDockerFactAttribute"/>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrderIdempotencyTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OrderIdempotencyTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [RequiresDockerFact]
    public async Task CreateOrder_Twice_With_Same_Key_Creates_One_Order_And_Decrements_Once()
    {
        using var scope = _factory.Services.CreateScope();
        var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var products = scope.ServiceProvider.GetRequiredService<IRepository<Product>>();
        var orderRepo = scope.ServiceProvider.GetRequiredService<IRepository<Order>>();
        var context = scope.ServiceProvider.GetRequiredService<MongoDbContext>();

        // The app boot already ran this; re-run so the test is self-sufficient and
        // the ux_order_userIdempotency index is guaranteed present.
        await context.EnsureIndexesAsync();

        var userId = $"idem-user-{Guid.NewGuid():N}";
        var key = $"key-{Guid.NewGuid():N}";

        var product = new Product
        {
            Name = "Idempotency Widget",
            Price = 12.50m,
            StockQuantity = 5,
            CategoryId = "cat1",
            IsActive = true
        };
        await products.AddAsync(product);

        var dto = new CreateOrderDto
        {
            Items = new List<OrderItemDto> { new() { ProductId = product.Id, Quantity = 2, UnitPrice = 0m } },
            ShippingAddress = new AddressDto
            {
                Street = "123 Main St",
                City = "Springfield",
                State = "IL",
                ZipCode = "62701",
                Country = "USA"
            },
            IdempotencyKey = key
        };

        var first = await orders.CreateOrderAsync(userId, dto);
        var replay = await orders.CreateOrderAsync(userId, dto);

        replay.Id.Should().Be(first.Id, "same (user, key) replay must return the existing order");
        first.TotalAmount.Should().Be(25.00m);

        var (items, total) = await orderRepo.PagedAsync(o => o.UserId == userId, 1, 100, null, true);
        total.Should().Be(1);
        items.Should().ContainSingle(o => o.Id == first.Id && o.IdempotencyKey == key);

        var after = await products.GetByIdAsync(product.Id);
        after!.StockQuantity.Should().Be(3, "stock must be decremented exactly once across both calls");
    }
}

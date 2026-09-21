using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Data;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// <see cref="MongoDbContext"/> transaction + index + query-translation coverage
/// against the replica-set community-server container (via
/// <see cref="CustomWebApplicationFactory"/>): commit, abort-rollback, both
/// non-reentrant <c>TransactAsync</c> overloads, index options, and the
/// Name-Contains (ToLower/Contains → regex) translation used by product search.
/// Docker-gated; skipped without a daemon (see <see cref="RequiresDockerFactAttribute"/>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class MongoTransactionsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MongoTransactionsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [RequiresDockerFact]
    public async Task TransactAsync_Commits_Stock_Order_And_Cart()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var context = sp.GetRequiredService<MongoDbContext>();
        var products = sp.GetRequiredService<IRepository<Product>>();
        var orders = sp.GetRequiredService<IRepository<Order>>();
        var carts = sp.GetRequiredService<IRepository<Cart>>();
        await context.EnsureIndexesAsync();

        var uid = $"txn-user-{Guid.NewGuid():N}";
        var product = await AddProductAsync(products, "Txn Widget", 10);

        await context.TransactAsync(async () =>
        {
            var p = await products.GetByIdAsync(product.Id);
            p!.StockQuantity -= 2;
            await products.UpdateAsync(p.Id, p);
            await orders.AddAsync(new Order
            {
                UserId = uid,
                Items = new List<OrderItem>
                {
                    new() { ProductId = product.Id, Quantity = 2, UnitPrice = 9.99m },
                },
                TotalAmount = 19.98m,
                ShippingAddress = new Address
                {
                    Street = "1 Main St", City = "Springfield", State = "IL",
                    ZipCode = "62701", Country = "USA",
                },
            });
            await carts.AddAsync(new Cart
            {
                UserId = uid,
                Items = new List<CartItem> { new() { ProductId = product.Id, Quantity = 1 } },
            });
        }, CancellationToken.None);

        (await products.GetByIdAsync(product.Id))!.StockQuantity.Should().Be(8);
        (await orders.FindAsync(o => o.UserId == uid)).Should().ContainSingle();
        (await carts.FindOneAsync(c => c.UserId == uid)).Should().NotBeNull();
    }

    [RequiresDockerFact]
    public async Task TransactAsync_Abort_Rolls_Back_Stock_Order_And_Cart()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var context = sp.GetRequiredService<MongoDbContext>();
        var products = sp.GetRequiredService<IRepository<Product>>();
        var orders = sp.GetRequiredService<IRepository<Order>>();
        var carts = sp.GetRequiredService<IRepository<Cart>>();
        await context.EnsureIndexesAsync();

        var uid = $"txn-abort-user-{Guid.NewGuid():N}";
        var product = await AddProductAsync(products, "Abort Widget", 10);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.TransactAsync(async () =>
            {
                var p = await products.GetByIdAsync(product.Id);
                p!.StockQuantity -= 2;
                await products.UpdateAsync(p.Id, p);
                await orders.AddAsync(new Order
                {
                    UserId = uid,
                    TotalAmount = 19.98m,
                    ShippingAddress = new Address
                    {
                        Street = "1 Main St", City = "Springfield", State = "IL",
                        ZipCode = "62701", Country = "USA",
                    },
                });
                await carts.AddAsync(new Cart { UserId = uid });
                throw new InvalidOperationException("forced abort");
            }, CancellationToken.None));

        ex.Message.Should().Be("forced abort");
        (await products.GetByIdAsync(product.Id))!.StockQuantity.Should().Be(10);
        (await orders.FindAsync(o => o.UserId == uid)).Should().BeEmpty();
        (await carts.FindOneAsync(c => c.UserId == uid)).Should().BeNull();
    }

    [RequiresDockerFact]
    public async Task TransactAsync_Nested_Calls_Reuse_Ambient_Session()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var context = sp.GetRequiredService<MongoDbContext>();
        var products = sp.GetRequiredService<IRepository<Product>>();
        await context.EnsureIndexesAsync();

        // Both overloads: when already inside a transaction the work runs on the
        // ambient session without opening a nested transaction (non-reentrant path).
        var outer = await AddProductAsync(products, "Outer Widget", 10);
        var inner = await AddProductAsync(products, "Inner Widget", 10);
        var sessioned = await AddProductAsync(products, "Session Widget", 10);

        await context.TransactAsync(async () =>
        {
            await context.TransactAsync(async () =>
            {
                var p = await products.GetByIdAsync(inner.Id);
                p!.StockQuantity -= 1;
                await products.UpdateAsync(p.Id, p);
            }, CancellationToken.None);

            await context.TransactAsync(async session =>
            {
                session.Should().NotBeNull();
                var p = await products.GetByIdAsync(sessioned.Id);
                p!.StockQuantity -= 1;
                await products.UpdateAsync(p.Id, p);
            });
        }, CancellationToken.None);

        (await products.GetByIdAsync(outer.Id))!.StockQuantity.Should().Be(10);
        (await products.GetByIdAsync(inner.Id))!.StockQuantity.Should().Be(9);
        (await products.GetByIdAsync(sessioned.Id))!.StockQuantity.Should().Be(9);
    }

    [RequiresDockerFact]
    public async Task EnsureIndexes_Creates_Expected_Unique_Sparse_And_Ttl_Options()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoDbContext>();
        await context.EnsureIndexesAsync();

        static async Task<IReadOnlyList<BsonDocument>> ListIndexesAsync(MongoDbContext ctx, string collection)
        {
            using var cursor = await ctx.Database
                .GetCollection<BsonDocument>(collection).Indexes.ListAsync();
            return await cursor.ToListAsync();
        }

        static BsonDocument SingleNamed(IReadOnlyList<BsonDocument> indexes, string name) =>
            indexes.Should().ContainSingle(d => d["name"].AsString == name).Subject;

        var users = await ListIndexesAsync(context, nameof(User));
        SingleNamed(users, "ux_user_email")["unique"].AsBoolean.Should().BeTrue();

        var products = await ListIndexesAsync(context, nameof(Product));
        SingleNamed(products, "ix_product_categoryId").Should().NotBeNull();

        var carts = await ListIndexesAsync(context, nameof(Cart));
        SingleNamed(carts, "ux_cart_userId")["unique"].AsBoolean.Should().BeTrue();

        var orders = await ListIndexesAsync(context, nameof(Order));
        SingleNamed(orders, "ix_order_userId").Should().NotBeNull();
        var idempotency = SingleNamed(orders, "ux_order_userIdempotency");
        idempotency["unique"].AsBoolean.Should().BeTrue();
        idempotency["sparse"].AsBoolean.Should().BeTrue();

        var categories = await ListIndexesAsync(context, nameof(Category));
        SingleNamed(categories, "ix_category_parentCategoryId").Should().NotBeNull();

        var webhooks = await ListIndexesAsync(context, nameof(ProcessedWebhookEvent));
        SingleNamed(webhooks, "ux_webhookevent_eventId")["unique"].AsBoolean.Should().BeTrue();
        SingleNamed(webhooks, "ttl_webhookevent_receivedAt")["expireAfterSeconds"]
            .ToInt64().Should().Be((long)TimeSpan.FromDays(30).TotalSeconds);
    }

    [RequiresDockerFact]
    public async Task ProductSearch_NameContains_Is_CaseInsensitive()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var context = sp.GetRequiredService<MongoDbContext>();
        var products = sp.GetRequiredService<IRepository<Product>>();
        var productService = sp.GetRequiredService<IProductService>();
        await context.EnsureIndexesAsync();

        var tag = Guid.NewGuid().ToString("N");
        await AddProductAsync(products, $"Red WIDGET Pro {tag}", 5);
        await AddProductAsync(products, $"blue widget mini {tag}", 5);
        await AddProductAsync(products, $"Unrelated Gadget {tag}", 5);

        // The service lowercases both sides (p.Name.ToLower().Contains(search));
        // the driver must translate that to a case-insensitive regex server-side.
        var result = await productService.GetProductsAsync(new ProductFilterDto
        {
            Search = $"wIdGeT pRo {tag}",
            PageNumber = 1,
            PageSize = 20,
        });

        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle(p => p.Name.Contains("WIDGET Pro"));
    }

    private static async Task<Product> AddProductAsync(IRepository<Product> products, string name, int stock)
    {
        var product = new Product
        {
            Name = name,
            Description = "integration seed",
            Price = 9.99m,
            StockQuantity = stock,
            CategoryId = ObjectId.GenerateNewId().ToString(),
            IsActive = true,
        };
        await products.AddAsync(product);
        return product;
    }
}

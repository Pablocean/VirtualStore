using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Moq;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

/// <summary>
/// Idempotent checkout (ADR-0006). The service is built WITHOUT a MongoDbContext,
/// so the transactional core runs session-less against mocked repos — the same
/// statements production runs inside <c>TransactAsync</c>.
/// </summary>
public class OrderIdempotencyTests
{
    private static IMapper RealMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private static AddressDto ValidAddress() => new()
    {
        Street = "123 Main St",
        City = "Springfield",
        State = "IL",
        ZipCode = "62701",
        Country = "USA"
    };

    private static Product P(string id, string name, decimal price, int stock) => new()
    {
        Id = id,
        Name = name,
        Price = price,
        Currency = "usd",
        StockQuantity = stock,
        CategoryId = "c1",
        IsActive = true
    };

    private static CreateOrderDto Dto(string productId, int qty, string? key) => new()
    {
        Items = new List<OrderItemDto> { new() { ProductId = productId, Quantity = qty, UnitPrice = 0m } },
        ShippingAddress = ValidAddress(),
        IdempotencyKey = key
    };

    private sealed record Harness(OrderService Service, List<Order> Store, Mock<IRepository<Order>> Orders);

    // WriteError's ctor is internal in driver 3.x; activate it via reflection
    // (driver version is pinned to 3.2.1, so this shape is stable for the suite).
    private static WriteError DuplicateKeyError() =>
        (WriteError)Activator.CreateInstance(
            typeof(WriteError),
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            args: [ServerErrorCategory.DuplicateKey, 11000, "E11000 duplicate key error", new BsonDocument()],
            culture: null)!;

    private static MongoWriteException DuplicateKeyException() => new(
        new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
        DuplicateKeyError(),
        null,
        null);

    private static Harness Build(
        Dictionary<string, Product>? products = null,
        Cart? cart = null,
        Func<Order, List<Order>, bool>? failAdd = null,
        Func<int>? findOverride = null)
    {
        products ??= new Dictionary<string, Product>();
        var store = new List<Order>();
        var findCalls = 0;

        var orders = new Mock<IRepository<Order>>();
        orders.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<Order, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Order, bool>> pred, CancellationToken _) =>
            {
                findCalls++;
                if (findOverride is not null && findOverride() == findCalls)
                    return null; // simulate lost check-then-act race: winner not yet visible
                return store.FirstOrDefault(pred.Compile());
            });
        orders.Setup(r => r.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback((Order o, CancellationToken _) =>
            {
                if (failAdd?.Invoke(o, store) == true)
                    throw DuplicateKeyException();
                store.Add(o);
            })
            .Returns(Task.CompletedTask);
        orders.Setup(r => r.PagedAsync(
                It.IsAny<Expression<Func<Order, bool>>>(),
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<Order, bool>> pred, int page, int size, string? sort, bool desc, CancellationToken _) =>
            {
                var matches = store.Where(pred.Compile()).ToList();
                return ((IReadOnlyList<Order>)matches.Skip((page - 1) * size).Take(size).ToList(), (long)matches.Count);
            });

        var productRepo = new Mock<IRepository<Product>>();
        productRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => products.TryGetValue(id, out var p) ? p : null);

        var carts = new Mock<IRepository<Cart>>();
        carts.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<Cart, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cart);

        var service = new OrderService(orders.Object, carts.Object, productRepo.Object, RealMapper());
        return new Harness(service, store, orders);
    }

    [Fact]
    public async Task Replay_Same_Key_Returns_Existing_Without_Duplicating_Or_Redecrementing()
    {
        var product = P("p1", "Widget", 10m, 5);
        var h = Build(new Dictionary<string, Product> { ["p1"] = product });

        var first = await h.Service.CreateOrderAsync("u1", Dto("p1", 2, "key-1"));
        var replay = await h.Service.CreateOrderAsync("u1", Dto("p1", 2, "key-1"));

        replay.Id.Should().Be(first.Id);
        h.Store.Should().ContainSingle();
        product.StockQuantity.Should().Be(3, "stock must be decremented exactly once");
    }

    [Fact]
    public async Task Same_Key_Different_Payload_Throws_InvalidOperation()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", "Widget", 10m, 5) });

        await h.Service.CreateOrderAsync("u1", Dto("p1", 1, "key-1"));
        var act = () => h.Service.CreateOrderAsync("u1", Dto("p1", 2, "key-1"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different order payload*");
        h.Store.Should().ContainSingle();
    }

    [Fact]
    public async Task Same_Key_Different_Address_Throws_InvalidOperation()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", "Widget", 10m, 5) });

        await h.Service.CreateOrderAsync("u1", Dto("p1", 1, "key-1"));
        var other = Dto("p1", 1, "key-1");
        other.ShippingAddress.City = "Shelbyville";
        var act = () => h.Service.CreateOrderAsync("u1", other);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different order payload*");
    }

    [Fact]
    public async Task DuplicateKey_On_Insert_Falls_Back_To_Existing_Winner()
    {
        var product = P("p1", "Widget", 10m, 5);
        var h = Build(
            new Dictionary<string, Product> { ["p1"] = product },
            failAdd: (o, store) => store.Any(x => x.UserId == o.UserId && x.IdempotencyKey == o.IdempotencyKey),
            findOverride: () => 2); // second check misses: concurrent replay race

        var first = await h.Service.CreateOrderAsync("u1", Dto("p1", 2, "key-race"));
        var replay = await h.Service.CreateOrderAsync("u1", Dto("p1", 2, "key-race"));

        replay.Id.Should().Be(first.Id);
        h.Store.Should().ContainSingle();
        // Mocked repos have no rollback, so the loser's in-memory decrement persists
        // here (5→3→1). With a real transaction the loser's writes abort and stock
        // is decremented exactly once — proven by the integration test.
        product.StockQuantity.Should().Be(1);
    }

    [Fact]
    public async Task Same_Key_Different_User_Creates_Distinct_Order()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", "Widget", 10m, 10) });

        var a = await h.Service.CreateOrderAsync("u1", Dto("p1", 1, "shared-key"));
        var b = await h.Service.CreateOrderAsync("u2", Dto("p1", 1, "shared-key"));

        a.Id.Should().NotBe(b.Id);
        h.Store.Should().HaveCount(2);
    }

    [Fact]
    public async Task Absent_Or_Blank_Key_Creates_Distinct_Orders_With_Null_Key()
    {
        var h = Build(new Dictionary<string, Product> { ["p1"] = P("p1", "Widget", 10m, 10) });

        await h.Service.CreateOrderAsync("u1", Dto("p1", 1, null));
        await h.Service.CreateOrderAsync("u1", Dto("p1", 1, "   "));

        h.Store.Should().HaveCount(2);
        h.Store.Should().OnlyContain(o => o.IdempotencyKey == null);
    }

    [Fact]
    public async Task GetUserOrders_Uses_Server_Side_Paging_And_Honors_100_Cap()
    {
        var h = Build();
        h.Store.AddRange(Enumerable.Range(0, 3).Select(i => new Order
        {
            Id = $"o{i}",
            UserId = "u1",
            Status = OrderStatus.Pending,
            Items = new List<OrderItem>()
        }));

        var result = await h.Service.GetUserOrdersAsync("u1", 1, 500);

        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(3);
        result.PageSize.Should().Be(100);
        h.Orders.Verify(r => r.PagedAsync(
            It.IsAny<Expression<Func<Order, bool>>>(),
            1, 100, null, true, It.IsAny<CancellationToken>()), Times.Once);
        h.Orders.Verify(r => r.FindAsync(It.IsAny<Expression<Func<Order, bool>>>()), Times.Never);
        h.Orders.Verify(r => r.FindAsync(It.IsAny<Expression<Func<Order, bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

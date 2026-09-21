using System.Linq.Expressions;
using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

public class CartServiceTests
{
    private static IMapper RealMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private sealed record Harness(
        CartService Service,
        Mock<IRepository<Cart>> Carts,
        Mock<IRepository<Product>> Products);

    private static Harness Build(Cart? cart = null, Dictionary<string, Product>? products = null, bool cartFound = true)
    {
        products ??= new Dictionary<string, Product>();
        var carts = new Mock<IRepository<Cart>>();
        var productRepo = new Mock<IRepository<Product>>();

        // cartFound=false simulates a repo miss (FindOneAsync returns null).
        var found = cartFound ? cart : null;
        carts.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<Cart, bool>>>()))
            .ReturnsAsync(found);
        carts.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<Cart, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(found);

        productRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => products.TryGetValue(id, out var p) ? p : null);
        productRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => products.TryGetValue(id, out var p) ? p : null);

        return new Harness(new CartService(carts.Object, productRepo.Object, RealMapper()), carts, productRepo);
    }

    private static Product P(string id) => new()
    {
        Id = id,
        Name = "Widget",
        Price = 10m,
        Currency = "usd",
        StockQuantity = 5,
        CategoryId = "c1"
    };

    [Fact]
    public async Task GetCart_Miss_Returns_Empty_Cart_For_User()
    {
        var h = Build(cart: null, cartFound: false);

        var result = await h.Service.GetCartAsync("u1");

        result.UserId.Should().Be("u1");
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCart_Hit_Returns_Mapped_Items()
    {
        var cart = new Cart { Id = "cart1", UserId = "u1" };
        cart.Items.Add(new CartItem { ProductId = "p1", ProductName = "Widget", UnitPrice = 10m, Quantity = 2 });
        var h = Build(cart: cart);

        var result = await h.Service.GetCartAsync("u1");

        result.Items.Should().ContainSingle(i => i.ProductId == "p1" && i.Quantity == 2);
        result.Total.Should().Be(20m);
    }

    [Fact]
    public async Task AddToCart_Product_Missing_Throws_KeyNotFound()
    {
        var h = Build(cart: null, cartFound: false);

        var act = () => h.Service.AddToCartAsync("u1", new AddToCartDto { ProductId = "ghost", Quantity = 1 });

        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*Product not found*");
    }

    [Fact]
    public async Task AddToCart_Existing_Item_Increments_Quantity_And_Updates()
    {
        var cart = new Cart { Id = "cart1", UserId = "u1" };
        cart.Items.Add(new CartItem { ProductId = "p1", ProductName = "Widget", UnitPrice = 10m, Quantity = 2 });
        var h = Build(cart: cart, products: new Dictionary<string, Product> { ["p1"] = P("p1") });

        var result = await h.Service.AddToCartAsync("u1", new AddToCartDto { ProductId = "p1", Quantity = 3 });

        result.Items.Should().ContainSingle(i => i.Quantity == 5);
        h.Carts.Verify(r => r.UpdateAsync("cart1", It.IsAny<Cart>()), Times.Once);
        h.Carts.Verify(r => r.AddAsync(It.IsAny<Cart>()), Times.Never);
    }

    [Fact]
    public async Task AddToCart_New_Item_Adds_Line_With_Server_Price()
    {
        var cart = new Cart { Id = "cart1", UserId = "u1" };
        var h = Build(cart: cart, products: new Dictionary<string, Product> { ["p1"] = P("p1") });

        var result = await h.Service.AddToCartAsync("u1", new AddToCartDto { ProductId = "p1", Quantity = 1 });

        result.Items.Should().ContainSingle(i =>
            i.ProductId == "p1" && i.ProductName == "Widget" && i.UnitPrice == 10m && i.Quantity == 1);
        h.Carts.Verify(r => r.UpdateAsync("cart1", It.IsAny<Cart>()), Times.Once);
    }

    [Fact]
    public async Task AddToCart_Null_Id_Takes_Add_Path()
    {
        // cart.Id == null is only reachable with an explicitly null Id (BaseEntity
        // generates one by default); force it to cover the AddAsync branch.
        var cart = new Cart { UserId = "u1" };
        cart.Id = null!;
        var h = Build(cart: cart, products: new Dictionary<string, Product> { ["p1"] = P("p1") });

        await h.Service.AddToCartAsync("u1", new AddToCartDto { ProductId = "p1", Quantity = 1 });

        h.Carts.Verify(r => r.AddAsync(It.IsAny<Cart>()), Times.Once);
        h.Carts.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<Cart>()), Times.Never);
    }

    [Fact]
    public async Task UpdateCartItem_Null_Cart_Throws_InvalidOperation()
    {
        var h = Build(cart: null, cartFound: false);

        var act = () => h.Service.UpdateCartItemAsync("u1", "p1", 2);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cart not found*");
    }

    [Fact]
    public async Task UpdateCartItem_Missing_Item_Throws_KeyNotFound()
    {
        var h = Build(cart: new Cart { Id = "cart1", UserId = "u1" });

        var act = () => h.Service.UpdateCartItemAsync("u1", "p1", 2);

        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*Product not in cart*");
    }

    [Fact]
    public async Task UpdateCartItem_Success_Sets_Quantity_And_Updates()
    {
        var cart = new Cart { Id = "cart1", UserId = "u1" };
        cart.Items.Add(new CartItem { ProductId = "p1", ProductName = "Widget", UnitPrice = 10m, Quantity = 1 });
        var h = Build(cart: cart);

        var result = await h.Service.UpdateCartItemAsync("u1", "p1", 4);

        result.Items.Should().ContainSingle(i => i.Quantity == 4);
        h.Carts.Verify(r => r.UpdateAsync("cart1", It.IsAny<Cart>()), Times.Once);
    }

    [Fact]
    public async Task RemoveFromCart_Null_Cart_Throws_InvalidOperation()
    {
        var h = Build(cart: null, cartFound: false);

        var act = () => h.Service.RemoveFromCartAsync("u1", "p1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cart not found*");
    }

    [Fact]
    public async Task RemoveFromCart_Removes_Item_And_Updates()
    {
        var cart = new Cart { Id = "cart1", UserId = "u1" };
        cart.Items.Add(new CartItem { ProductId = "p1", ProductName = "Widget", UnitPrice = 10m, Quantity = 1 });
        var h = Build(cart: cart);

        var result = await h.Service.RemoveFromCartAsync("u1", "p1");

        result.Items.Should().BeEmpty();
        h.Carts.Verify(r => r.UpdateAsync("cart1", It.IsAny<Cart>()), Times.Once);
    }

    [Fact]
    public async Task RemoveFromCart_Absent_Item_Still_Updates()
    {
        var cart = new Cart { Id = "cart1", UserId = "u1" };
        cart.Items.Add(new CartItem { ProductId = "p1", ProductName = "Widget", UnitPrice = 10m, Quantity = 1 });
        var h = Build(cart: cart);

        var result = await h.Service.RemoveFromCartAsync("u1", "other");

        result.Items.Should().HaveCount(1);
        h.Carts.Verify(r => r.UpdateAsync("cart1", It.IsAny<Cart>()), Times.Once);
    }

    [Fact]
    public async Task ClearCart_Null_Is_Noop()
    {
        var h = Build(cart: null, cartFound: false);

        await h.Service.ClearCartAsync("u1");

        h.Carts.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
        h.Carts.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClearCart_Existing_Deletes()
    {
        var h = Build(cart: new Cart { Id = "cart1", UserId = "u1" });

        await h.Service.ClearCartAsync("u1");

        h.Carts.Verify(r => r.DeleteAsync("cart1"), Times.Once);
    }
}

using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using Xunit;

namespace VirtualStore.UnitTests.Mappings;

/// <summary>
/// Wave T1c remainder suite: response-DTO coverage for every DTO that the
/// existing <c>MappingProfileTests</c> never names individually. Mapped DTOs go
/// through a real <see cref="IMapper"/> (entity → DTO field asserts, including
/// <c>ReverseMap</c> round-trips); DTOs with intentionally no map
/// (<see cref="TokenResponse"/>, <see cref="UpdateOrderStatusDto"/>,
/// <see cref="CreatePaymentIntentDto"/>, <see cref="RefundPaymentDto"/>,
/// <see cref="StripeWebhookResultDto"/>, <see cref="PaymentIntentResultDto"/>,
/// <see cref="MeExportDto"/>) get construction/retention asserts that pin the
/// no-map intent while covering their accessors.
/// </summary>
public class MappingExtensionTests
{
    private static IMapper Mapper() => new MapperConfiguration(
        cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    [Fact]
    public void Map_User_To_UserDto()
    {
        var dto = Mapper().Map<UserDto>(new User
        {
            Id = "u1",
            Email = "a@b.com",
            Username = "alice",
            FirstName = "Alice",
            LastName = "L",
            PhoneNumber = "123",
            Roles = new List<UserRole> { UserRole.Admin, UserRole.Customer },
            EmailConfirmed = true,
            TwoFactorEnabled = true
        });

        dto.Id.Should().Be("u1");
        dto.Email.Should().Be("a@b.com");
        dto.Username.Should().Be("alice");
        dto.FirstName.Should().Be("Alice");
        dto.Roles.Should().BeEquivalentTo([UserRole.Admin, UserRole.Customer]);
        dto.EmailConfirmed.Should().BeTrue();
        dto.TwoFactorEnabled.Should().BeTrue();
    }

    [Fact]
    public void Map_Product_To_ProductDto()
    {
        var dto = Mapper().Map<ProductDto>(new Product
        {
            Id = "p1",
            Name = "Widget",
            Description = "A widget",
            Price = 19.99m,
            Currency = "usd",
            StockQuantity = 7,
            ImageUrl = "https://img/x.png",
            CategoryId = "c1",
            Tags = ["sale"],
            IsActive = true
        });

        dto.Id.Should().Be("p1");
        dto.Name.Should().Be("Widget");
        dto.Price.Should().Be(19.99m);
        dto.StockQuantity.Should().Be(7);
        dto.CategoryId.Should().Be("c1");
        dto.Tags.Should().ContainSingle("sale");
        dto.IsActive.Should().BeTrue();
        dto.CategoryName.Should().BeNull("CategoryName is Ignore()'d and enriched by the service");
    }

    [Fact]
    public void Map_Category_To_CategoryDto()
    {
        var dto = Mapper().Map<CategoryDto>(new Category
        {
            Id = "c1",
            Name = "Electronics",
            Description = "Gadgets",
            ParentCategoryId = "c0",
            IsActive = true
        });

        dto.Id.Should().Be("c1");
        dto.Name.Should().Be("Electronics");
        dto.ParentCategoryId.Should().Be("c0");
        dto.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Map_Order_To_OrderDto_WithItemsAndAddress()
    {
        var dto = Mapper().Map<OrderDto>(new Order
        {
            Id = "o1",
            UserId = "u1",
            Items = [new OrderItem { ProductId = "p1", ProductName = "Widget", UnitPrice = 10m, Quantity = 2 }],
            TotalAmount = 20m,
            Currency = "usd",
            Status = OrderStatus.PaymentReceived,
            StripePaymentIntentId = "pi_1",
            StripeRefundId = "re_1",
            StripeRefundAmount = 5m,
            ShippingAddress = new Address
            {
                Street = "Main 1", City = "Lima", State = "LIM", ZipCode = "15001", Country = "PE"
            }
        });

        dto.Id.Should().Be("o1");
        dto.UserId.Should().Be("u1");
        dto.Items.Should().ContainSingle(i =>
            i.ProductId == "p1" && i.UnitPrice == 10m && i.Quantity == 2);
        dto.TotalAmount.Should().Be(20m);
        dto.Status.Should().Be(OrderStatus.PaymentReceived);
        dto.StripePaymentIntentId.Should().Be("pi_1");
        dto.StripeRefundId.Should().Be("re_1");
        dto.StripeRefundAmount.Should().Be(5m);
        dto.ShippingAddress.City.Should().Be("Lima");
        dto.ShippingAddress.Country.Should().Be("PE");
    }

    [Fact]
    public void Map_Cart_To_CartDto_WithComputedTotal()
    {
        var dto = Mapper().Map<CartDto>(new Cart
        {
            Id = "cart1",
            UserId = "u1",
            Items =
            [
                new CartItem { ProductId = "p1", ProductName = "A", UnitPrice = 10m, Quantity = 2 },
                new CartItem { ProductId = "p2", ProductName = "B", UnitPrice = 5m, Quantity = 1 }
            ]
        });

        dto.Id.Should().Be("cart1");
        dto.UserId.Should().Be("u1");
        dto.Items.Should().HaveCount(2);
        dto.Total.Should().Be(25m, "CartDto.Total is computed from items");
    }

    [Fact]
    public void Map_CartItem_OrderItem_Address_Reverse_RoundTrips()
    {
        var mapper = Mapper();

        var cartItem = mapper.Map<CartItem>(new CartItemDto
            { ProductId = "p1", ProductName = "A", UnitPrice = 3m, Quantity = 4 });
        cartItem.Should().BeEquivalentTo(new CartItem
            { ProductId = "p1", ProductName = "A", UnitPrice = 3m, Quantity = 4 });
        mapper.Map<CartItemDto>(cartItem).Should().BeEquivalentTo(new CartItemDto
            { ProductId = "p1", ProductName = "A", UnitPrice = 3m, Quantity = 4 });

        var orderItem = mapper.Map<OrderItem>(new OrderItemDto
            { ProductId = "p2", ProductName = "B", UnitPrice = 7m, Quantity = 1 });
        orderItem.ProductName.Should().Be("B");
        mapper.Map<OrderItemDto>(orderItem).Quantity.Should().Be(1);

        var address = mapper.Map<Address>(new AddressDto
            { Street = "S", City = "C", State = "ST", ZipCode = "Z", Country = "PE" });
        mapper.Map<AddressDto>(address).Should().BeEquivalentTo(new AddressDto
            { Street = "S", City = "C", State = "ST", ZipCode = "Z", Country = "PE" });
    }

    [Fact]
    public void Map_EnterpriseInfo_To_EnterpriseInfoDto()
    {
        var dto = Mapper().Map<EnterpriseInfoDto>(new EnterpriseInfo
        {
            Id = "e1",
            CompanyName = "Acme",
            Address = "Main 1",
            Phone = "555-0100",
            Email = "info@acme.com",
            LogoUrl = "https://img/logo.png",
            AboutUs = "About",
            TermsAndConditions = "Terms",
            PrivacyPolicy = "Privacy"
        });

        dto.Id.Should().Be("e1");
        dto.CompanyName.Should().Be("Acme");
        dto.Phone.Should().Be("555-0100");
        dto.PrivacyPolicy.Should().Be("Privacy");
    }

    [Fact]
    public void Map_CreateOrderDto_To_Order_IgnoresServerOwnedMembers()
    {
        var order = Mapper().Map<Order>(new CreateOrderDto
        {
            Items = [new OrderItemDto { ProductId = "p1", ProductName = "A", UnitPrice = 2m, Quantity = 3 }],
            ShippingAddress = new AddressDto
                { Street = "S", City = "C", State = "ST", ZipCode = "Z", Country = "PE" }
        });

        order.Items.Should().ContainSingle();
        order.ShippingAddress.City.Should().Be("C");
        order.UserId.Should().BeEmpty("UserId is Ignore()'d and set from claims");
        order.TotalAmount.Should().Be(0, "TotalAmount is re-priced server-side");
    }

    [Fact]
    public void MeExportDto_Defaults_AreSane()
    {
        var before = DateTime.UtcNow;
        var export = new MeExportDto
        {
            Profile = new UserDto { Id = "u1", Email = "a@b.com" },
            Orders = [new OrderDto { Id = "o1" }],
            Carts = [new CartDto { Id = "c1" }],
            TokenMetadata =
            [
                new RefreshTokenMetadataDto
                {
                    Created = before, CreatedByIp = "1.2.3.4", Expires = before.AddDays(7),
                    HasReplacement = false, IsActive = true
                }
            ]
        };

        export.Profile.Id.Should().Be("u1");
        export.Orders.Should().ContainSingle(o => o.Id == "o1");
        export.Carts.Should().ContainSingle();
        export.TokenMetadata.Should().ContainSingle(t => t.IsActive && !t.HasReplacement);
        export.ExportedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public void TokenResponse_RetainsValues_NoMapByDesign()
    {
        var expires = DateTime.UtcNow.AddMinutes(15);
        var response = new TokenResponse
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = expires,
            RequiresTwoFactor = true
        };

        response.AccessToken.Should().Be("access");
        response.RefreshToken.Should().Be("refresh");
        response.ExpiresAt.Should().Be(expires);
        response.RequiresTwoFactor.Should().BeTrue();
    }

    [Fact]
    public void UpdateOrderStatusDto_RetainsStatus()
    {
        new UpdateOrderStatusDto { Status = OrderStatus.Shipped }.Status
            .Should().Be(OrderStatus.Shipped);
    }

    [Fact]
    public void CreatePaymentIntentDto_And_RefundPaymentDto_RetainValues()
    {
        var create = new CreatePaymentIntentDto
            { Amount = 42.5m, Currency = "usd", CustomerId = "cus_1", OrderId = "o1" };
        create.Amount.Should().Be(42.5m);
        create.CustomerId.Should().Be("cus_1");
        create.OrderId.Should().Be("o1");

        new RefundPaymentDto { Amount = null }.Amount.Should().BeNull("null means full refund");
        new RefundPaymentDto { Amount = 5m }.Amount.Should().Be(5m);
    }

    [Fact]
    public void StripeWebhookResultDto_And_PaymentIntentResultDto_RetainValues()
    {
        var webhook = new StripeWebhookResultDto
        {
            EventType = "payment_intent.succeeded",
            PaymentIntentId = "pi_1",
            OrderId = "o1",
            Succeeded = true,
            Duplicate = false
        };
        webhook.Succeeded.Should().BeTrue();
        webhook.Duplicate.Should().BeFalse();

        var duplicate = new StripeWebhookResultDto
            { EventType = "payment_intent.succeeded", Duplicate = true };
        duplicate.Duplicate.Should().BeTrue();
        duplicate.PaymentIntentId.Should().BeNull();

        var result = new PaymentIntentResultDto
        {
            PaymentIntentId = "pi_1", ClientSecret = "secret",
            Amount = 20m, Currency = "usd", Status = "succeeded", RefundId = "re_1"
        };
        result.RefundId.Should().Be("re_1");
        result.Amount.Should().Be(20m);
    }
}

using FluentAssertions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class OrderItemDtoValidatorTests
{
    private readonly OrderItemDtoValidator _sut = new();

    [Fact]
    public void Valid_Item_Passes()
    {
        _sut.Validate(new OrderItemDto { ProductId = "p1", Quantity = 2, UnitPrice = 5m })
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_ProductId_Fails()
    {
        _sut.Validate(new OrderItemDto { ProductId = "", Quantity = 1, UnitPrice = 5m })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Zero_Quantity_Fails()
    {
        _sut.Validate(new OrderItemDto { ProductId = "p1", Quantity = 0, UnitPrice = 5m })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Quantity_Over_99_Fails()
    {
        _sut.Validate(new OrderItemDto { ProductId = "p1", Quantity = 100, UnitPrice = 5m })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Negative_UnitPrice_Fails()
    {
        _sut.Validate(new OrderItemDto { ProductId = "p1", Quantity = 1, UnitPrice = -1m })
            .IsValid.Should().BeFalse();
    }
}

public class AddressDtoValidatorTests
{
    private readonly AddressDtoValidator _sut = new();

    private static AddressDto Valid() => new()
    {
        Street = "123 Main St",
        City = "Springfield",
        State = "IL",
        ZipCode = "62701",
        Country = "USA"
    };

    [Fact]
    public void Valid_Address_Passes()
    {
        _sut.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_Street_Fails()
    {
        var dto = Valid();
        dto.Street = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_City_Fails()
    {
        var dto = Valid();
        dto.City = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_State_Fails()
    {
        var dto = Valid();
        dto.State = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_ZipCode_Fails()
    {
        var dto = Valid();
        dto.ZipCode = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Country_Fails()
    {
        var dto = Valid();
        dto.Country = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Street_Fails()
    {
        var dto = Valid();
        dto.Street = new string('s', 201);
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_ZipCode_Fails()
    {
        var dto = Valid();
        dto.ZipCode = new string('z', 21);
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }
}

public class CreateOrderDtoValidatorTests
{
    private readonly CreateOrderDtoValidator _sut = new();

    private static CreateOrderDto Valid() => new()
    {
        Items = new List<OrderItemDto> { new() { ProductId = "p1", Quantity = 1, UnitPrice = 5m } },
        ShippingAddress = new AddressDto
        {
            Street = "123 Main St",
            City = "Springfield",
            State = "IL",
            ZipCode = "62701",
            Country = "USA"
        }
    };

    [Fact]
    public void Valid_Order_Passes()
    {
        _sut.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Null_Items_Fail()
    {
        var dto = Valid();
        dto.Items = null!;
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Items_Fail()
    {
        var dto = Valid();
        dto.Items = new List<OrderItemDto>();
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Null_ShippingAddress_Fails()
    {
        var dto = Valid();
        dto.ShippingAddress = null!;
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Invalid_Nested_Item_Fails()
    {
        var dto = Valid();
        dto.Items = new List<OrderItemDto> { new() { ProductId = "", Quantity = 0, UnitPrice = -1m } };
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Invalid_Nested_Address_Fails()
    {
        var dto = Valid();
        dto.ShippingAddress = new AddressDto();
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }
}

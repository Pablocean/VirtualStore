using FluentAssertions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class CreateProductDtoValidatorTests
{
    private readonly CreateProductDtoValidator _sut = new();

    private static CreateProductDto Valid() => new()
    {
        Name = "Widget",
        Description = "A fine widget",
        Price = 9.99m,
        Currency = "USD",
        StockQuantity = 5,
        CategoryId = "c1"
    };

    [Fact]
    public void Valid_Dto_Passes()
    {
        _sut.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_Name_Fails()
    {
        var dto = Valid();
        dto.Name = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Name_Fails()
    {
        var dto = Valid();
        dto.Name = new string('n', 201);
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Description_Fails()
    {
        var dto = Valid();
        dto.Description = new string('d', 2001);
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Negative_Price_Fails()
    {
        var dto = Valid();
        dto.Price = -0.01m;
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Currency_Fails()
    {
        var dto = Valid();
        dto.Currency = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Short_Currency_Fails()
    {
        var dto = Valid();
        dto.Currency = "US";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void NonLetter_Currency_Fails()
    {
        var dto = Valid();
        dto.Currency = "12A";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Negative_Stock_Fails()
    {
        var dto = Valid();
        dto.StockQuantity = -1;
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_CategoryId_Fails()
    {
        var dto = Valid();
        dto.CategoryId = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }
}

using FluentAssertions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class UpdateProductDtoValidatorTests
{
    private readonly UpdateProductDtoValidator _sut = new();

    [Fact]
    public void All_Null_Passes()
    {
        _sut.Validate(new UpdateProductDto()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_Name_Fails()
    {
        _sut.Validate(new UpdateProductDto { Name = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Name_Fails()
    {
        _sut.Validate(new UpdateProductDto { Name = new string('n', 201) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Description_Fails()
    {
        _sut.Validate(new UpdateProductDto { Description = new string('d', 2001) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Negative_Price_Fails()
    {
        _sut.Validate(new UpdateProductDto { Price = -1m }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Currency_Fails()
    {
        _sut.Validate(new UpdateProductDto { Currency = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Short_Currency_Fails()
    {
        _sut.Validate(new UpdateProductDto { Currency = "US" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void NonLetter_Currency_Fails()
    {
        _sut.Validate(new UpdateProductDto { Currency = "12A" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Negative_Stock_Fails()
    {
        _sut.Validate(new UpdateProductDto { StockQuantity = -1 }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_CategoryId_Fails()
    {
        _sut.Validate(new UpdateProductDto { CategoryId = "" }).IsValid.Should().BeFalse();
    }
}

using FluentAssertions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class AddToCartDtoValidatorTests
{
    private readonly AddToCartDtoValidator _sut = new();

    [Fact]
    public void Valid_Dto_Passes()
    {
        _sut.Validate(new AddToCartDto { ProductId = "p1", Quantity = 2 }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_ProductId_Fails()
    {
        _sut.Validate(new AddToCartDto { ProductId = "", Quantity = 1 }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Zero_Quantity_Fails()
    {
        _sut.Validate(new AddToCartDto { ProductId = "p1", Quantity = 0 }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Quantity_Over_99_Fails()
    {
        _sut.Validate(new AddToCartDto { ProductId = "p1", Quantity = 100 }).IsValid.Should().BeFalse();
    }
}

public class UpdateCartItemDtoValidatorTests
{
    private readonly UpdateCartItemDtoValidator _sut = new();

    [Fact]
    public void Valid_Quantity_Passes()
    {
        _sut.Validate(new UpdateCartItemDto { Quantity = 3 }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Zero_Quantity_Fails()
    {
        _sut.Validate(new UpdateCartItemDto { Quantity = 0 }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Quantity_Over_99_Fails()
    {
        _sut.Validate(new UpdateCartItemDto { Quantity = 100 }).IsValid.Should().BeFalse();
    }
}

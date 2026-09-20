using FluentAssertions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class CreateCategoryDtoValidatorTests
{
    private readonly CreateCategoryDtoValidator _sut = new();

    [Fact]
    public void Valid_Dto_Passes()
    {
        _sut.Validate(new CreateCategoryDto { Name = "Cat", Description = "desc" }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_Name_Fails()
    {
        _sut.Validate(new CreateCategoryDto { Name = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Name_Fails()
    {
        _sut.Validate(new CreateCategoryDto { Name = new string('n', 101) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Description_Fails()
    {
        _sut.Validate(new CreateCategoryDto { Name = "Cat", Description = new string('d', 501) })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_ParentCategoryId_When_Provided_Fails()
    {
        _sut.Validate(new CreateCategoryDto { Name = "Cat", ParentCategoryId = "" }).IsValid.Should().BeFalse();
    }
}

public class UpdateCategoryDtoValidatorTests
{
    private readonly UpdateCategoryDtoValidator _sut = new();

    [Fact]
    public void All_Null_Passes()
    {
        _sut.Validate(new UpdateCategoryDto()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_Name_Fails()
    {
        _sut.Validate(new UpdateCategoryDto { Name = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Name_Fails()
    {
        _sut.Validate(new UpdateCategoryDto { Name = new string('n', 101) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Description_Fails()
    {
        _sut.Validate(new UpdateCategoryDto { Description = new string('d', 501) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_ParentCategoryId_When_Provided_Fails()
    {
        _sut.Validate(new UpdateCategoryDto { ParentCategoryId = "" }).IsValid.Should().BeFalse();
    }
}

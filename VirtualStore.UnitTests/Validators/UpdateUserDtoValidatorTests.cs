using FluentAssertions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class UpdateUserDtoValidatorTests
{
    private readonly UpdateUserDtoValidator _sut = new();

    [Fact]
    public void All_Null_Passes()
    {
        _sut.Validate(new UpdateUserDto()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Valid_Fields_Pass()
    {
        _sut.Validate(new UpdateUserDto { Username = "newname", FirstName = "A", LastName = "B", PhoneNumber = "123" })
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_Username_Fails()
    {
        _sut.Validate(new UpdateUserDto { Username = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Short_Username_Fails()
    {
        _sut.Validate(new UpdateUserDto { Username = "ab" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Username_Fails()
    {
        _sut.Validate(new UpdateUserDto { Username = new string('u', 51) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_FirstName_Fails()
    {
        _sut.Validate(new UpdateUserDto { FirstName = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_FirstName_Fails()
    {
        _sut.Validate(new UpdateUserDto { FirstName = new string('a', 101) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_LastName_Fails()
    {
        _sut.Validate(new UpdateUserDto { LastName = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_LastName_Fails()
    {
        _sut.Validate(new UpdateUserDto { LastName = new string('a', 101) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Phone_Fails()
    {
        _sut.Validate(new UpdateUserDto { PhoneNumber = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Phone_Fails()
    {
        _sut.Validate(new UpdateUserDto { PhoneNumber = new string('1', 21) }).IsValid.Should().BeFalse();
    }
}

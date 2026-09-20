using FluentAssertions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class CreateUserDtoValidatorTests
{
    private readonly CreateUserDtoValidator _sut = new();

    private static CreateUserDto Valid() => new()
    {
        Email = "user@test.com",
        Username = "testuser",
        Password = "Password123",
        FirstName = "Test",
        LastName = "User",
        PhoneNumber = "123456789"
    };

    [Fact]
    public void Valid_Dto_Passes()
    {
        _sut.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_Email_Fails()
    {
        var dto = Valid();
        dto.Email = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Malformed_Email_Fails()
    {
        var dto = Valid();
        dto.Email = "bad";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Email_Over_256_Fails()
    {
        var dto = Valid();
        dto.Email = new string('a', 251) + "@t.com"; // 257 chars > MaximumLength(256)
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Username_Fails()
    {
        var dto = Valid();
        dto.Username = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Short_Username_Fails()
    {
        var dto = Valid();
        dto.Username = "ab";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Username_Fails()
    {
        var dto = Valid();
        dto.Username = new string('u', 51);
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_Password_Fails()
    {
        var dto = Valid();
        dto.Password = "";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Short_Password_Fails()
    {
        var dto = Valid();
        dto.Password = "Pw1";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Password_Without_Uppercase_Fails()
    {
        var dto = Valid();
        dto.Password = "password123";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Password_Without_Lowercase_Fails()
    {
        var dto = Valid();
        dto.Password = "PASSWORD123";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Password_Without_Digit_Fails()
    {
        var dto = Valid();
        dto.Password = "PasswordABC";
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_FirstName_Fails()
    {
        var dto = Valid();
        dto.FirstName = new string('a', 101);
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_LastName_Fails()
    {
        var dto = Valid();
        dto.LastName = new string('a', 101);
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Long_Phone_Fails()
    {
        var dto = Valid();
        dto.PhoneNumber = new string('1', 21);
        _sut.Validate(dto).IsValid.Should().BeFalse();
    }
}

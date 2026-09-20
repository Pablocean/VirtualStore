using FluentAssertions;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class ConfirmEmailDtoValidatorTests
{
    private readonly ConfirmEmailDtoValidator _sut = new();

    [Fact]
    public void Valid_Dto_Passes()
    {
        _sut.Validate(new ConfirmEmailDto { Email = "a@b.com", Token = "tok" }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Missing_Token_Fails()
    {
        _sut.Validate(new ConfirmEmailDto { Email = "a@b.com", Token = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Bad_Email_Fails()
    {
        _sut.Validate(new ConfirmEmailDto { Email = "nope", Token = "tok" }).IsValid.Should().BeFalse();
    }
}

public class ResendConfirmationDtoValidatorTests
{
    private readonly ResendConfirmationDtoValidator _sut = new();

    [Fact]
    public void Bad_Email_Fails()
    {
        _sut.Validate(new ResendConfirmationDto { Email = "nope" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Valid_Dto_Passes()
    {
        _sut.Validate(new ResendConfirmationDto { Email = "a@b.com" }).IsValid.Should().BeTrue();
    }
}

public class ChangePasswordDtoValidatorTests
{
    private readonly ChangePasswordDtoValidator _sut = new();

    [Fact]
    public void Strong_New_Password_Passes()
    {
        _sut.Validate(new ChangePasswordDto { CurrentPassword = "OldPass123", NewPassword = "NewPass123" })
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("short1A")]
    [InlineData("alllowercase1")]
    [InlineData("ALLUPPERCASE1")]
    [InlineData("NoDigitsHere")]
    public void Weak_New_Password_Fails(string weak)
    {
        _sut.Validate(new ChangePasswordDto { CurrentPassword = "OldPass123", NewPassword = weak })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void New_Equals_Current_Fails()
    {
        _sut.Validate(new ChangePasswordDto { CurrentPassword = "SamePass1", NewPassword = "SamePass1" })
            .IsValid.Should().BeFalse();
    }
}

public class ForgotPasswordDtoValidatorTests
{
    private readonly ForgotPasswordDtoValidator _sut = new();

    [Fact]
    public void Bad_Email_Fails()
    {
        _sut.Validate(new ForgotPasswordDto { Email = "nope" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Valid_Dto_Passes()
    {
        _sut.Validate(new ForgotPasswordDto { Email = "a@b.com" }).IsValid.Should().BeTrue();
    }
}

public class ResetPasswordDtoValidatorTests
{
    private readonly ResetPasswordDtoValidator _sut = new();

    [Fact]
    public void Valid_Dto_Passes()
    {
        _sut.Validate(new ResetPasswordDto { Email = "a@b.com", Token = "tok", NewPassword = "NewPass123" })
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Weak_New_Password_Fails()
    {
        _sut.Validate(new ResetPasswordDto { Email = "a@b.com", Token = "tok", NewPassword = "weak" })
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Missing_Token_Fails()
    {
        _sut.Validate(new ResetPasswordDto { Email = "a@b.com", Token = "", NewPassword = "NewPass123" })
            .IsValid.Should().BeFalse();
    }
}

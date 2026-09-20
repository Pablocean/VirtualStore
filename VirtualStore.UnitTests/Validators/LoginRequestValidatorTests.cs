using FluentAssertions;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class LoginRequestValidatorTests
{
    private readonly LoginRequestValidator _sut = new();

    private static LoginRequest Valid() => new() { Email = "user@test.com", Password = "Password123" };

    [Fact]
    public void Valid_Request_Passes()
    {
        _sut.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Valid_Request_With_Otp_Passes()
    {
        var req = Valid();
        req.OtpCode = "123456";
        _sut.Validate(req).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_Email_Fails()
    {
        var req = Valid();
        req.Email = "";
        _sut.Validate(req).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Malformed_Email_Fails()
    {
        var req = Valid();
        req.Email = "not-an-email";
        var result = _sut.Validate(req);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Email");
    }

    [Fact]
    public void Empty_Password_Fails()
    {
        var req = Valid();
        req.Password = "";
        _sut.Validate(req).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Otp_Wrong_Length_Fails()
    {
        var req = Valid();
        req.OtpCode = "123";
        var result = _sut.Validate(req);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("OtpCode"));
    }

    [Fact]
    public void Otp_NonDigits_Fail()
    {
        var req = Valid();
        req.OtpCode = "abcdef";
        _sut.Validate(req).IsValid.Should().BeFalse();
    }
}

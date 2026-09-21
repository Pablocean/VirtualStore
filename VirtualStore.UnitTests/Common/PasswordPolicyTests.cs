using FluentAssertions;
using FluentValidation;
using VirtualStore.Application.Common;
using Xunit;

namespace VirtualStore.UnitTests.Common;

/// <summary>
/// Wave T1c remainder suite for <see cref="PasswordPolicy"/>:
/// accept/reject matrix plus <see cref="PasswordPolicy.EnsureValid"/> behavior.
/// Mirrors the <c>CreateUserDtoValidator</c> rule (min 8, upper, lower, digit).
/// </summary>
public class PasswordPolicyTests
{
    [Theory]
    [InlineData("Password1")]
    [InlineData("Abcdefg1")]
    [InlineData("xY9_!@#qwerty")]
    [InlineData("ALLlower1")]
    [InlineData("allUPPER1")]
    public void Validate_AcceptedPasswords_ReturnsNoFailures(string password)
    {
        PasswordPolicy.Validate(password).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "Password is required.")]
    [InlineData("", "Password is required.")]
    [InlineData("Short1a", "Password must be at least 8 characters.")]
    [InlineData("alllowercase1", "Password must contain at least one uppercase letter.")]
    [InlineData("ALLUPPERCASE1", "Password must contain at least one lowercase letter.")]
    [InlineData("NoDigitsHere", "Password must contain at least one digit.")]
    public void Validate_RejectedPasswords_ReportsExpectedMessage(string? password, string message)
    {
        PasswordPolicy.Validate(password!).Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(message);
    }

    [Fact]
    public void Validate_UsesSuppliedPropertyName()
    {
        var failures = PasswordPolicy.Validate("short", "CustomProp");

        failures.Should().NotBeEmpty("short violates length, upper-case and digit rules");
        failures.Should().OnlyContain(f => f.PropertyName == "CustomProp");
    }

    [Fact]
    public void EnsureValid_ValidPassword_DoesNotThrow()
    {
        var act = () => PasswordPolicy.EnsureValid("Password1");

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureValid_InvalidPassword_ThrowsValidationException()
    {
        var act = () => PasswordPolicy.EnsureValid("weak");

        act.Should().Throw<ValidationException>();
    }
}

using FluentAssertions;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Validators;
using Xunit;

namespace VirtualStore.UnitTests.Validators;

public class PurgeRequestDtoValidatorTests
{
    private readonly PurgeRequestDtoValidator _sut = new();

    [Fact]
    public void NonEmpty_Password_Passes()
    {
        _sut.Validate(new PurgeRequestDto { ConfirmPassword = "Current123" }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Missing_Password_Fails(string? password)
    {
        _sut.Validate(new PurgeRequestDto { ConfirmPassword = password! }).IsValid.Should().BeFalse();
    }
}

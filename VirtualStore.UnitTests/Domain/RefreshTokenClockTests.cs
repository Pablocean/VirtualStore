using FluentAssertions;
using VirtualStore.Domain.Entities;
using Xunit;

namespace VirtualStore.UnitTests.Domain;

/// <summary>
/// Wave T1c exclusion-audit suite for the entity-level clock reads
/// (<see cref="RefreshToken.IsExpired"/> / <see cref="RefreshToken.IsActive"/>).
/// Covered deterministically with tolerance windows (past vs future), never
/// exact equality, so no exclusion is needed for these lines.
/// </summary>
public class RefreshTokenClockTests
{
    [Fact]
    public void IsExpired_PastExpiry_ReturnsTrue()
    {
        var token = new RefreshToken { Expires = DateTime.UtcNow.AddMinutes(-1) };

        token.IsExpired.Should().BeTrue();
    }

    [Fact]
    public void IsExpired_FutureExpiry_ReturnsFalse()
    {
        var token = new RefreshToken { Expires = DateTime.UtcNow.AddMinutes(1) };

        token.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void IsActive_FreshToken_ReturnsTrue()
    {
        var token = new RefreshToken
        {
            Expires = DateTime.UtcNow.AddDays(7),
            Revoked = null
        };

        token.IsActive.Should().BeTrue();
    }

    [Fact]
    public void IsActive_RevokedToken_ReturnsFalse()
    {
        var token = new RefreshToken
        {
            Expires = DateTime.UtcNow.AddDays(7),
            Revoked = DateTime.UtcNow.AddMinutes(-5)
        };

        token.IsActive.Should().BeFalse();
    }

    [Fact]
    public void IsActive_ExpiredToken_ReturnsFalse()
    {
        var token = new RefreshToken
        {
            Expires = DateTime.UtcNow.AddMinutes(-1),
            Revoked = null
        };

        token.IsActive.Should().BeFalse();
    }
}

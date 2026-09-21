using System.Security.Claims;
using FluentAssertions;
using VirtualStore.Application.Common;
using Xunit;

namespace VirtualStore.UnitTests.Common;

/// <summary>
/// Wave T1c remainder suite for <see cref="ClaimsPrincipalExtensions.GetUserId"/>:
/// <c>sub</c> first, <see cref="ClaimTypes.NameIdentifier"/> fallback, null when absent.
/// </summary>
public class TokenClaimTypesTests
{
    [Fact]
    public void GetUserId_SubClaim_ReturnsSub()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(TokenClaimTypes.UserIdClaim, "user-1"),
            new Claim(ClaimTypes.NameIdentifier, "user-2")
        ]));

        principal.GetUserId().Should().Be("user-1");
    }

    [Fact]
    public void GetUserId_SubMissing_FallsBackToNameIdentifier()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "user-2"),
            new Claim(ClaimTypes.Email, "a@b.com")
        ]));

        principal.GetUserId().Should().Be("user-2");
    }

    [Fact]
    public void GetUserId_NoIdentityClaims_ReturnsNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Email, "a@b.com"),
            new Claim(ClaimTypes.Role, "Customer")
        ]));

        principal.GetUserId().Should().BeNull();
    }

    [Fact]
    public void GetUserId_EmptyPrincipal_ReturnsNull()
    {
        new ClaimsPrincipal().GetUserId().Should().BeNull();
    }

    [Fact]
    public void UserIdClaim_IsSub()
    {
        TokenClaimTypes.UserIdClaim.Should().Be("sub");
    }
}

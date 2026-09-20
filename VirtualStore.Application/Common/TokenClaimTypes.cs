using System.Security.Claims;

namespace VirtualStore.Application.Common;

/// <summary>
/// Canonical claim type names used in access tokens.
/// "sub" matches JwtRegisteredClaimNames.Sub without taking a dependency on IdentityModel in Application.
/// </summary>
public static class TokenClaimTypes
{
    public const string UserIdClaim = "sub";
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Reads the user id from the "sub" claim, falling back to ClaimTypes.NameIdentifier.
    /// Returns null when neither claim is present.
    /// </summary>
    public static string? GetUserId(this ClaimsPrincipal principal)
    {
        return principal.FindFirst(TokenClaimTypes.UserIdClaim)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}

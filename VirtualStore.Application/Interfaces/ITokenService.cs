using System.Security.Claims;
using VirtualStore.Domain.Entities;

namespace VirtualStore.Application.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(User user);
    string GenerateRefreshToken();
    ClaimsPrincipal GetPrincipalFromExpiredToken(string token);
}
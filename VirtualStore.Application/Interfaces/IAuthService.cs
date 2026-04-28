
using VirtualStore.Application.DTOs.Auth;

namespace VirtualStore.Application.Interfaces;

public interface IAuthService
{
    Task<TokenResponse> LoginAsync(LoginRequest request, string ipAddress);
    Task<TokenResponse> RefreshTokenAsync(string token, string ipAddress);
    Task RevokeTokenAsync(string token, string ipAddress);
    Task<string> GenerateOtpAsync(string email);
    Task<bool> ValidateOtpAsync(string email, string otp);
}
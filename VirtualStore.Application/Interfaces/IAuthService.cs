
using VirtualStore.Application.DTOs.Auth;

namespace VirtualStore.Application.Interfaces;

public interface IAuthService
{
    Task<TokenResponse> LoginAsync(LoginRequest request, string ipAddress);
    Task<TokenResponse> RefreshTokenAsync(string token, string ipAddress);
    Task RevokeTokenAsync(string token, string ipAddress);
    Task ConfirmEmailAsync(ConfirmEmailDto request);
    Task ResendConfirmationAsync(ResendConfirmationDto request);
    Task ChangePasswordAsync(string userId, ChangePasswordDto request, string ipAddress);
    Task ForgotPasswordAsync(ForgotPasswordDto request);
    Task ResetPasswordAsync(ResetPasswordDto request, string ipAddress);
}

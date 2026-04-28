using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;

namespace VirtualStore.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly IRepository<User> _userRepository;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly IMemoryCache _cache;
    private readonly JwtSettings _jwtSettings;

    public AuthService(
        IRepository<User> userRepository,
        ITokenService tokenService,
        IEmailService emailService,
        IMemoryCache cache,
        IOptions<JwtSettings> jwtSettings)
    {
        _userRepository = userRepository;
        _tokenService = tokenService;
        _emailService = emailService;
        _cache = cache;
        _jwtSettings = jwtSettings.Value;
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, string ipAddress)
    {
        var user = await _userRepository.FindOneAsync(u => u.Email == request.Email);
        if (user == null || !VerifyPassword(request.Password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid credentials");

        // Two-factor logic
        if (user.TwoFactorEnabled && string.IsNullOrEmpty(request.OtpCode))
        {
            var otp = GenerateOtp();
            _cache.Set($"otp_{user.Email}", otp, TimeSpan.FromMinutes(10));
            await _emailService.SendOtpEmailAsync(user.Email, otp);
            return new TokenResponse { RequiresTwoFactor = true };
        }

        if (user.TwoFactorEnabled && !string.IsNullOrEmpty(request.OtpCode))
        {
            if (!_cache.TryGetValue($"otp_{user.Email}", out string? cachedOtp) || cachedOtp != request.OtpCode)
                throw new UnauthorizedAccessException("Invalid OTP code");
            _cache.Remove($"otp_{user.Email}");
        }

        // Generate tokens
        var accessToken = _tokenService.GenerateAccessToken(user);
        var refreshToken = _tokenService.GenerateRefreshToken();

        var refreshTokenEntity = new RefreshToken
        {
            Token = refreshToken,
            Expires = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            Created = DateTime.UtcNow,
            CreatedByIp = ipAddress
        };
        user.RefreshTokens.Add(refreshTokenEntity);
        user.LastLoginAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user.Id, user);

        return new TokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = refreshTokenEntity.Expires,
            RequiresTwoFactor = false
        };
    }

    public async Task<TokenResponse> RefreshTokenAsync(string token, string ipAddress)
    {
        var user = await _userRepository.FindOneAsync(u => u.RefreshTokens.Any(rt => rt.Token == token));
        if (user == null)
            throw new UnauthorizedAccessException("Invalid refresh token");

        var refreshToken = user.RefreshTokens.Single(rt => rt.Token == token);
        if (!refreshToken.IsActive)
            throw new UnauthorizedAccessException("Inactive refresh token");

        // Rotation: revoke old and issue new
        refreshToken.Revoked = DateTime.UtcNow;
        refreshToken.RevokedByIp = ipAddress;

        var newRefreshToken = _tokenService.GenerateRefreshToken();
        var newRefreshTokenEntity = new RefreshToken
        {
            Token = newRefreshToken,
            Expires = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            Created = DateTime.UtcNow,
            CreatedByIp = ipAddress,
            ReplacedByToken = newRefreshToken
        };
        user.RefreshTokens.Add(newRefreshTokenEntity);
        await _userRepository.UpdateAsync(user.Id, user);

        var accessToken = _tokenService.GenerateAccessToken(user);
        return new TokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = newRefreshToken,
            ExpiresAt = newRefreshTokenEntity.Expires
        };
    }

    public async Task RevokeTokenAsync(string token, string ipAddress)
    {
        var user = await _userRepository.FindOneAsync(u => u.RefreshTokens.Any(rt => rt.Token == token));
        if (user == null) return;

        var refreshToken = user.RefreshTokens.Single(rt => rt.Token == token);
        refreshToken.Revoked = DateTime.UtcNow;
        refreshToken.RevokedByIp = ipAddress;
        await _userRepository.UpdateAsync(user.Id, user);
    }

    private static string GenerateOtp() => RandomNumberGenerator.GetInt32(100000, 999999).ToString();

    private static bool VerifyPassword(string password, string hash)
        => BCrypt.Net.BCrypt.Verify(password, hash);

    Task<TokenResponse> IAuthService.RefreshTokenAsync(string token, string ipAddress)
    {
        throw new NotImplementedException();
    }

    public Task<string> GenerateOtpAsync(string email)
    {
        throw new NotImplementedException();
    }

    public Task<bool> ValidateOtpAsync(string email, string otp)
    {
        throw new NotImplementedException();
    }

}
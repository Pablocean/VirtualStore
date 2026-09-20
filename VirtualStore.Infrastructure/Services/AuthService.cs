using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;

namespace VirtualStore.Infrastructure.Services;

public class AuthService : IAuthService
{
    private const int MaxOtpAttempts = 5;
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(10);

    private readonly IRepository<User> _userRepository;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly IDistributedCache _cache;
    private readonly JwtSettings _jwtSettings;

    public AuthService(
        IRepository<User> userRepository,
        ITokenService tokenService,
        IEmailService emailService,
        IDistributedCache cache,
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

        // Two-factor logic (OTP cache keys normalized by lowercase email)
        if (user.TwoFactorEnabled && string.IsNullOrEmpty(request.OtpCode))
        {
            var otp = GenerateOtp();
            await _cache.SetStringAsync(
                OtpCacheKey(user.Email),
                otp,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = OtpLifetime });
            await _emailService.SendOtpEmailAsync(user.Email, otp);
            return new TokenResponse { RequiresTwoFactor = true };
        }

        if (user.TwoFactorEnabled && !string.IsNullOrEmpty(request.OtpCode))
        {
            await ValidateOtpOrThrowAsync(user.Email, request.OtpCode);
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

        // Reuse detection: presented token was already revoked -> possible theft.
        // Revoke all active descendant tokens for this user, then reject.
        if (refreshToken.Revoked != null)
        {
            foreach (var activeToken in user.RefreshTokens.Where(rt => rt.IsActive))
            {
                activeToken.Revoked = DateTime.UtcNow;
                activeToken.RevokedByIp = ipAddress;
            }
            await _userRepository.UpdateAsync(user.Id, user);
            throw new UnauthorizedAccessException("Refresh token reuse detected");
        }

        if (!refreshToken.IsActive)
            throw new UnauthorizedAccessException("Inactive refresh token");

        // Rotation: revoke old and issue new. Old token points forward to its replacement.
        var newRefreshToken = _tokenService.GenerateRefreshToken();
        refreshToken.Revoked = DateTime.UtcNow;
        refreshToken.RevokedByIp = ipAddress;
        refreshToken.ReplacedByToken = newRefreshToken;

        var newRefreshTokenEntity = new RefreshToken
        {
            Token = newRefreshToken,
            Expires = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            Created = DateTime.UtcNow,
            CreatedByIp = ipAddress
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

    private static string OtpCacheKey(string email) => $"otp_{email.ToLowerInvariant()}";

    private static string OtpAttemptCacheKey(string email) => $"otp_attempts_{email.ToLowerInvariant()}";

    private async Task ValidateOtpOrThrowAsync(string email, string providedOtp)
    {
        var otpKey = OtpCacheKey(email);
        var attemptKey = OtpAttemptCacheKey(email);

        // Attempt counter is stored as a string-encoded int on IDistributedCache.
        var attemptRaw = await _cache.GetStringAsync(attemptKey);
        if (int.TryParse(attemptRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts)
            && attempts >= MaxOtpAttempts)
            throw new UnauthorizedAccessException("Too many OTP attempts");

        var cachedOtp = await _cache.GetStringAsync(otpKey);
        var valid = cachedOtp != null
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(cachedOtp),
                Encoding.UTF8.GetBytes(providedOtp));

        if (!valid)
        {
            var current = int.TryParse(attemptRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? count
                : 0;
            await _cache.SetStringAsync(
                attemptKey,
                (current + 1).ToString(CultureInfo.InvariantCulture),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = OtpLifetime });
            throw new UnauthorizedAccessException("Invalid OTP code");
        }

        await _cache.RemoveAsync(otpKey);
        await _cache.RemoveAsync(attemptKey);
    }
}

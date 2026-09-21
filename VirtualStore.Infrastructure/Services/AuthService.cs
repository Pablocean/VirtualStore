using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using VirtualStore.Application.Common;
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

    private const int MaxFailedAccessAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum ACTIVE refresh tokens per user (ADR-0010). On login, when the
    /// append would exceed the cap, the oldest-active tokens beyond the cap are
    /// revoked (Revoked + ReplacedByToken set to the new token).
    /// </summary>
    public const int MaxActiveRefreshTokens = 10;

    private static readonly TimeSpan EmailConfirmationLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    private readonly IRepository<User> _userRepository;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly IDistributedCache _cache;
    private readonly JwtSettings _jwtSettings;
    private readonly IDateTimeProvider _clock;

    public AuthService(
        IRepository<User> userRepository,
        ITokenService tokenService,
        IEmailService emailService,
        IDistributedCache cache,
        IOptions<JwtSettings> jwtSettings,
        IDateTimeProvider? clock = null)
    {
        _userRepository = userRepository;
        _tokenService = tokenService;
        _emailService = emailService;
        _cache = cache;
        _jwtSettings = jwtSettings.Value;
        _clock = clock ?? new SystemDateTimeProvider();
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, string ipAddress)
    {
        var user = await _userRepository.FindOneAsync(u => u.Email == request.Email);
        if (user == null)
            throw new UnauthorizedAccessException("Invalid credentials");

        // Expired lockouts clear silently; active lockouts reject before password work.
        if (user.LockoutEnd != null && user.LockoutEnd <= _clock.UtcNow)
        {
            user.LockoutEnd = null;
            user.FailedAccessCount = 0;
        }

        if (user.LockoutEnd != null && user.LockoutEnd > _clock.UtcNow)
            throw new AccountLockedException("Account is locked due to too many failed login attempts.");

        if (!VerifyPassword(request.Password, user.PasswordHash))
        {
            user.FailedAccessCount++;
            if (user.FailedAccessCount >= MaxFailedAccessAttempts)
                user.LockoutEnd = _clock.UtcNow.Add(LockoutDuration);
            await _userRepository.UpdateAsync(user.Id, user);
            throw new UnauthorizedAccessException("Invalid credentials");
        }

        if (!user.EmailConfirmed)
            throw new EmailNotConfirmedException("Email address is not confirmed.");

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
            Expires = _clock.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            Created = _clock.UtcNow,
            CreatedByIp = ipAddress
        };
        user.RefreshTokens.Add(refreshTokenEntity);
        EnforceActiveTokenCap(user, refreshTokenEntity.Token, ipAddress);
        user.LastLoginAt = _clock.UtcNow;
        user.FailedAccessCount = 0;
        user.LockoutEnd = null;
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
                activeToken.Revoked = _clock.UtcNow;
                activeToken.RevokedByIp = ipAddress;
            }
            await _userRepository.UpdateAsync(user.Id, user);
            throw new UnauthorizedAccessException("Refresh token reuse detected");
        }

        if (!refreshToken.IsActive)
            throw new UnauthorizedAccessException("Inactive refresh token");

        // Rotation: revoke old and issue new. Old token points forward to its replacement.
        var newRefreshToken = _tokenService.GenerateRefreshToken();
        refreshToken.Revoked = _clock.UtcNow;
        refreshToken.RevokedByIp = ipAddress;
        refreshToken.ReplacedByToken = newRefreshToken;

        var newRefreshTokenEntity = new RefreshToken
        {
            Token = newRefreshToken,
            Expires = _clock.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            Created = _clock.UtcNow,
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
        refreshToken.Revoked = _clock.UtcNow;
        refreshToken.RevokedByIp = ipAddress;
        await _userRepository.UpdateAsync(user.Id, user);
    }

    public async Task ConfirmEmailAsync(ConfirmEmailDto request)
    {
        var user = await _userRepository.FindOneAsync(u => u.Email == request.Email);
        if (user == null)
            throw new InvalidOperationException("Invalid or expired confirmation token.");

        var cachedToken = await _cache.GetStringAsync(EmailConfirmationCacheKey(user.Id));
        if (cachedToken == null || !FixedTimeEqual(cachedToken, request.Token))
            throw new InvalidOperationException("Invalid or expired confirmation token.");

        user.EmailConfirmed = true;
        await _userRepository.UpdateAsync(user.Id, user);
        await _cache.RemoveAsync(EmailConfirmationCacheKey(user.Id));
    }

    public async Task ResendConfirmationAsync(ResendConfirmationDto request)
    {
        // Always succeeds silently: no enumeration of registered or confirmed emails.
        var user = await _userRepository.FindOneAsync(u => u.Email == request.Email);
        if (user == null || user.EmailConfirmed)
            return;

        var token = GenerateSecureToken();
        await _cache.SetStringAsync(
            EmailConfirmationCacheKey(user.Id),
            token,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = EmailConfirmationLifetime });
        await _emailService.SendConfirmationEmailAsync(user.Email, token);
    }

    public async Task ChangePasswordAsync(string userId, ChangePasswordDto request, string ipAddress)
    {
        var user = await _userRepository.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        if (!VerifyPassword(request.CurrentPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Current password is incorrect.");

        PasswordPolicy.EnsureValid(request.NewPassword);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        RevokeAllRefreshTokens(user, ipAddress);
        await _userRepository.UpdateAsync(user.Id, user);
    }

    public async Task ForgotPasswordAsync(ForgotPasswordDto request)
    {
        // Always succeeds silently: no enumeration of registered emails.
        var user = await _userRepository.FindOneAsync(u => u.Email == request.Email);
        if (user == null)
            return;

        var token = GenerateSecureToken();
        await _cache.SetStringAsync(
            PasswordResetCacheKey(user.Email),
            token,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = PasswordResetLifetime });
        await _emailService.SendPasswordResetEmailAsync(user.Email, token);
    }

    public async Task ResetPasswordAsync(ResetPasswordDto request, string ipAddress)
    {
        var user = await _userRepository.FindOneAsync(u => u.Email == request.Email);
        var cachedToken = user != null
            ? await _cache.GetStringAsync(PasswordResetCacheKey(user.Email))
            : null;

        if (user == null || cachedToken == null || !FixedTimeEqual(cachedToken, request.Token))
            throw new InvalidOperationException("Invalid or expired password reset token.");

        PasswordPolicy.EnsureValid(request.NewPassword);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        RevokeAllRefreshTokens(user, ipAddress);
        await _userRepository.UpdateAsync(user.Id, user);
        await _cache.RemoveAsync(PasswordResetCacheKey(user.Email));
    }

    private static string GenerateOtp() => RandomNumberGenerator.GetInt32(100000, 999999).ToString();

    private static string GenerateSecureToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool VerifyPassword(string password, string hash)
        => BCrypt.Net.BCrypt.Verify(password, hash);

    private static bool FixedTimeEqual(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));

    private void RevokeAllRefreshTokens(User user, string ipAddress)
    {
        var now = _clock.UtcNow;
        foreach (var rt in user.RefreshTokens.Where(rt => rt.Revoked == null))
        {
            rt.Revoked = now;
            rt.RevokedByIp = ipAddress;
        }
    }

    /// <summary>
    /// Caps ACTIVE tokens at <see cref="MaxActiveRefreshTokens"/> (ADR-0010):
    /// revokes the oldest-active tokens beyond the cap, linking each to the
    /// newly issued token via <c>ReplacedByToken</c>. Revoked tokens stay on
    /// the document for the 90-day forensics window (see
    /// <c>RefreshTokenCleanupJob</c>).
    /// </summary>
    private void EnforceActiveTokenCap(User user, string newToken, string ipAddress)
    {
        var active = user.RefreshTokens
            .Where(rt => rt.IsActive)
            .OrderBy(rt => rt.Created)
            .ToList();
        var excess = active.Count - MaxActiveRefreshTokens;
        if (excess <= 0)
            return;

        var now = _clock.UtcNow;
        foreach (var oldest in active.Take(excess))
        {
            oldest.Revoked = now;
            oldest.RevokedByIp = ipAddress;
            oldest.ReplacedByToken = newToken;
        }
    }

    private static string OtpCacheKey(string email) => $"otp_{email.ToLowerInvariant()}";

    private static string OtpAttemptCacheKey(string email) => $"otp_attempts_{email.ToLowerInvariant()}";

    private static string EmailConfirmationCacheKey(string userId) => $"emailconfirm_{userId.ToLowerInvariant()}";

    private static string PasswordResetCacheKey(string email) => $"pwdreset_{email.ToLowerInvariant()}";

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

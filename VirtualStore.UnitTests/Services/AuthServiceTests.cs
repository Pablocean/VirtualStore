using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

public class AuthServiceTests
{
    private const string KnownPassword = "Password123";
    private readonly string _passwordHash = BCrypt.Net.BCrypt.HashPassword(KnownPassword);

    private readonly Mock<IRepository<User>> _users = new();
    private readonly Mock<ITokenService> _tokens = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly MemoryDistributedCache _cache = new(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly IOptions<JwtSettings> _jwtOptions = Options.Create(new JwtSettings
    {
        Secret = "test-secret-that-is-long-enough-for-hmac-256!!!",
        Issuer = "TestIssuer",
        Audience = "TestAudience",
        AccessTokenExpirationMinutes = 15,
        RefreshTokenExpirationDays = 7
    });

    private AuthService Build() =>
        new(_users.Object, _tokens.Object, _email.Object, _cache, _jwtOptions);

    private User StoredUser(Action<User>? configure = null)
    {
        var user = new User
        {
            Id = "u1",
            Email = "user@test.com",
            Username = "user1",
            PasswordHash = _passwordHash,
            EmailConfirmed = true,
            TwoFactorEnabled = false,
            RefreshTokens = new List<RefreshToken>()
        };
        configure?.Invoke(user);
        return user;
    }

    private void SetupFindOne(User? user)
    {
        _users.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()))
            .ReturnsAsync((Expression<Func<User, bool>> pred) =>
                user != null && pred.Compile()(user) ? user : null);
        _users.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<User, bool>> pred, CancellationToken _) =>
                user != null && pred.Compile()(user) ? user : null);
    }

    // NOTE: MemoryDistributedCache (backed by shared in-memory store) needs no disposal.

    [Fact]
    public async Task Login_Valid_Credentials_Returns_Tokens_And_Persists_RefreshToken()
    {
        var user = StoredUser();
        SetupFindOne(user);
        _tokens.Setup(t => t.GenerateAccessToken(user)).Returns("access-1");
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-1");

        var svc = Build();
        var result = await svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "1.2.3.4");

        result.RequiresTwoFactor.Should().BeFalse();
        result.AccessToken.Should().Be("access-1");
        result.RefreshToken.Should().Be("refresh-1");
        user.RefreshTokens.Should().ContainSingle(rt => rt.Token == "refresh-1");
        user.LastLoginAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        _users.Verify(r => r.UpdateAsync(user.Id, user), Times.Once);
    }

    [Fact]
    public async Task Login_Unknown_User_Throws_Unauthorized()
    {
        SetupFindOne(null);
        var svc = Build();

        var act = () => svc.LoginAsync(new LoginRequest { Email = "nobody@test.com", Password = "whatever" }, "ip");

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Invalid credentials*");
    }

    [Fact]
    public async Task Login_Wrong_Password_Uses_Real_BCrypt_Verify_And_Throws()
    {
        var user = StoredUser();
        SetupFindOne(user);
        var svc = Build();

        // Sanity: the precomputed hash really verifies the known password (real BCrypt path).
        BCrypt.Net.BCrypt.Verify(KnownPassword, _passwordHash).Should().BeTrue();

        var act = () => svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = "WrongPass1" }, "ip");

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Invalid credentials*");
    }

    [Fact]
    public async Task Refresh_Active_Token_Rotates_And_Links_ReplacedByToken()
    {
        var user = StoredUser(u => u.RefreshTokens.Add(new RefreshToken
        {
            Token = "old-rt",
            Created = DateTime.UtcNow.AddDays(-1),
            Expires = DateTime.UtcNow.AddDays(6),
            CreatedByIp = "ip"
        }));
        SetupFindOne(user);
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("new-rt");
        _tokens.Setup(t => t.GenerateAccessToken(user)).Returns("access-2");

        var svc = Build();
        var result = await svc.RefreshTokenAsync("old-rt", "9.9.9.9");

        result.RefreshToken.Should().Be("new-rt");
        result.AccessToken.Should().Be("access-2");
        var old = user.RefreshTokens.Single(rt => rt.Token == "old-rt");
        old.Revoked.Should().NotBeNull();
        old.ReplacedByToken.Should().Be("new-rt");
        user.RefreshTokens.Should().Contain(rt => rt.Token == "new-rt");
        _users.Verify(r => r.UpdateAsync(user.Id, user), Times.Once);
    }

    [Fact]
    public async Task Refresh_Unknown_Token_Throws_Unauthorized()
    {
        SetupFindOne(null);
        var svc = Build();

        var act = () => svc.RefreshTokenAsync("nope", "ip");

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Invalid refresh token*");
    }

    [Fact]
    public async Task Refresh_Revoked_Token_Detects_Reuse_Revokes_Active_Tokens_And_Throws()
    {
        var user = StoredUser(u =>
        {
            u.RefreshTokens.Add(new RefreshToken
            {
                Token = "stolen-rt",
                Created = DateTime.UtcNow.AddDays(-2),
                Expires = DateTime.UtcNow.AddDays(5),
                CreatedByIp = "ip",
                Revoked = DateTime.UtcNow.AddHours(-1),
                RevokedByIp = "ip",
                ReplacedByToken = "replacement-rt"
            });
            u.RefreshTokens.Add(new RefreshToken
            {
                Token = "still-active",
                Created = DateTime.UtcNow.AddHours(-2),
                Expires = DateTime.UtcNow.AddDays(5),
                CreatedByIp = "ip"
            });
        });
        SetupFindOne(user);
        var svc = Build();

        var act = () => svc.RefreshTokenAsync("stolen-rt", "attacker-ip");

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*reuse detected*");
        user.RefreshTokens.Where(rt => rt.Token == "still-active")
            .Should().OnlyContain(rt => rt.Revoked != null);
        _users.Verify(r => r.UpdateAsync(user.Id, user), Times.Once);
    }

    [Fact]
    public async Task Refresh_Expired_Token_Throws_Inactive()
    {
        var user = StoredUser(u => u.RefreshTokens.Add(new RefreshToken
        {
            Token = "expired-rt",
            Created = DateTime.UtcNow.AddDays(-10),
            Expires = DateTime.UtcNow.AddMinutes(-1),
            CreatedByIp = "ip"
        }));
        SetupFindOne(user);
        var svc = Build();

        var act = () => svc.RefreshTokenAsync("expired-rt", "ip");

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Inactive refresh token*");
    }

    [Fact]
    public async Task Revoke_Token_Marks_Revoked()
    {
        var user = StoredUser(u => u.RefreshTokens.Add(new RefreshToken
        {
            Token = "rt-1",
            Created = DateTime.UtcNow.AddDays(-1),
            Expires = DateTime.UtcNow.AddDays(6),
            CreatedByIp = "ip"
        }));
        SetupFindOne(user);
        var svc = Build();

        await svc.RevokeTokenAsync("rt-1", "5.5.5.5");

        user.RefreshTokens.Single(rt => rt.Token == "rt-1").Revoked.Should().NotBeNull();
        _users.Verify(r => r.UpdateAsync(user.Id, user), Times.Once);
    }

    [Fact]
    public async Task Login_Unconfirmed_Email_Throws_EmailNotConfirmed()
    {
        var user = StoredUser(u => u.EmailConfirmed = false);
        SetupFindOne(user);
        var svc = Build();

        var act = () => svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "ip");

        await act.Should().ThrowAsync<EmailNotConfirmedException>();
    }

    [Fact]
    public async Task Login_Locked_Account_Throws_AccountLocked()
    {
        var user = StoredUser(u => u.LockoutEnd = DateTime.UtcNow.AddMinutes(10));
        SetupFindOne(user);
        var svc = Build();

        var act = () => svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "ip");

        await act.Should().ThrowAsync<AccountLockedException>();
    }

    [Fact]
    public async Task Login_Wrong_Password_Increments_FailedAccessCount()
    {
        var user = StoredUser();
        SetupFindOne(user);
        var svc = Build();

        var act = () => svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = "WrongPass1" }, "ip");

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Invalid credentials*");
        user.FailedAccessCount.Should().Be(1);
        user.LockoutEnd.Should().BeNull();
        _users.Verify(r => r.UpdateAsync(user.Id, user), Times.Once);
    }

    [Fact]
    public async Task Login_Fifth_Failed_Attempt_Sets_Lockout()
    {
        var user = StoredUser(u => u.FailedAccessCount = 4);
        SetupFindOne(user);
        var svc = Build();

        var act = () => svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = "WrongPass1" }, "ip");

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Invalid credentials*");
        user.FailedAccessCount.Should().Be(5);
        user.LockoutEnd.Should().NotBeNull();
        user.LockoutEnd.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Login_After_Lockout_Rejects_Even_With_Correct_Password()
    {
        var user = StoredUser(u =>
        {
            u.FailedAccessCount = 4;
        });
        SetupFindOne(user);
        var svc = Build();

        // 5th bad attempt triggers the lockout window.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = "WrongPass1" }, "ip"));

        // Correct password inside the window still gets 423.
        var act = () => svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "ip");
        await act.Should().ThrowAsync<AccountLockedException>();
    }

    [Fact]
    public async Task Login_Expired_Lockout_Clears_And_Succeeds()
    {
        var user = StoredUser(u =>
        {
            u.FailedAccessCount = 5;
            u.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
        });
        SetupFindOne(user);
        _tokens.Setup(t => t.GenerateAccessToken(user)).Returns("access-1");
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-1");
        var svc = Build();

        var result = await svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "ip");

        result.AccessToken.Should().Be("access-1");
        user.FailedAccessCount.Should().Be(0);
        user.LockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task Login_Success_Resets_FailedAccessCount()
    {
        var user = StoredUser(u => u.FailedAccessCount = 2);
        SetupFindOne(user);
        _tokens.Setup(t => t.GenerateAccessToken(user)).Returns("access-1");
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-1");
        var svc = Build();

        await svc.LoginAsync(new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "ip");

        user.FailedAccessCount.Should().Be(0);
        user.LockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmEmail_Valid_Token_Sets_Confirmed_And_Is_SingleUse()
    {
        var user = StoredUser(u => u.EmailConfirmed = false);
        SetupFindOne(user);
        var svc = Build();
        await _cache.SetStringAsync("emailconfirm_u1", "tok-123",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });

        await svc.ConfirmEmailAsync(new ConfirmEmailDto { Email = "user@test.com", Token = "tok-123" });

        user.EmailConfirmed.Should().BeTrue();
        (await _cache.GetStringAsync("emailconfirm_u1")).Should().BeNull();

        // Second use fails: token already consumed.
        var act = () => svc.ConfirmEmailAsync(new ConfirmEmailDto { Email = "user@test.com", Token = "tok-123" });
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ConfirmEmail_Wrong_Token_Throws_Conflict()
    {
        var user = StoredUser(u => u.EmailConfirmed = false);
        SetupFindOne(user);
        var svc = Build();
        await _cache.SetStringAsync("emailconfirm_u1", "real-token",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });

        var act = () => svc.ConfirmEmailAsync(new ConfirmEmailDto { Email = "user@test.com", Token = "wrong-token" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid or expired*");
        user.EmailConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task ResendConfirmation_Unconfirmed_Sends_Email_And_Stores_Token()
    {
        var user = StoredUser(u => u.EmailConfirmed = false);
        SetupFindOne(user);
        var svc = Build();

        await svc.ResendConfirmationAsync(new ResendConfirmationDto { Email = "user@test.com" });

        (await _cache.GetStringAsync("emailconfirm_u1")).Should().NotBeNull();
        _email.Verify(e => e.SendConfirmationEmailAsync("user@test.com", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ResendConfirmation_Unknown_Or_Confirmed_Sends_Nothing_But_Succeeds()
    {
        SetupFindOne(null);
        var svc = Build();

        await svc.ResendConfirmationAsync(new ResendConfirmationDto { Email = "nobody@test.com" });

        _email.Verify(e => e.SendConfirmationEmailAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        var confirmed = StoredUser();
        SetupFindOne(confirmed);

        await svc.ResendConfirmationAsync(new ResendConfirmationDto { Email = "user@test.com" });

        _email.Verify(e => e.SendConfirmationEmailAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ChangePassword_Success_Rehashes_And_Revokes_All_Tokens()
    {
        var user = StoredUser(u => u.RefreshTokens.Add(new RefreshToken
        {
            Token = "rt-1",
            Created = DateTime.UtcNow.AddDays(-1),
            Expires = DateTime.UtcNow.AddDays(6),
            CreatedByIp = "ip"
        }));
        _users.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        var svc = Build();

        await svc.ChangePasswordAsync("u1", new ChangePasswordDto { CurrentPassword = KnownPassword, NewPassword = "NewPass123" }, "ip");

        BCrypt.Net.BCrypt.Verify("NewPass123", user.PasswordHash).Should().BeTrue();
        user.RefreshTokens.Should().OnlyContain(rt => rt.Revoked != null);
        _users.Verify(r => r.UpdateAsync(user.Id, user), Times.Once);
    }

    [Fact]
    public async Task ChangePassword_Wrong_Current_Throws_Unauthorized()
    {
        var user = StoredUser();
        _users.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        var svc = Build();

        var act = () => svc.ChangePasswordAsync("u1",
            new ChangePasswordDto { CurrentPassword = "WrongPass1", NewPassword = "NewPass123" }, "ip");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ChangePassword_Weak_New_Password_Throws_Validation()
    {
        var user = StoredUser();
        _users.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        var svc = Build();

        var act = () => svc.ChangePasswordAsync("u1",
            new ChangePasswordDto { CurrentPassword = KnownPassword, NewPassword = "weak" }, "ip");

        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
    }

    [Fact]
    public async Task ForgotPassword_Existing_User_Stores_Token_And_Sends_Email()
    {
        var user = StoredUser();
        SetupFindOne(user);
        var svc = Build();

        await svc.ForgotPasswordAsync(new ForgotPasswordDto { Email = "user@test.com" });

        (await _cache.GetStringAsync("pwdreset_user@test.com")).Should().NotBeNull();
        _email.Verify(e => e.SendPasswordResetEmailAsync("user@test.com", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ForgotPassword_Unknown_User_Succeeds_Silently()
    {
        SetupFindOne(null);
        var svc = Build();

        await svc.ForgotPasswordAsync(new ForgotPasswordDto { Email = "nobody@test.com" });

        _email.Verify(e => e.SendPasswordResetEmailAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResetPassword_Valid_Token_Rehashes_Revokes_And_Is_SingleUse()
    {
        var user = StoredUser(u => u.RefreshTokens.Add(new RefreshToken
        {
            Token = "rt-1",
            Created = DateTime.UtcNow.AddDays(-1),
            Expires = DateTime.UtcNow.AddDays(6),
            CreatedByIp = "ip"
        }));
        SetupFindOne(user);
        var svc = Build();
        await _cache.SetStringAsync("pwdreset_user@test.com", "reset-tok",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });

        await svc.ResetPasswordAsync(new ResetPasswordDto { Email = "user@test.com", Token = "reset-tok", NewPassword = "BrandNew123" }, "ip");

        BCrypt.Net.BCrypt.Verify("BrandNew123", user.PasswordHash).Should().BeTrue();
        user.RefreshTokens.Should().OnlyContain(rt => rt.Revoked != null);
        (await _cache.GetStringAsync("pwdreset_user@test.com")).Should().BeNull();

        // Single-use: replay fails.
        var act = () => svc.ResetPasswordAsync(
            new ResetPasswordDto { Email = "user@test.com", Token = "reset-tok", NewPassword = "Another123" }, "ip");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ResetPassword_Wrong_Token_Throws_Conflict()
    {
        var user = StoredUser();
        SetupFindOne(user);
        var svc = Build();
        await _cache.SetStringAsync("pwdreset_user@test.com", "real-tok",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });

        var act = () => svc.ResetPasswordAsync(
            new ResetPasswordDto { Email = "user@test.com", Token = "bad-tok", NewPassword = "BrandNew123" }, "ip");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid or expired*");
    }
}

using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

/// <summary>
/// ADR-0010: login caps ACTIVE refresh tokens at 10 (oldest-active revoked).
/// </summary>
public class AuthTokenCapTests
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

    private User StoredUserWithTokens(int activeCount, int revokedCount = 0, int expiredUnrevokedCount = 0)
    {
        var now = DateTime.UtcNow;
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
        for (var i = 0; i < activeCount; i++)
            user.RefreshTokens.Add(new RefreshToken
            {
                Token = $"active-{i}",
                Created = now.AddHours(-(activeCount - i)),
                Expires = now.AddDays(6),
                CreatedByIp = "ip"
            });
        for (var i = 0; i < revokedCount; i++)
            user.RefreshTokens.Add(new RefreshToken
            {
                Token = $"revoked-{i}",
                Created = now.AddDays(-2),
                Expires = now.AddDays(5),
                CreatedByIp = "ip",
                Revoked = now.AddHours(-1),
                RevokedByIp = "ip"
            });
        for (var i = 0; i < expiredUnrevokedCount; i++)
            user.RefreshTokens.Add(new RefreshToken
            {
                Token = $"expired-{i}",
                Created = now.AddDays(-10),
                Expires = now.AddMinutes(-1),
                CreatedByIp = "ip"
            });
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

    [Fact]
    public async Task Login_At_Cap_Revokes_Oldest_Active_And_Links_Replacement()
    {
        var user = StoredUserWithTokens(activeCount: 10);
        SetupFindOne(user);
        _tokens.Setup(t => t.GenerateAccessToken(user)).Returns("access-1");
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-new");

        var result = await Build().LoginAsync(
            new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "9.9.9.9");

        result.RefreshToken.Should().Be("refresh-new");
        var actives = user.RefreshTokens.Where(rt => rt.IsActive).ToList();
        actives.Should().HaveCount(10);
        actives.Should().Contain(rt => rt.Token == "refresh-new");
        actives.Should().NotContain(rt => rt.Token == "active-0", "oldest-active must be revoked beyond the cap");

        var evicted = user.RefreshTokens.Single(rt => rt.Token == "active-0");
        evicted.Revoked.Should().NotBeNull();
        evicted.RevokedByIp.Should().Be("9.9.9.9");
        evicted.ReplacedByToken.Should().Be("refresh-new");
    }

    [Fact]
    public async Task Login_Well_Over_Cap_Revokes_Only_Excess()
    {
        var user = StoredUserWithTokens(activeCount: 13);
        SetupFindOne(user);
        _tokens.Setup(t => t.GenerateAccessToken(user)).Returns("access-1");
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-new");

        await Build().LoginAsync(
            new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "ip");

        user.RefreshTokens.Where(rt => rt.IsActive).Should().HaveCount(10);
        user.RefreshTokens.Where(rt => rt.Token is "active-0" or "active-1" or "active-2" or "active-3")
            .Should().OnlyContain(rt => rt.Revoked != null);
        user.RefreshTokens.Single(rt => rt.Token == "active-4").Revoked.Should().BeNull();
    }

    [Fact]
    public async Task Login_Under_Cap_Revokes_Nothing()
    {
        var user = StoredUserWithTokens(activeCount: 3);
        SetupFindOne(user);
        _tokens.Setup(t => t.GenerateAccessToken(user)).Returns("access-1");
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-new");

        await Build().LoginAsync(
            new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "ip");

        user.RefreshTokens.Where(rt => rt.IsActive).Should().HaveCount(4);
        user.RefreshTokens.Where(rt => rt.Token.StartsWith("active-"))
            .Should().OnlyContain(rt => rt.Revoked == null);
    }

    [Fact]
    public async Task Login_Revoked_And_Expired_Tokens_Do_Not_Count_Toward_Cap()
    {
        var user = StoredUserWithTokens(activeCount: 9, revokedCount: 5, expiredUnrevokedCount: 5);
        SetupFindOne(user);
        _tokens.Setup(t => t.GenerateAccessToken(user)).Returns("access-1");
        _tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-new");

        await Build().LoginAsync(
            new LoginRequest { Email = "user@test.com", Password = KnownPassword }, "ip");

        // 9 active + 1 new = 10 → at cap, nothing evicted.
        user.RefreshTokens.Where(rt => rt.IsActive).Should().HaveCount(10);
        user.RefreshTokens.Where(rt => rt.Token.StartsWith("active-"))
            .Should().OnlyContain(rt => rt.Revoked == null);
    }
}

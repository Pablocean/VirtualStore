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
}

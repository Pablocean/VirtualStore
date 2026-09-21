using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using VirtualStore.Application.Common;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

public class TokenServiceTests
{
    private const string Secret = "test-secret-that-is-long-enough-for-hmac-256!!!";
    private const string Issuer = "TestIssuer";
    private const string Audience = "TestAudience";

    private static readonly DateTime FixedNow = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    private static TokenService Build(DateTime? now = null)
    {
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(c => c.UtcNow).Returns(now ?? FixedNow);
        return new TokenService(Options.Create(new JwtSettings
        {
            Secret = Secret,
            Issuer = Issuer,
            Audience = Audience,
            AccessTokenExpirationMinutes = 15,
            RefreshTokenExpirationDays = 7
        }), clock.Object);
    }

    private static User TestUser() => new()
    {
        Id = "u1",
        Email = "user@test.com",
        Roles = new List<UserRole> { UserRole.Customer, UserRole.Admin }
    };

    [Fact]
    public void GenerateAccessToken_Carries_Expected_Claims()
    {
        var token = Build().GenerateAccessToken(TestUser());
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value.Should().Be("u1");
        jwt.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value.Should().Be("u1");
        jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Email).Value.Should().Be("user@test.com");
        var jti = jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        jti.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(jti, out _).Should().BeTrue("jti must be a guid");
        jwt.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo("Customer", "Admin");
    }

    [Fact]
    public void GenerateAccessToken_Sets_Issuer_Audience_And_Expiry_From_Fake_Clock()
    {
        var token = Build().GenerateAccessToken(TestUser());
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Issuer.Should().Be(Issuer);
        jwt.Audiences.Should().Contain(Audience);
        jwt.ValidTo.Should().BeCloseTo(FixedNow.AddMinutes(15), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void GenerateAccessToken_Jti_Is_Unique_Per_Token()
    {
        var svc = Build();
        var first = new JwtSecurityTokenHandler().ReadJwtToken(svc.GenerateAccessToken(TestUser()));
        var second = new JwtSecurityTokenHandler().ReadJwtToken(svc.GenerateAccessToken(TestUser()));

        first.Id.Should().NotBe(second.Id);
    }

    [Fact]
    public void GenerateRefreshToken_Is_32_Random_Bytes_And_Unique()
    {
        var svc = Build();

        var first = svc.GenerateRefreshToken();
        var second = svc.GenerateRefreshToken();

        Convert.FromBase64String(first).Should().HaveCount(32);
        Convert.FromBase64String(second).Should().HaveCount(32);
        first.Should().NotBe(second);
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_Valid_Token_Returns_Principal()
    {
        var svc = Build();
        var token = svc.GenerateAccessToken(TestUser());

        var principal = svc.GetPrincipalFromExpiredToken(token);

        principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value.Should().Be("u1");
        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be("u1");
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_Expired_Token_Still_Returns_Principal()
    {
        var svc = Build();
        var expired = CraftToken(
            Secret, Issuer, Audience,
            new Claim(JwtRegisteredClaimNames.Sub, "u1"),
            expires: FixedNow.AddMinutes(-5),
            algorithm: SecurityAlgorithms.HmacSha256);

        var principal = svc.GetPrincipalFromExpiredToken(expired);

        principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value.Should().Be("u1");
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_Bad_Signature_Throws()
    {
        var svc = Build();
        var tampered = CraftToken(
            "a-different-secret-that-is-also-long-enough-1234",
            Issuer, Audience,
            new Claim(JwtRegisteredClaimNames.Sub, "u1"),
            expires: FixedNow.AddMinutes(15),
            algorithm: SecurityAlgorithms.HmacSha256);

        var act = () => svc.GetPrincipalFromExpiredToken(tampered);

        act.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void GetPrincipalFromExpiredToken_Wrong_Algorithm_Throws()
    {
        // HS512 needs a >=64-byte key, so this arm uses a longer secret for both
        // the service and the crafted token: signature validates, alg check fails.
        const string longSecret = "test-secret-that-is-long-enough-for-hmac-512-algorithm-check-0123456789!!!";
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(c => c.UtcNow).Returns(FixedNow);
        var svc = new TokenService(Options.Create(new JwtSettings
        {
            Secret = longSecret,
            Issuer = Issuer,
            Audience = Audience,
            AccessTokenExpirationMinutes = 15,
            RefreshTokenExpirationDays = 7
        }), clock.Object);
        var wrongAlg = CraftToken(
            longSecret, Issuer, Audience,
            new Claim(JwtRegisteredClaimNames.Sub, "u1"),
            expires: FixedNow.AddMinutes(15),
            algorithm: SecurityAlgorithms.HmacSha512);

        var act = () => svc.GetPrincipalFromExpiredToken(wrongAlg);

        act.Should().Throw<SecurityTokenException>().WithMessage("Invalid token");
    }

    private static string CraftToken(
        string secret, string issuer, string audience, Claim claim, DateTime expires, string algorithm)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, algorithm);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { claim },
            expires: expires,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

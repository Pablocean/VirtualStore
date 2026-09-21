using System.IdentityModel.Tokens.Jwt;
using System.Linq.Expressions;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using Quartz;
using Stripe;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.BackgroundServices;
using VirtualStore.Infrastructure.Data;
using VirtualStore.Infrastructure.Email;
using VirtualStore.Infrastructure.Services;
using VirtualStore.Infrastructure.Stripe;
using Xunit;

namespace VirtualStore.UnitTests.Seams;

/// <summary>
/// Wave T0 seam smoke tests: every injectable seam resolves its production
/// default, the <c>InternalsVisibleTo</c> surface is reachable, the clock
/// seam drives expiry/lockout timestamps deterministically, and CacheService
/// still round-trips through a real (in-memory) HybridCache.
/// No network I/O: SMTP/Stripe/Mongo calls are never made.
/// </summary>
public class SeamSmokeTests
{
    [Fact]
    public void Clock_DefaultProvider_Returns_Near_UtcNow()
    {
        var before = DateTime.UtcNow;
        var now = new SystemDateTimeProvider().UtcNow;
        var after = DateTime.UtcNow;

        now.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Email_SmtpFactory_Default_Creates_Client_And_Service_Constructs()
    {
        using var client = new SmtpClientFactory().CreateClient();
        client.Should().NotBeNull();

        var settings = Options.Create(new EmailSettings
        {
            SmtpServer = "localhost",
            Port = 25,
            SenderEmail = "seam@test.com",
            SenderName = "Seam",
            Username = "u",
            Password = "p",
            EnableSsl = false
        });

        // Null-coalescing default: both constructions behave identically.
        new EmailService(settings).Should().NotBeNull();
        new EmailService(settings, new SmtpClientFactory()).Should().NotBeNull();
    }

    [Fact]
    public void Stripe_Factory_Default_Creates_Services_And_Service_Constructs()
    {
        var factory = new StripeClientFactory();
        factory.CreatePaymentIntentService("sk_test_seam").Should().NotBeNull();
        factory.CreateRefundService("sk_test_seam").Should().NotBeNull();

        var options = Options.Create(new StripeSettings
        {
            SecretKey = "sk_test_seam",
            WebhookSecret = "whsec_seam"
        });
        new StripePaymentService(options).Should().NotBeNull();
        new StripePaymentService(options, stripeFactory: new StripeClientFactory()).Should().NotBeNull();
    }

    [Fact]
    public void Stripe_Factory_ConstructEvent_Uses_Real_Signature_Validation()
    {
        var factory = new StripeClientFactory();

        var act = () => factory.ConstructEvent("{}", "t=123,v1=deadbeef", "whsec_seam");

        act.Should().Throw<StripeException>("default factory must delegate to EventUtility.ConstructEvent");
    }

    [Fact]
    public void Stripe_IsTransient_Matrix_Via_Internal_Seam()
    {
        // Internal access proves [assembly: InternalsVisibleTo("VirtualStore.UnitTests")].
        StripePaymentService.IsTransientStripeError(new StripeException("no-http-response"))
            .Should().BeTrue("default HttpStatusCode (0) means no HTTP response was received");

        StripePaymentService.IsTransientStripeError(
                new StripeException("rate-limited") { HttpStatusCode = (HttpStatusCode)429 })
            .Should().BeTrue();

        StripePaymentService.IsTransientStripeError(
                new StripeException("client-error") { HttpStatusCode = HttpStatusCode.BadRequest })
            .Should().BeFalse("4xx (other than 408/429) is not retried");
    }

    [Fact]
    public async Task Mongo_Session_Setter_And_Transact_Overloads_Use_Ambient_Session()
    {
        var context = new MongoDbContext(Options.Create(new MongoDbSettings
        {
            ConnectionString = "mongodb://localhost:27017",
            DatabaseName = "VirtualStoreSeamTest"
        }));
        context.CurrentSession.Should().BeNull();

        // Internal setter: proves InternalsVisibleTo; no session is ever started (no I/O).
        var session = new Mock<IClientSessionHandle>().Object;
        context.CurrentSession = session;

        var ran = false;
        Func<Task> legacyWork = () =>
        {
            ran = true;
            return Task.CompletedTask;
        };
        await context.TransactAsync(legacyWork, CancellationToken.None);
        ran.Should().BeTrue("re-entrant Func<Task> overload runs on the ambient session");

        IClientSessionHandle? seen = null;
        await context.TransactAsync(s =>
        {
            seen = s;
            return Task.CompletedTask;
        }, CancellationToken.None);
        seen.Should().BeSameAs(session, "session-aware overload must hand over the ambient session");

        context.CurrentSession.Should().BeSameAs(session, "re-entrant path must not clear the ambient session");
        context.CurrentSession = null;
    }

    [Fact]
    public void CacheService_Real_HybridCache_RoundTrips_Without_Caching_Misses()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        using var provider = services.BuildServiceProvider();
        var hybrid = provider.GetRequiredService<HybridCache>();

        // Back-compat ctor path (wraps HybridCache in the default adapter).
        var viaConvenience = new CacheService(hybrid);
        viaConvenience.Get<string>("seam:missing").Should().BeNull();
        viaConvenience.TryGet<string>("seam:missing", out var miss).Should().BeFalse();
        miss.Should().BeNull("misses are never written");

        viaConvenience.Set("seam:key", "seam-value");
        viaConvenience.Get<string>("seam:key").Should().Be("seam-value");
        viaConvenience.TryGet<string>("seam:key", out var hit).Should().BeTrue();
        hit.Should().Be("seam-value");

        viaConvenience.Remove("seam:key");
        viaConvenience.Get<string>("seam:key").Should().BeNull("removed entries must miss again");

        // Adapter-injected path (used by DI).
        var viaAdapter = new CacheService(new HybridCacheAdapter(hybrid));
        viaAdapter.Set("seam:key2", "v2", TimeSpan.FromMinutes(1));
        viaAdapter.Get<string>("seam:key2").Should().Be("v2");
    }

    [Fact]
    public void TokenService_Uses_Injected_Clock_For_AccessToken_Expiry()
    {
        var fixedNow = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(c => c.UtcNow).Returns(fixedNow);

        var svc = new VirtualStore.Infrastructure.Services.TokenService(
            Options.Create(new JwtSettings
            {
                Secret = "test-secret-that-is-long-enough-for-hmac-256!!!",
                Issuer = "TestIssuer",
                Audience = "TestAudience",
                AccessTokenExpirationMinutes = 15,
                RefreshTokenExpirationDays = 7
            }),
            clock.Object);

        var token = svc.GenerateAccessToken(new User
        {
            Id = "u1",
            Email = "user@test.com",
            Roles = new List<UserRole> { UserRole.Customer }
        });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.ValidTo.Should().BeCloseTo(fixedNow.AddMinutes(15), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AuthService_Uses_Injected_Clock_For_Expiry_And_Login_Timestamps()
    {
        var fixedNow = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(c => c.UtcNow).Returns(fixedNow);

        var passwordHash = BCrypt.Net.BCrypt.HashPassword("Password123");
        var user = new User
        {
            Id = "u1",
            Email = "user@test.com",
            Username = "user1",
            PasswordHash = passwordHash,
            EmailConfirmed = true,
            TwoFactorEnabled = false,
            RefreshTokens = new List<RefreshToken>()
        };
        var users = new Mock<IRepository<User>>();
        users.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()))
            .ReturnsAsync((Expression<Func<User, bool>> pred) => pred.Compile()(user) ? user : null);
        users.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<User, bool>> pred, CancellationToken _) => pred.Compile()(user) ? user : null);
        var tokens = new Mock<ITokenService>();
        tokens.Setup(t => t.GenerateAccessToken(user)).Returns("access-1");
        tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh-1");

        var svc = new AuthService(
            users.Object,
            tokens.Object,
            new Mock<IEmailService>().Object,
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            Options.Create(new JwtSettings
            {
                Secret = "test-secret-that-is-long-enough-for-hmac-256!!!",
                Issuer = "TestIssuer",
                Audience = "TestAudience",
                AccessTokenExpirationMinutes = 15,
                RefreshTokenExpirationDays = 7
            }),
            clock.Object);

        var result = await svc.LoginAsync(
            new LoginRequest { Email = "user@test.com", Password = "Password123" }, "1.2.3.4");

        result.RefreshToken.Should().Be("refresh-1");
        user.LastLoginAt.Should().Be(fixedNow);
        user.RefreshTokens.Should().ContainSingle(rt =>
            rt.Token == "refresh-1"
            && rt.Created == fixedNow
            && rt.Expires == fixedNow.AddDays(7));
    }

    [Fact]
    public async Task CleanupJob_Uses_Injected_Clock_For_Purge_Window()
    {
        var fixedNow = new DateTime(2026, 4, 1, 3, 0, 0, DateTimeKind.Utc);
        var clock = new Mock<IDateTimeProvider>();
        clock.SetupGet(c => c.UtcNow).Returns(fixedNow);

        var dirty = new User
        {
            Id = "dirty",
            RefreshTokens = new List<RefreshToken>
            {
                new() { Token = "old", Created = fixedNow.AddDays(-10), Expires = fixedNow.AddDays(-1), CreatedByIp = "ip" }
            }
        };
        var users = new Mock<IRepository<User>>();
        users.Setup(r => r.PagedAsync(
                It.IsAny<Expression<Func<User, bool>>>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<User, bool>> _, int page, int __, string? ___, bool ____, CancellationToken _____) =>
                page == 1
                    ? ((IReadOnlyList<User>)new List<User> { dirty }, 1L)
                    : ((IReadOnlyList<User>)new List<User>(), 0L));
        var context = new Mock<IJobExecutionContext>();
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        await new RefreshTokenCleanupJob(users.Object, new Mock<ILogger<RefreshTokenCleanupJob>>().Object, clock.Object)
            .Execute(context.Object);

        dirty.RefreshTokens.Should().BeEmpty();
        dirty.UpdatedAt.Should().Be(fixedNow);
        users.Verify(r => r.UpdateAsync("dirty", dirty, It.IsAny<CancellationToken>()), Times.Once);
    }
}

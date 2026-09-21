using System.Linq.Expressions;
using System.Reflection;
using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using Stripe;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Data;
using VirtualStore.Infrastructure.Repositories;
using VirtualStore.Infrastructure.Services;
using VirtualStore.Infrastructure.Stripe;
using Xunit;

namespace VirtualStore.UnitTests.Audit;

/// <summary>
/// Wave T1c exclusion audit for every T0-flagged clock line:
/// <c>ProcessedWebhookEvent.ReceivedAt</c> (+ <c>StripePaymentService:161</c>),
/// <c>BaseEntity.CreatedAt</c> (+ <c>MongoRepository</c> stamps),
/// <c>MeExportDto.ExportedAt</c> (+ <c>UserService:123</c>),
/// <c>RefreshToken.IsExpired/IsActive</c> (see <c>Domain.RefreshTokenClockTests</c>).
/// Each item below is COVERED with a deterministic tolerance-window assert
/// (never exact <c>UtcNow</c> equality), so this audit adds zero production
/// exclusions. The only deferral is the non-reentrant <c>TransactAsync</c>
/// branches, which need a live replica-set session (Wave T2 Docker leg).
/// </summary>
public class ClockInitializerAuditTests
{
    private static IMapper Mapper() => new MapperConfiguration(
        cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    [Fact]
    public void BaseEntity_CreatedAt_DefaultsNearUtcNow()
    {
        var before = DateTime.UtcNow;
        var user = new User { Email = "a@b.com" };

        user.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        user.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void ProcessedWebhookEvent_ReceivedAt_DefaultsNearUtcNow()
    {
        var before = DateTime.UtcNow;
        var evt = new ProcessedWebhookEvent { EventId = "evt_1", Type = "payment_intent.succeeded" };

        evt.ReceivedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public void MeExportDto_ExportedAt_DefaultsNearUtcNow()
    {
        var before = DateTime.UtcNow;

        new MeExportDto().ExportedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public async Task StripePaymentService_HandleWebhook_ReceivedAtStampedNearUtcNow()
    {
        // Covers StripePaymentService.cs ReceivedAt initializer through the real
        // dedup path (miss → insert), with a fake factory so no Stripe I/O occurs.
        var stripeEvent = new Event
        {
            Id = "evt_audit_1",
            Type = "payment_intent.succeeded",
            Data = new EventData
            {
                Object = new PaymentIntent
                {
                    Id = "pi_audit_1",
                    Amount = 2000,
                    Currency = "usd",
                    Status = "succeeded",
                    Metadata = new Dictionary<string, string> { ["orderId"] = "o1" }
                }
            }
        };
        var factory = new Mock<IStripeClientFactory>();
        factory.Setup(f => f.ConstructEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stripeEvent);

        ProcessedWebhookEvent? captured = null;
        var webhooks = new Mock<IRepository<ProcessedWebhookEvent>>();
        webhooks.Setup(r => r.FindOneAsync(
                It.IsAny<Expression<Func<ProcessedWebhookEvent, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessedWebhookEvent?)null);
        webhooks.Setup(r => r.AddAsync(It.IsAny<ProcessedWebhookEvent>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessedWebhookEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        var svc = new StripePaymentService(
            Options.Create(new StripeSettings { SecretKey = "sk_test", WebhookSecret = "whsec_test" }),
            webhooks.Object,
            stripeFactory: factory.Object);

        var before = DateTime.UtcNow;
        var result = await svc.HandleWebhookEventAsync("{}", "sig");

        result.Succeeded.Should().BeTrue();
        result.PaymentIntentId.Should().Be("pi_audit_1");
        result.OrderId.Should().Be("o1");
        captured.Should().NotBeNull();
        captured!.EventId.Should().Be("evt_audit_1");
        captured.ReceivedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public async Task UserService_GetExport_ExportedAtStampedNearUtcNow()
    {
        // Covers UserService.cs ExportedAt assignment with mocked repos + real mapper.
        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = "u1",
            Email = "a@b.com",
            Username = "alice",
            RefreshTokens =
            [
                new RefreshToken
                {
                    Token = "rt-active", Created = now.AddDays(-1),
                    Expires = now.AddDays(6), CreatedByIp = "1.2.3.4"
                },
                new RefreshToken
                {
                    Token = "rt-revoked", Created = now.AddDays(-2),
                    Expires = now.AddDays(5), CreatedByIp = "1.2.3.4",
                    Revoked = now.AddDays(-1)
                }
            ]
        };
        var users = new Mock<IRepository<User>>();
        users.Setup(r => r.GetByIdAsync("u1")).ReturnsAsync(user);
        var orders = new Mock<IRepository<Order>>();
        orders.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Order, bool>>>()))
            .ReturnsAsync([]);
        var carts = new Mock<IRepository<Cart>>();
        carts.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Cart, bool>>>()))
            .ReturnsAsync([]);

        var svc = new UserService(
            users.Object, Mapper(), orders.Object, carts.Object,
            new Mock<IDistributedCache>().Object);

        var before = DateTime.UtcNow;
        var export = await svc.GetExportAsync("u1");

        export.Profile.Email.Should().Be("a@b.com");
        export.ExportedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        export.TokenMetadata.Should().HaveCount(2);
        export.TokenMetadata.Should().ContainSingle(t => t.IsActive && !t.HasReplacement);
        export.TokenMetadata.Should().ContainSingle(t => !t.IsActive && t.Revoked != null);
    }

    [Fact]
    public async Task MongoRepository_AddAsync_StampsCreatedAtNearUtcNow()
    {
        // Covers MongoRepository.cs CreatedAt initializer without Docker: the
        // collection is swapped for a mock via reflection (no production change).
        var (repo, collection) = RepositoryWithMockCollection<User>();
        var entity = new User { Email = "a@b.com" };

        var before = DateTime.UtcNow;
        await repo.AddAsync(entity);

        entity.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        collection.Verify(c => c.InsertOneAsync(
            It.Is<User>(u => ReferenceEquals(u, entity)),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MongoRepository_UpdateAsync_StampsUpdatedAtNearUtcNow()
    {
        // Covers MongoRepository.cs UpdatedAt initializer, same seam as above.
        var (repo, collection) = RepositoryWithMockCollection<User>();
        var entity = new User { Email = "a@b.com", UpdatedAt = null };

        var before = DateTime.UtcNow;
        await repo.UpdateAsync("u1", entity);

        entity.UpdatedAt.Should().NotBeNull();
        entity.UpdatedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        collection.Verify(c => c.ReplaceOneAsync(
            It.IsAny<FilterDefinition<User>>(),
            It.Is<User>(u => ReferenceEquals(u, entity)),
            It.IsAny<ReplaceOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (MongoRepository<T> Repo, Mock<IMongoCollection<T>> Collection)
        RepositoryWithMockCollection<T>() where T : BaseEntity
    {
        // Real context construction opens no connection; only the collection is
        // swapped so timestamp logic runs without a live MongoDB.
        var context = new MongoDbContext(Options.Create(new MongoDbSettings
        {
            ConnectionString = "mongodb://localhost:27017",
            DatabaseName = "VirtualStoreAuditTest"
        }));
        var repo = new MongoRepository<T>(context);
        var collection = new Mock<IMongoCollection<T>>();
        collection.Setup(c => c.InsertOneAsync(
                It.IsAny<T>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        collection.Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<T>>(), It.IsAny<T>(),
                It.IsAny<ReplaceOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<ReplaceOneResult>(null!));
        typeof(MongoRepository<T>)
            .GetField("_collection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(repo, collection.Object);
        return (repo, collection);
    }
}

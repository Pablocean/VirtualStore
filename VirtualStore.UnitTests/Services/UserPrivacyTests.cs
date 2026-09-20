using System.Linq.Expressions;
using System.Text.Json;
using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Mappings;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.UnitTests.Services;

/// <summary>
/// ADR-0010: GDPR export shape (no secrets) + purge (hard-delete, cart
/// removal, order pseudonymization, cache clearing).
/// </summary>
public class UserPrivacyTests
{
    private const string KnownPassword = "Password123";
    private readonly string _passwordHash = BCrypt.Net.BCrypt.HashPassword(KnownPassword);

    private readonly Mock<IRepository<User>> _users = new();
    private readonly Mock<IRepository<Order>> _orders = new();
    private readonly Mock<IRepository<Cart>> _carts = new();
    private readonly MemoryDistributedCache _cache = new(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly IMapper _mapper = new MapperConfiguration(
        cfg => cfg.AddProfile<MappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private UserService Build() =>
        new(_users.Object, _mapper, _orders.Object, _carts.Object, _cache);

    private User StoredUser()
    {
        var now = DateTime.UtcNow;
        return new User
        {
            Id = "507f1f77bcf86cd799439011",
            Email = "User@Test.com",
            Username = "user1",
            PasswordHash = _passwordHash,
            EmailConfirmed = true,
            RefreshTokens = new List<RefreshToken>
            {
                new() { Token = "secret-rt-1", Created = now.AddDays(-2), Expires = now.AddDays(5), CreatedByIp = "1.1.1.1" },
                new() { Token = "secret-rt-2", Created = now.AddDays(-3), Expires = now.AddDays(4), CreatedByIp = "2.2.2.2",
                    Revoked = now.AddHours(-1), RevokedByIp = "3.3.3.3", ReplacedByToken = "secret-rt-3" }
            }
        };
    }

    private void SetupUserRepo(User? user)
    {
        _users.Setup(r => r.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => user != null && user.Id == id ? user : null);
        _users.Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => user != null && user.Id == id ? user : null);
    }

    private static void SetupFind<T>(Mock<IRepository<T>> repo, List<T> store) where T : BaseEntity
    {
        repo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<T, bool>>>()))
            .ReturnsAsync((Expression<Func<T, bool>> pred) => store.Where(pred.Compile()).ToList());
        repo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<T, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<T, bool>> pred, CancellationToken _) => store.Where(pred.Compile()).ToList());
    }

    // ---------- export ----------

    [Fact]
    public async Task Export_Returns_Profile_Orders_Carts_And_Token_Metadata_Without_Secrets()
    {
        var user = StoredUser();
        SetupUserRepo(user);
        SetupFind(_orders, new List<Order>
        {
            new() { UserId = user.Id, TotalAmount = 42.5m, ShippingAddress = new Address { Street = "Main 1", City = "Madrid", Country = "ES" } }
        });
        SetupFind(_carts, new List<Cart> { new() { UserId = user.Id } });

        var export = await Build().GetExportAsync(user.Id);

        export.Profile.Email.Should().Be("User@Test.com");
        export.Profile.Id.Should().Be(user.Id);
        export.Orders.Should().HaveCount(1);
        export.Carts.Should().HaveCount(1);
        export.TokenMetadata.Should().HaveCount(2);
        export.TokenMetadata.Single(m => m.CreatedByIp == "2.2.2.2").HasReplacement.Should().BeTrue();
        export.TokenMetadata.Single(m => m.CreatedByIp == "1.1.1.1").IsActive.Should().BeTrue();

        // No secret may leak: neither the token values nor the password hash.
        var json = JsonSerializer.Serialize(export);
        json.Should().NotContain("secret-rt-1");
        json.Should().NotContain("secret-rt-2");
        json.Should().NotContain("secret-rt-3");
        json.Should().NotContain("PasswordHash");
    }

    [Fact]
    public async Task Export_Unknown_User_Throws_NotFound()
    {
        SetupUserRepo(null);

        var act = () => Build().GetExportAsync("missing");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ---------- purge ----------

    [Fact]
    public async Task Purge_HardDeletes_User_Deletes_Carts_Pseudonymizes_Orders_And_Clears_Cache()
    {
        var user = StoredUser();
        SetupUserRepo(user);

        var orderStore = new List<Order>
        {
            new() { UserId = user.Id, TotalAmount = 10m, Status = OrderStatus.Delivered,
                ShippingAddress = new Address { Street = "Main 1", City = "Madrid", State = "M", ZipCode = "28001", Country = "ES" } },
            new() { UserId = user.Id, TotalAmount = 20m, Status = OrderStatus.Pending,
                ShippingAddress = new Address { Street = "Other 2", City = "Barcelona", Country = "ES" } }
        };
        SetupFind(_orders, orderStore);
        var cartStore = new List<Cart> { new() { UserId = user.Id } };
        SetupFind(_carts, cartStore);

        var updatedOrders = new List<Order>();
        _orders.Setup(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<Order>()))
            .Callback((string id, Order o) => updatedOrders.Add(o))
            .Returns(Task.CompletedTask);
        var hardDeletedCarts = new List<string>();
        _carts.Setup(r => r.HardDeleteAsync(It.IsAny<string>()))
            .Callback((string id) => hardDeletedCarts.Add(id))
            .Returns(Task.CompletedTask);
        string? hardDeletedUser = null;
        _users.Setup(r => r.HardDeleteAsync(It.IsAny<string>()))
            .Callback((string id) => hardDeletedUser = id)
            .Returns(Task.CompletedTask);

        await _cache.SetStringAsync("otp_user@test.com", "123456");
        await _cache.SetStringAsync("otp_attempts_user@test.com", "2");
        await _cache.SetStringAsync($"emailconfirm_{user.Id.ToLowerInvariant()}", "tok");
        await _cache.SetStringAsync("pwdreset_user@test.com", "tok");

        var result = await Build().PurgeAsync(user.Id, new PurgeRequestDto { ConfirmPassword = KnownPassword });

        result.UserDeleted.Should().BeTrue();
        result.CartsDeleted.Should().Be(1);
        result.OrdersPseudonymized.Should().Be(2);

        // Orders keep financials, lose identity.
        updatedOrders.Should().HaveCount(2);
        foreach (var o in updatedOrders)
        {
            o.UserId.Should().StartWith("deleted:");
            o.UserId.Should().NotContain(user.Id);
            o.ShippingAddress.Should().BeEquivalentTo(new Address());
            o.TotalAmount.Should().BePositive();
        }
        // Deterministic pseudonym: both orders share the same ref.
        updatedOrders.Select(o => o.UserId).Distinct().Should().ContainSingle();

        hardDeletedCarts.Should().ContainSingle().Which.Should().Be(cartStore[0].Id);
        hardDeletedUser.Should().Be(user.Id);
        _users.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never,
            "purge must hard-delete, never soft-delete");

        (await _cache.GetStringAsync("otp_user@test.com")).Should().BeNull();
        (await _cache.GetStringAsync("otp_attempts_user@test.com")).Should().BeNull();
        (await _cache.GetStringAsync($"emailconfirm_{user.Id.ToLowerInvariant()}")).Should().BeNull();
        (await _cache.GetStringAsync("pwdreset_user@test.com")).Should().BeNull();
    }

    [Fact]
    public void Pseudonym_Is_Deterministic_OneWay_And_Prefixed()
    {
        var a = UserService.PseudonymizeUserId("user-1");
        var b = UserService.PseudonymizeUserId("user-1");
        var c = UserService.PseudonymizeUserId("user-2");

        a.Should().Be(b);
        a.Should().NotBe(c);
        a.Should().StartWith("deleted:");
        a.Should().NotContain("user-1");
        a.Length.Should().Be("deleted:".Length + 64, "sha256 hex is 64 chars");
    }

    [Fact]
    public async Task Purge_Wrong_Password_Throws_Unauthorized_And_Deletes_Nothing()
    {
        var user = StoredUser();
        SetupUserRepo(user);

        var act = () => Build().PurgeAsync(user.Id, new PurgeRequestDto { ConfirmPassword = "WrongPass1" });

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _users.Verify(r => r.HardDeleteAsync(It.IsAny<string>()), Times.Never);
        _orders.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<Order>()), Times.Never);
        _carts.Verify(r => r.HardDeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Purge_Unknown_User_Throws_NotFound()
    {
        SetupUserRepo(null);

        var act = () => Build().PurgeAsync("missing", new PurgeRequestDto { ConfirmPassword = KnownPassword });

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}

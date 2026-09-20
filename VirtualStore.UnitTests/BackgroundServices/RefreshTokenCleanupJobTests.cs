using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Quartz;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;
using VirtualStore.Infrastructure.BackgroundServices;
using Xunit;

namespace VirtualStore.UnitTests.BackgroundServices;

/// <summary>
/// ADR-0010: cleanup purges (expired AND unrevoked) OR (expired AND revoked
/// beyond the 90-day forensics window); revoked-within-90d and active tokens
/// are kept. One bad doc never aborts the run.
/// </summary>
public class RefreshTokenCleanupJobTests
{
    private readonly Mock<IRepository<User>> _users = new();
    private readonly Mock<ILogger<RefreshTokenCleanupJob>> _logger = new();
    private readonly Mock<IJobExecutionContext> _context = new();

    public RefreshTokenCleanupJobTests()
    {
        _context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
    }

    private static RefreshToken Token(string id, DateTime expires, DateTime? revoked = null) =>
        new()
        {
            Token = id,
            Created = expires.AddDays(-7),
            Expires = expires,
            CreatedByIp = "ip",
            Revoked = revoked,
            RevokedByIp = revoked == null ? null : "ip"
        };

    private void SetupPages(params List<User>[] pages)
    {
        _users.Setup(r => r.PagedAsync(
                It.IsAny<Expression<Func<User, bool>>>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Expression<Func<User, bool>> _, int page, int __, string? ___, bool ____, CancellationToken _____) =>
                page >= 1 && page <= pages.Length
                    ? ((IReadOnlyList<User>)pages[page - 1], (long)pages[page - 1].Count)
                    : ((IReadOnlyList<User>)new List<User>(), 0L));
    }

    [Fact]
    public void PurgeExpiredTokens_Applies_Forensics_Window()
    {
        var now = DateTime.UtcNow;
        var cutoff = now - RefreshTokenCleanupJob.RevokedForensicsRetention;
        var user = new User
        {
            Id = "u1",
            RefreshTokens = new List<RefreshToken>
            {
                Token("purge-expired-unrevoked", now.AddDays(-1)),
                Token("purge-revoked-old", now.AddDays(-100), now.AddDays(-95)),
                Token("keep-revoked-recent", now.AddDays(-10), now.AddDays(-5)),
                Token("keep-active", now.AddDays(5)),
                Token("keep-revoked-unexpired", now.AddDays(5), now.AddDays(-100))
            }
        };

        var removed = RefreshTokenCleanupJob.PurgeExpiredTokens(user, now, cutoff);

        removed.Should().Be(2);
        user.RefreshTokens.Select(rt => rt.Token).Should().BeEquivalentTo(
            "keep-revoked-recent", "keep-active", "keep-revoked-unexpired");
    }

    [Fact]
    public async Task Execute_Purges_Dirty_Users_And_Skips_Clean_Ones()
    {
        var now = DateTime.UtcNow;
        var dirty = new User
        {
            Id = "dirty",
            RefreshTokens = new List<RefreshToken>
            {
                Token("old-unrevoked", now.AddDays(-8)),
                Token("forensics", now.AddDays(-10), now.AddDays(-9))
            }
        };
        var clean = new User
        {
            Id = "clean",
            RefreshTokens = new List<RefreshToken> { Token("active", now.AddDays(5)) }
        };
        SetupPages(new List<User> { dirty, clean });

        var updated = new List<string>();
        _users.Setup(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback((string id, User _, CancellationToken __) => updated.Add(id))
            .Returns(Task.CompletedTask);

        await new RefreshTokenCleanupJob(_users.Object, _logger.Object).Execute(_context.Object);

        updated.Should().ContainSingle().Which.Should().Be("dirty");
        dirty.RefreshTokens.Should().ContainSingle(rt => rt.Token == "forensics");
        clean.RefreshTokens.Should().ContainSingle();
    }

    [Fact]
    public async Task Execute_One_Bad_Doc_Does_Not_Abort_Run()
    {
        var now = DateTime.UtcNow;
        var bad = new User { Id = "bad", RefreshTokens = new List<RefreshToken> { Token("t1", now.AddDays(-2)) } };
        var good = new User { Id = "good", RefreshTokens = new List<RefreshToken> { Token("t2", now.AddDays(-2)) } };
        SetupPages(new List<User> { bad, good });

        var updated = new List<string>();
        _users.Setup(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback((string id, User _, CancellationToken __) => updated.Add(id))
            .Returns((string id, User _, CancellationToken __) =>
                id == "bad" ? throw new InvalidOperationException("boom") : Task.CompletedTask);

        await new RefreshTokenCleanupJob(_users.Object, _logger.Object).Execute(_context.Object);

        updated.Should().BeEquivalentTo("bad", "good");
        good.RefreshTokens.Should().BeEmpty("good user's token must still be purged after bad failed");
    }
}

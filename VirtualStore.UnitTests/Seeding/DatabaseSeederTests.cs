using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using VirtualStore.API.Data;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;
using Xunit;

namespace VirtualStore.UnitTests.Seeding;

/// <summary>
/// Wave T1c remainder suite for <see cref="DatabaseSeeder"/>.
/// Covers the configured-vs-default truth table, the existing-admin early
/// return, the existing-non-admin Add path, and the Warning vs Information
/// log arms. No I/O: repository, logger and configuration are all faked.
/// </summary>
public class DatabaseSeederTests
{
    private static Mock<IConfiguration> Config(string? email, string? password, string? username)
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["DatabaseSeeder:AdminEmail"]).Returns(email);
        config.Setup(c => c["DatabaseSeeder:AdminPassword"]).Returns(password);
        config.Setup(c => c["DatabaseSeeder:AdminUsername"]).Returns(username);
        return config;
    }

    private static void VerifyLog(Mock<ILogger<DatabaseSeeder>> logger, LogLevel level, Times times)
    {
        logger.Verify(l => l.Log(
            level,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => true),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);
    }

    [Fact]
    public async Task SeedAsync_AllConfigured_CreatesAdmin_LogsInformation()
    {
        User? added = null;
        var repo = new Mock<IRepository<User>>();
        repo.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()))
            .ReturnsAsync((User?)null);
        repo.Setup(r => r.AddAsync(It.IsAny<User>()))
            .Callback<User>(u => added = u)
            .Returns(Task.CompletedTask);
        var logger = new Mock<ILogger<DatabaseSeeder>>();

        await new DatabaseSeeder(
            repo.Object, logger.Object, Config("ops@acme.com", "OpsSecret123!", "opsadmin").Object)
            .SeedAsync();

        repo.Verify(r => r.AddAsync(It.IsAny<User>()), Times.Once());
        added.Should().NotBeNull();
        added!.Email.Should().Be("ops@acme.com");
        added.Username.Should().Be("opsadmin");
        BCrypt.Net.BCrypt.Verify("OpsSecret123!", added.PasswordHash).Should().BeTrue();
        added.EmailConfirmed.Should().BeTrue();
        added.Roles.Should().BeEquivalentTo([UserRole.Admin, UserRole.Manager, UserRole.Customer]);

        VerifyLog(logger, LogLevel.Information, Times.Once());
        VerifyLog(logger, LogLevel.Warning, Times.Never());
    }

    [Theory]
    // Any single missing value (null or whitespace) flips to defaults + Warning.
    [InlineData(null, null, null, "admin@virtualstore.com", "admin")]
    [InlineData(null, "OpsSecret123!", "opsadmin", "admin@virtualstore.com", "opsadmin")]
    [InlineData("ops@acme.com", null, "opsadmin", "ops@acme.com", "opsadmin")]
    [InlineData("ops@acme.com", "OpsSecret123!", null, "ops@acme.com", "admin")]
    [InlineData("  ", "OpsSecret123!", "opsadmin", "admin@virtualstore.com", "opsadmin")]
    [InlineData("ops@acme.com", "", "opsadmin", "ops@acme.com", "opsadmin")]
    public async Task SeedAsync_AnyMissingDefault_UsesDefaults_LogsWarning(
        string? email, string? password, string? username,
        string expectedEmail, string expectedUsername)
    {
        User? added = null;
        var repo = new Mock<IRepository<User>>();
        repo.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()))
            .ReturnsAsync((User?)null);
        repo.Setup(r => r.AddAsync(It.IsAny<User>()))
            .Callback<User>(u => added = u)
            .Returns(Task.CompletedTask);
        var logger = new Mock<ILogger<DatabaseSeeder>>();

        await new DatabaseSeeder(repo.Object, logger.Object, Config(email, password, username).Object)
            .SeedAsync();

        added.Should().NotBeNull();
        added!.Email.Should().Be(expectedEmail);
        added.Username.Should().Be(expectedUsername);
        // The password used must always verify, whether configured or default.
        var expectedPassword = string.IsNullOrWhiteSpace(password) ? "Admin123!" : password;
        BCrypt.Net.BCrypt.Verify(expectedPassword, added.PasswordHash).Should().BeTrue();

        VerifyLog(logger, LogLevel.Warning, Times.Once());
        VerifyLog(logger, LogLevel.Information, Times.Never());
    }

    [Fact]
    public async Task SeedAsync_ExistingAdmin_ReturnsEarly_NoAdd_NoLog()
    {
        var existing = new User
        {
            Email = "admin@virtualstore.com",
            Username = "admin",
            Roles = new List<UserRole> { UserRole.Admin }
        };
        var repo = new Mock<IRepository<User>>();
        repo.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()))
            .ReturnsAsync(existing);
        var logger = new Mock<ILogger<DatabaseSeeder>>();

        await new DatabaseSeeder(repo.Object, logger.Object, Config(null, null, null).Object)
            .SeedAsync();

        repo.Verify(r => r.AddAsync(It.IsAny<User>()), Times.Never);
        repo.Verify(r => r.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyLog(logger, LogLevel.Warning, Times.Never());
        VerifyLog(logger, LogLevel.Information, Times.Never());
    }

    [Fact]
    public async Task SeedAsync_ExistingNonAdmin_TakesAddPath_LogsWarning()
    {
        // Same email on file but without the Admin role: the seeder does not
        // treat it as an admin and proceeds to create one.
        var existing = new User
        {
            Email = "admin@virtualstore.com",
            Username = "someone",
            Roles = new List<UserRole> { UserRole.Customer }
        };
        User? added = null;
        var repo = new Mock<IRepository<User>>();
        repo.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()))
            .ReturnsAsync(existing);
        repo.Setup(r => r.AddAsync(It.IsAny<User>()))
            .Callback<User>(u => added = u)
            .Returns(Task.CompletedTask);
        var logger = new Mock<ILogger<DatabaseSeeder>>();

        await new DatabaseSeeder(repo.Object, logger.Object, Config(null, null, null).Object)
            .SeedAsync();

        repo.Verify(r => r.AddAsync(It.IsAny<User>()), Times.Once());
        added.Should().NotBeNull();
        added!.Roles.Should().Contain(UserRole.Admin);
        VerifyLog(logger, LogLevel.Warning, Times.Once());
    }
}

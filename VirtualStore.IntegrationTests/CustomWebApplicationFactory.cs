using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// API factory wired to the Testcontainers MongoDB instance.
/// Host is built lazily on first CreateClient(), which happens after
/// <see cref="MongoDbFixture.InitializeAsync"/>, so the container
/// connection string is already known at configuration time.
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public MongoDbFixture Mongo { get; } = new();

    public Task InitializeAsync() => Mongo.InitializeAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await Mongo.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var mongoConnection = Mongo.ConnectionString ?? "mongodb://localhost:27017";
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDbSettings:ConnectionString"] = mongoConnection,
                ["MongoDbSettings:DatabaseName"] = "VirtualStoreIntegrationTests",
                ["JwtSettings:Secret"] = "integration-test-secret-that-is-long-enough-for-hmac-256!!!",
                ["JwtSettings:Issuer"] = "TestIssuer",
                ["JwtSettings:Audience"] = "TestAudience",
                ["JwtSettings:AccessTokenExpirationMinutes"] = "15",
                ["JwtSettings:RefreshTokenExpirationDays"] = "7",
                ["CorsSettings:AllowedOrigins:0"] = "http://localhost:3000",
                ["DatabaseSeeder:AdminEmail"] = "admin@integration.test",
                ["DatabaseSeeder:AdminUsername"] = "admin",
                ["DatabaseSeeder:AdminPassword"] = "Admin123!"
            });
        });

        builder.UseEnvironment("Testing");
    }
}

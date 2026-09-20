using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// ADR-0010 GDPR flow: export (no secrets) → purge rejects a wrong password →
/// purge with the current password erases the user → export/login fail after.
/// Docker-gated; skipped without a daemon.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MePrivacyIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string AdminEmail = "admin@integration.test";
    private const string AdminPassword = "Admin123!";
    private readonly CustomWebApplicationFactory _factory;

    public MePrivacyIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [RequiresDockerFact]
    public async Task Me_Export_Then_Purge_Flow()
    {
        var client = _factory.CreateClient();

        // Login as the seeded admin.
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = AdminEmail,
            password = AdminPassword
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var loginDoc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var accessToken = loginDoc.RootElement.GetProperty("accessToken").GetString();
        var refreshToken = loginDoc.RootElement.GetProperty("refreshToken").GetString();
        accessToken.Should().NotBeNullOrEmpty();
        refreshToken.Should().NotBeNullOrEmpty();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Export carries profile + collections and no token secrets.
        var export = await client.GetAsync("/api/me/export");
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportJson = await export.Content.ReadAsStringAsync();
        using var exportDoc = JsonDocument.Parse(exportJson);
        exportDoc.RootElement.GetProperty("profile").GetProperty("email").GetString()
            .Should().Be(AdminEmail);
        exportDoc.RootElement.TryGetProperty("orders", out _).Should().BeTrue();
        exportDoc.RootElement.TryGetProperty("carts", out _).Should().BeTrue();
        exportDoc.RootElement.TryGetProperty("tokenMetadata", out _).Should().BeTrue();
        exportJson.Should().NotContain(refreshToken!);

        // Wrong confirmation password → 401, user intact.
        var badPurge = await client.PostAsJsonAsync("/api/me/purge", new { confirmPassword = "WrongPass1" });
        badPurge.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/me/export")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Correct password → 200 with summary counts.
        var purge = await client.PostAsJsonAsync("/api/me/purge", new { confirmPassword = AdminPassword });
        purge.StatusCode.Should().Be(HttpStatusCode.OK);
        using var purgeDoc = JsonDocument.Parse(await purge.Content.ReadAsStringAsync());
        purgeDoc.RootElement.GetProperty("userDeleted").GetBoolean().Should().BeTrue();

        // After erasure the (still-valid JWT) export 404s and login 401s.
        (await client.GetAsync("/api/me/export")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var relogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = AdminEmail,
            password = AdminPassword
        });
        relogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

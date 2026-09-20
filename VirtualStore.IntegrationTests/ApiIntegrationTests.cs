using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace VirtualStore.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ApiIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApiIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [RequiresDockerFact]
    public async Task Get_Health_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [RequiresDockerFact]
    public async Task Get_Products_Empty_Ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/products");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("items", out _).Should().BeTrue("GET /api/products must return a PagedResult with 'items'");
        doc.RootElement.TryGetProperty("totalCount", out _).Should().BeTrue("GET /api/products must return a PagedResult with 'totalCount'");
    }

    [RequiresDockerFact]
    public async Task Post_Login_Invalid_Creds_Returns_401_ProblemDetails()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "nobody@test.com",
            password = "WrongPass123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.TryGetProperty("title", out var title).Should().BeTrue("401 must follow ProblemDetails shape");
        title.GetString().Should().Be("Unauthorized");
        root.TryGetProperty("status", out var status).Should().BeTrue("401 must follow ProblemDetails shape");
        status.GetInt32().Should().Be(401);
        root.TryGetProperty("traceId", out _).Should().BeTrue("ProblemDetails must carry 'traceId'");
    }
}

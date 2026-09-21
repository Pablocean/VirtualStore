using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Services;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// HTTP contract suite against <see cref="CustomWebApplicationFactory"/> (needs the
/// Mongo fixture for boot): authorize-attribute matrix, auth rate limiting (429 +
/// Retry-After), the raw-JSON-number cart body, ProblemDetails traceId presence,
/// and the 200-even-on-webhook-failure contract.
/// Test JWTs are minted with the real <see cref="TokenService"/> and the same
/// JwtSettings the factory boots with.
/// Docker-gated; skipped without a daemon (see <see cref="RequiresDockerFactAttribute"/>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ApiContractTests : IClassFixture<CustomWebApplicationFactory>
{
    // Must mirror CustomWebApplicationFactory.ConfigureWebHost.
    private const string TestJwtSecret = "integration-test-secret-that-is-long-enough-for-hmac-256!!!";
    private const string TestIssuer = "TestIssuer";
    private const string TestAudience = "TestAudience";

    private readonly CustomWebApplicationFactory _factory;

    public ApiContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [RequiresDockerFact]
    public async Task Anonymous_Get_Products_Returns_200_With_Paged_Shape()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/products");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("items", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("totalCount", out _).Should().BeTrue();
    }

    [RequiresDockerFact]
    public async Task Users_Endpoint_Authorize_Matrix()
    {
        var client = _factory.CreateClient();

        var anonymous = await client.GetAsync("/api/users");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var customerClient = AuthorizedClient(UserRole.Customer);
        var forbidden = await customerClient.GetAsync("/api/users");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var adminClient = AuthorizedClient(UserRole.Admin);
        var allowed = await adminClient.GetAsync("/api/users");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK,
            "the admin control proves the minted tokens are valid (no false-positive 401/403)");
    }

    [RequiresDockerFact]
    public async Task Auth_Rate_Limit_Returns_429_With_RetryAfter()
    {
        var client = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        // "auth" policy: 5 req/min per IP. Six rapid logins -> 5x401 then 429.
        for (var i = 0; i < 6; i++)
        {
            using var attempt = await client.PostAsJsonAsync("/api/auth/login", new
            {
                email = "nobody@test.com",
                password = "WrongPass123",
            });
            statuses.Add(attempt.StatusCode);
        }

        statuses.Take(5).Should().OnlyContain(s => s == HttpStatusCode.Unauthorized);
        statuses.Should().Contain(HttpStatusCode.TooManyRequests);

        // Re-hit once more to capture headers deterministically (budget exhausted).
        using var limited = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "nobody@test.com",
            password = "WrongPass123",
        });
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.Contains("Retry-After").Should().BeTrue();
    }

    [RequiresDockerFact]
    public async Task Cart_UpdateItem_Raw_Int_Body_Accepted_String_Body_Rejected()
    {
        using var client = AuthorizedClient(UserRole.Customer);
        var productId = ObjectId.GenerateNewId().ToString();

        using var numberBody = new StringContent("3", Encoding.UTF8, "application/json");
        var fromNumber = await client.PutAsync($"/api/cart/items/{productId}", numberBody);
        fromNumber.StatusCode.Should().NotBe(
            HttpStatusCode.BadRequest, "a raw JSON number must bind to [FromBody] int quantity");

        using var stringBody = new StringContent("\"three\"", Encoding.UTF8, "application/json");
        var fromString = await client.PutAsync($"/api/cart/items/{productId}", stringBody);
        fromString.StatusCode.Should().Be(
            HttpStatusCode.BadRequest, "a JSON string must not bind to [FromBody] int quantity");
    }

    [RequiresDockerFact]
    public async Task ProblemDetails_Contains_TraceId_On_404_And_409()
    {
        using var client = AuthorizedClient(UserRole.Admin);

        using var notFound = await client.DeleteAsync($"/api/products/{ObjectId.GenerateNewId()}");
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var notFoundDoc = JsonDocument.Parse(await notFound.Content.ReadAsStringAsync());
        AssertProblemShape(notFoundDoc.RootElement, 404, "Not Found");

        var email = $"contract-{Guid.NewGuid():N}@example.com";
        using var created = await client.PostAsJsonAsync("/api/users", new
        {
            email,
            username = "contractuser",
            password = "TestPass123",
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        using var conflict = await client.PostAsJsonAsync("/api/users", new
        {
            email,
            username = "contractuser2",
            password = "TestPass123",
        });
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var conflictDoc = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        AssertProblemShape(conflictDoc.RootElement, 409, "Conflict");
    }

    [RequiresDockerFact]
    public async Task Webhook_Garbage_Signature_Is_400_Unknown_Order_Is_200()
    {
        var client = _factory.CreateClient();
        string secret;
        using (var scope = _factory.Services.CreateScope())
            secret = scope.ServiceProvider.GetRequiredService<IOptions<StripeSettings>>().Value.WebhookSecret;

        var unknownOrderId = ObjectId.GenerateNewId().ToString();
        var json = BuildSucceededEventJson(
            $"evt_{Guid.NewGuid():N}", $"pi_{Guid.NewGuid():N}", unknownOrderId);

        using var garbage = new StringContent(json, Encoding.UTF8, "application/json");
        garbage.Headers.Add("Stripe-Signature", "t=123,v1=deadbeef");
        var badSig = await client.PostAsync("/api/stripe/webhook", garbage);
        badSig.StatusCode.Should().Be(
            HttpStatusCode.BadRequest, "an invalid signature must be rejected with 400");

        using var valid = new StringContent(json, Encoding.UTF8, "application/json");
        valid.Headers.Add("Stripe-Signature", Sign(json, secret));
        var unknownOrder = await client.PostAsync("/api/stripe/webhook", valid);
        unknownOrder.StatusCode.Should().Be(HttpStatusCode.OK,
            "webhooks must ack 200 even when the order transition cannot be applied");
        using var doc = JsonDocument.Parse(await unknownOrder.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("succeeded").GetBoolean().Should().BeTrue();
    }

    private static void AssertProblemShape(JsonElement root, int status, string title)
    {
        root.GetProperty("status").GetInt32().Should().Be(status);
        root.GetProperty("title").GetString().Should().Be(title);
        root.TryGetProperty("traceId", out var traceId).Should().BeTrue("ProblemDetails must carry 'traceId'");
        traceId.GetString().Should().NotBeNullOrEmpty();
    }

    private HttpClient AuthorizedClient(UserRole role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken(role));
        return client;
    }

    private static string MintToken(UserRole role)
    {
        var tokens = new TokenService(Options.Create(new JwtSettings
        {
            Secret = TestJwtSecret,
            Issuer = TestIssuer,
            Audience = TestAudience,
            AccessTokenExpirationMinutes = 15,
            RefreshTokenExpirationDays = 7,
        }));
        return tokens.GenerateAccessToken(new User
        {
            Email = $"{role.ToString().ToLowerInvariant()}@contract.test",
            Username = role.ToString(),
            Roles = new List<UserRole> { role },
        });
    }

    private static string BuildSucceededEventJson(string eventId, string paymentIntentId, string orderId) =>
        "{\"id\":\"" + eventId + "\",\"object\":\"event\",\"api_version\":\"2024-12-18.acacia\",\"created\":1680000000,"
        + "\"data\":{\"object\":{\"id\":\"" + paymentIntentId + "\",\"object\":\"payment_intent\",\"amount\":2000,"
        + "\"currency\":\"usd\",\"status\":\"succeeded\",\"client_secret\":\"" + paymentIntentId + "_secret\","
        + "\"metadata\":{\"orderId\":\"" + orderId + "\"}}},"
        + "\"livemode\":false,\"pending_webhooks\":1,"
        + "\"request\":{\"id\":null,\"idempotency_key\":null},\"type\":\"payment_intent.succeeded\"}";

    private static string Sign(string payload, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signed = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
        return $"t={timestamp},v1={sig}";
    }
}

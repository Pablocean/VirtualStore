using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Stripe;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Stripe;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// <see cref="StripePaymentService"/> against a real <c>stripe-mock</c> container
/// (<c>stripe/stripe-mock:latest</c>, HTTP on 12111). The service is built with a
/// test-specific <see cref="IStripeClientFactory"/> that points real Stripe SDK
/// clients at the mock over plain HTTP and records the <c>Idempotency-Key</c>
/// header of every request on the wire (stripe-mock tolerates any key).
/// Docker-gated; skipped without a daemon (see <see cref="RequiresDockerFactAttribute"/>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class StripeIntegrationTests : IClassFixture<StripeMockFixture>
{
    private readonly StripeMockFixture _stripeMock;

    public StripeIntegrationTests(StripeMockFixture stripeMock)
    {
        _stripeMock = stripeMock;
    }

    private StripePaymentService CreateService(StripeMockFactory? factory = null) =>
        new(
            Options.Create(new StripeSettings
            {
                SecretKey = "sk_test_stripe_mock",
                PublishableKey = "pk_test_stripe_mock",
                WebhookSecret = "whsec_test",
            }),
            stripeFactory: factory ?? _stripeMock.CreateFactory());

    [RequiresDockerFact]
    public async Task CreatePaymentIntent_Hits_Mock_And_Sends_Idempotency_Key()
    {
        var factory = _stripeMock.CreateFactory();
        var service = CreateService(factory);
        var key = $"pi-key-{Guid.NewGuid():N}";

        var result = await service.CreatePaymentIntentAsync(25m, "usd", idempotencyKey: key);

        result.PaymentIntentId.Should().StartWith("pi_");
        result.Amount.Should().Be(25m);
        result.Currency.Should().Be("usd");
        factory.IdempotencyKeysSeen.Should().Contain(key,
            "the RequestOptions idempotency key must reach stripe-mock on the wire");
    }

    [RequiresDockerFact]
    public async Task CreatePaymentIntent_Without_Key_Sends_No_Idempotency_Header()
    {
        var factory = _stripeMock.CreateFactory();
        var service = CreateService(factory);

        var result = await service.CreatePaymentIntentAsync(10m, "usd");

        result.PaymentIntentId.Should().StartWith("pi_");
        factory.RequestsSeen.Should().BeGreaterThan(0);
        factory.IdempotencyKeysSeen.Should().BeEmpty(
            "ToRequestOptions(null) must not emit an Idempotency-Key header");
    }

    [RequiresDockerFact]
    public async Task ConfirmPayment_Reflects_Mock_Status()
    {
        var factory = _stripeMock.CreateFactory();
        var service = CreateService(factory);

        var created = await service.CreatePaymentIntentAsync(25m, "usd");
        var fetched = await new PaymentIntentService(factory.Client).GetAsync(created.PaymentIntentId);
        var confirmed = await service.ConfirmPaymentAsync(created.PaymentIntentId);

        confirmed.Should().Be(fetched.Status == "succeeded");
    }

    [RequiresDockerFact]
    public async Task RefundPayment_Hits_Mock_With_Idempotency_Keys()
    {
        var factory = _stripeMock.CreateFactory();
        var service = CreateService(factory);
        var created = await service.CreatePaymentIntentAsync(25m, "usd");
        factory.Recording.Clear();
        var fullKey = $"refund-full-{Guid.NewGuid():N}";
        var partialKey = $"refund-partial-{Guid.NewGuid():N}";

        var full = await service.RefundPaymentAsync(created.PaymentIntentId, idempotencyKey: fullKey);
        var partial = await service.RefundPaymentAsync(created.PaymentIntentId, 5m, partialKey);

        full.RefundId.Should().StartWith("re_");
        partial.RefundId.Should().StartWith("re_");
        partial.Amount.Should().Be(5m);
        factory.IdempotencyKeysSeen.Should().Contain(fullKey);
        factory.IdempotencyKeysSeen.Should().Contain(partialKey);
    }

    [RequiresDockerFact]
    public async Task Transient_500_Retries_Twice_Then_Throws_StripeException()
    {
        // Loopback stub (no Docker transport needed): every call fails with a
        // transient 500, which the service pipeline must retry twice before throwing.
        const string errorJson = "{\"error\":{\"message\":\"stub boom\",\"type\":\"api_error\"}}";
        using var stub = new LoopbackStripeStub(_ => (500, errorJson));
        var factory = new StripeMockFactory(stub.Url, maxNetworkRetries: 0);
        var service = CreateService(factory);

        var ex = await Assert.ThrowsAsync<StripeException>(
            () => service.CreatePaymentIntentAsync(25m, "usd", idempotencyKey: "retry-proof"));

        ex.HttpStatusCode.Should().Be(HttpStatusCode.InternalServerError);
        stub.Hits.Should().Be(3, "1 initial attempt + 2 Polly retries");
        factory.IdempotencyKeysSeen.Should().HaveCount(3)
            .And.OnlyContain(k => k == "retry-proof");
    }

    [RequiresDockerFact]
    public async Task Transient_500_Then_Recovers()
    {
        // Two transient 500s followed by a valid PaymentIntent: the retry pipeline
        // must recover and return the parsed result.
        const string errorJson = "{\"error\":{\"message\":\"stub boom\",\"type\":\"api_error\"}}";
        const string piJson = "{\"id\":\"pi_stub123\",\"object\":\"payment_intent\",\"amount\":2500,"
            + "\"currency\":\"usd\",\"client_secret\":\"pi_stub123_secret\",\"status\":\"requires_payment_method\"}";
        using var stub = new LoopbackStripeStub(hit => hit <= 2 ? (500, errorJson) : (200, piJson));
        var factory = new StripeMockFactory(stub.Url, maxNetworkRetries: 0);
        var service = CreateService(factory);

        var result = await service.CreatePaymentIntentAsync(25m, "usd", idempotencyKey: "recover");

        result.PaymentIntentId.Should().Be("pi_stub123");
        result.Amount.Should().Be(25m);
        stub.Hits.Should().Be(3);
    }
}

/// <summary>
/// stripe-mock container fixture (HTTP 12111 + HTTPS 12112).
/// </summary>
public sealed class StripeMockFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ApiBase =>
        $"http://{(_container?.Hostname ?? "localhost")}:{(_container?.GetMappedPublicPort(12111) ?? 12111)}";

    public StripeMockFactory CreateFactory() => new(ApiBase);

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage("stripe/stripe-mock:latest")
                .WithPortBinding(12111, true)
                .WithPortBinding(12112, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(12111))
                .Build();
            await _container.StartAsync();
        }
        catch (Exception)
        {
            // Docker unavailable: callers are RequiresDockerFact-gated and skip.
            if (_container is not null)
            {
                try { await _container.DisposeAsync(); } catch { /* ignore */ }
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }
}

/// <summary>
/// Test-only <see cref="IStripeClientFactory"/>: real Stripe SDK services pointed
/// at stripe-mock, with a recording <see cref="IHttpClient"/> that captures every
/// <c>Idempotency-Key</c> header sent on the wire. Signature validation delegates
/// to the real <see cref="EventUtility.ConstructEvent"/>.
/// </summary>
public sealed class StripeMockFactory : IStripeClientFactory
{
    private readonly string _apiBase;
    private readonly int? _maxNetworkRetries;

    public RecordingStripeHttpClient Recording { get; }

    public StripeClient Client => new("sk_test_stripe_mock", httpClient: Recording, apiBase: _apiBase);

    public IReadOnlyCollection<string> IdempotencyKeysSeen => Recording.IdempotencyKeys.ToArray();

    public int RequestsSeen => Recording.Requests;

    public StripeMockFactory(string apiBase, int? maxNetworkRetries = null)
    {
        _apiBase = apiBase;
        _maxNetworkRetries = maxNetworkRetries;
        Recording = new RecordingStripeHttpClient(maxNetworkRetries);
    }

    public PaymentIntentService CreatePaymentIntentService(string secretKey) => new(Client);

    public RefundService CreateRefundService(string secretKey) => new(Client);

    public Event ConstructEvent(string json, string signatureHeader, string webhookSecret) =>
        EventUtility.ConstructEvent(json, signatureHeader, webhookSecret);
}

/// <summary>
/// <see cref="IHttpClient"/> decorator that records request headers, then delegates
/// to the default <see cref="SystemNetHttpClient"/> for transport.
/// </summary>
public sealed class RecordingStripeHttpClient : IHttpClient
{
    private readonly SystemNetHttpClient _inner;
    private int _requests;

    public ConcurrentBag<string> IdempotencyKeys { get; } = new();

    public int Requests => _requests;

    public RecordingStripeHttpClient(int? maxNetworkRetries = null)
    {
        _inner = maxNetworkRetries.HasValue
            ? new SystemNetHttpClient(httpClient: null, maxNetworkRetries: maxNetworkRetries.Value)
            : new SystemNetHttpClient();
    }

    public void Clear()
    {
        while (IdempotencyKeys.TryTake(out _)) { }
        Interlocked.Exchange(ref _requests, 0);
    }

    public async Task<StripeResponse> MakeRequestAsync(StripeRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        foreach (var header in request.StripeHeaders)
        {
            if (string.Equals(header.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                IdempotencyKeys.Add(header.Value);
        }
        return await _inner.MakeRequestAsync(request, cancellationToken);
    }

    public Task<StripeStreamedResponse> MakeStreamingRequestAsync(StripeRequest request, CancellationToken cancellationToken) =>
        _inner.MakeStreamingRequestAsync(request, cancellationToken);
}

/// <summary>
/// Minimal loopback HTTP stub for Stripe API calls (plain TCP, no Docker transport
/// needed): counts hits and answers each request with a canned status + JSON body.
/// </summary>
internal sealed class LoopbackStripeStub : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<int, (int Status, string Body)> _respond;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _hits;

    public int Hits => Volatile.Read(ref _hits);

    public string Url { get; }

    public LoopbackStripeStub(Func<int, (int Status, string Body)> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
        {
            var hit = Interlocked.Increment(ref _hits);

            // Request line + headers (Stripe posts form-encoded ASCII bodies).
            var contentLength = 0;
            var expectContinue = false;
            await reader.ReadLineAsync();
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0)
                    continue;
                var name = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, out var n))
                    contentLength = n;
                if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase) &&
                    value.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                    expectContinue = true;
            }
            if (expectContinue)
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));

            var remaining = contentLength;
            var buf = new char[4096];
            while (remaining > 0)
            {
                var read = await reader.ReadAsync(buf, 0, Math.Min(buf.Length, remaining));
                if (read <= 0)
                    break;
                remaining -= read;
            }

            var (status, body) = _respond(hit);
            var reason = status == 200 ? "OK" : "Internal Server Error";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var header =
                $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
            await stream.WriteAsync(bodyBytes);
            await stream.FlushAsync();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

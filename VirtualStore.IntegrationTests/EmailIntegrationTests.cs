using System.Net.Sockets;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using VirtualStore.Domain.Settings;
using VirtualStore.Infrastructure.Email;
using Xunit;

namespace VirtualStore.IntegrationTests;

/// <summary>
/// End-to-end email delivery against a real MailHog SMTP container
/// (<c>mailhog/mailhog:v1.0.1</c>: SMTP on 1025, HTTP API on 8025).
/// The REAL <see cref="EmailService"/> (default <see cref="SmtpClientFactory"/>)
/// sends OTP / confirmation / reset messages; assertions read them back through
/// the MailHog v2 API (<c>GET /api/v2/messages</c>).
/// The refused-connection arms prove the Polly pipeline retries before throwing,
/// and the SSL arm covers the <c>EnableSsl ? StartTls : Auto</c> branch.
/// Docker-gated; skipped without a daemon (see <see cref="RequiresDockerFactAttribute"/>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmailIntegrationTests : IClassFixture<MailHogFixture>
{
    private readonly MailHogFixture _mailhog;

    public EmailIntegrationTests(MailHogFixture mailhog)
    {
        _mailhog = mailhog;
    }

    private EmailService CreateService(bool enableSsl) =>
        new(Options.Create(new EmailSettings
        {
            SmtpServer = _mailhog.SmtpHost,
            Port = _mailhog.SmtpPort,
            SenderEmail = "noreply@virtualstore.test",
            SenderName = "Virtual Store",
            Username = string.Empty,
            Password = string.Empty,
            EnableSsl = enableSsl,
        }));

    [RequiresDockerFact]
    public async Task Send_Otp_Confirmation_Reset_Arrive_In_MailHog()
    {
        var service = CreateService(enableSsl: false);
        var run = Guid.NewGuid().ToString("N");
        var to = $"user-{run}@example.com";
        const string otp = "482916";
        var confirmToken = $"confirm-{run}";
        var resetToken = $"reset-{run}";

        await service.SendOtpEmailAsync(to, otp);
        await service.SendConfirmationEmailAsync(to, confirmToken);
        await service.SendPasswordResetEmailAsync(to, resetToken);

        var messages = await WaitForMessagesAsync(
            $"http://{_mailhog.SmtpHost}:{_mailhog.ApiPort}", expectedTotal: 3, timeout: TimeSpan.FromSeconds(30));

        messages.Should().HaveCount(3);
        messages.Should().ContainSingle(m => m.Subject == "Your Verification Code" && m.Body.Contains(otp));
        messages.Should().ContainSingle(m => m.Subject == "Confirm your email" && m.Body.Contains(confirmToken));
        messages.Should().ContainSingle(m => m.Subject == "Password reset request" && m.Body.Contains(resetToken));
    }

    [RequiresDockerFact]
    public async Task Send_To_Closed_Port_Retries_Then_Throws()
    {
        // 127.0.0.1:1 is (practically) always closed: ConnectAsync fails fast,
        // so the elapsed time is dominated by the Polly retry backoff.
        var service = new EmailService(Options.Create(new EmailSettings
        {
            SmtpServer = "127.0.0.1",
            Port = 1,
            SenderEmail = "noreply@virtualstore.test",
            SenderName = "Virtual Store",
            Username = string.Empty,
            Password = string.Empty,
            EnableSsl = false,
        }));

        var start = DateTime.UtcNow;
        var ex = await Assert.ThrowsAnyAsync<SocketException>(
            () => service.SendOtpEmailAsync("nobody@example.com", "123456"));
        var elapsed = DateTime.UtcNow - start;

        ex.Should().NotBeNull();
        elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500),
            "the Polly pipeline must back off and retry before surfacing the refusal");
    }

    [RequiresDockerFact]
    public async Task Send_To_Closed_Port_With_Ssl_Retries_Then_Throws()
    {
        // Same refused-connection arm with EnableSsl=true: covers the
        // SecureSocketOptions.StartTls branch of the connect call.
        var service = new EmailService(Options.Create(new EmailSettings
        {
            SmtpServer = "127.0.0.1",
            Port = 1,
            SenderEmail = "noreply@virtualstore.test",
            SenderName = "Virtual Store",
            Username = string.Empty,
            Password = string.Empty,
            EnableSsl = true,
        }));

        await Assert.ThrowsAnyAsync<SocketException>(
            () => service.SendConfirmationEmailAsync("nobody@example.com", "token"));
    }

    private static async Task<IReadOnlyList<(string? Subject, string Body)>> WaitForMessagesAsync(
        string mailhogBaseUrl, int expectedTotal, TimeSpan timeout)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var json = await http.GetStringAsync(mailhogBaseUrl + "/api/v2/messages");
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(m =>
                {
                    var content = m.GetProperty("Content");
                    string? subject = null;
                    if (content.GetProperty("Headers").TryGetProperty("Subject", out var subjects) &&
                        subjects.GetArrayLength() > 0)
                        subject = subjects[0].GetString();
                    var body = content.TryGetProperty("Body", out var bodyEl)
                        ? bodyEl.GetString() ?? string.Empty
                        : string.Empty;
                    return (subject, body);
                })
                .ToList();

            if (items.Count >= expectedTotal)
                return items;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"MailHog held {items.Count}/{expectedTotal} messages after {timeout.TotalSeconds}s.");
            await Task.Delay(500);
        }
    }
}

/// <summary>
/// MailHog container fixture (SMTP 1025 + HTTP API 8025).
/// Started once per test class; Docker-gated callers skip when no daemon runs.
/// </summary>
public sealed class MailHogFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string SmtpHost => _container?.Hostname ?? "localhost";

    public int SmtpPort => _container?.GetMappedPublicPort(1025) ?? 1025;

    public int ApiPort => _container?.GetMappedPublicPort(8025) ?? 8025;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage("mailhog/mailhog:v1.0.1")
                .WithPortBinding(1025, true)
                .WithPortBinding(8025, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(1025))
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

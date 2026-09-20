using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Polly;
using Polly.Retry;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Settings;

namespace VirtualStore.Infrastructure.Email;

public class EmailService : IEmailService
{
    private readonly EmailSettings _emailSettings;

    // Static pipeline (MailKit/Stripe SDK are used directly, no IHttpClient): retry 3x
    // exponential backoff + 10s per-attempt timeout.
    private static readonly ResiliencePipeline _emailPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1),
            UseJitter = true
        })
        .AddTimeout(TimeSpan.FromSeconds(10))
        .Build();
    
    public EmailService(IOptions<EmailSettings> options)
    {
        _emailSettings = options.Value;
    }
    
    public async Task SendEmailAsync(string to, string subject, string body)
    {
        await _emailPipeline.ExecuteAsync(async cancellationToken =>
        {
            var email = new MimeMessage();
            email.From.Add(new MailboxAddress(_emailSettings.SenderName, _emailSettings.SenderEmail));
            email.To.Add(MailboxAddress.Parse(to));
            email.Subject = subject;
            email.Body = new TextPart("html") { Text = body };

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_emailSettings.SmtpServer, _emailSettings.Port,
                _emailSettings.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto,
                cancellationToken);
            await smtp.AuthenticateAsync(_emailSettings.Username, _emailSettings.Password, cancellationToken);
            await smtp.SendAsync(email, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        });
    }
    
    public async Task SendOtpEmailAsync(string to, string otpCode)
    {
        var subject = "Your Verification Code";
        var body = $@"
            <h2>Virtual Store Verification</h2>
            <p>Your one-time password is: <strong>{otpCode}</strong></p>
            <p>This code will expire in 10 minutes.</p>";
        await SendEmailAsync(to, subject, body);
    }

    public async Task SendConfirmationEmailAsync(string to, string token)
    {
        var subject = "Confirm your email";
        var body = $@"
            <h2>Virtual Store Email Confirmation</h2>
            <p>Use the following token to confirm your email address:</p>
            <p><strong>{token}</strong></p>
            <p>This token will expire in 24 hours. If you did not request this, ignore this email.</p>";
        await SendEmailAsync(to, subject, body);
    }

    public async Task SendPasswordResetEmailAsync(string to, string token)
    {
        var subject = "Password reset request";
        var body = $@"
            <h2>Virtual Store Password Reset</h2>
            <p>Use the following token to reset your password:</p>
            <p><strong>{token}</strong></p>
            <p>This token will expire in 1 hour. If you did not request this, ignore this email.</p>";
        await SendEmailAsync(to, subject, body);
    }
}
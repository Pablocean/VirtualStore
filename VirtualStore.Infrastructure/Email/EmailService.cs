using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Settings;

namespace VirtualStore.Infrastructure.Email;

public class EmailService : IEmailService
{
    private readonly EmailSettings _emailSettings;
    
    public EmailService(IOptions<EmailSettings> options)
    {
        _emailSettings = options.Value;
    }
    
    public async Task SendEmailAsync(string to, string subject, string body)
    {
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(_emailSettings.SenderName, _emailSettings.SenderEmail));
        email.To.Add(MailboxAddress.Parse(to));
        email.Subject = subject;
        email.Body = new TextPart("html") { Text = body };
        
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_emailSettings.SmtpServer, _emailSettings.Port, 
            _emailSettings.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);
        await smtp.AuthenticateAsync(_emailSettings.Username, _emailSettings.Password);
        await smtp.SendAsync(email);
        await smtp.DisconnectAsync(true);
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
}
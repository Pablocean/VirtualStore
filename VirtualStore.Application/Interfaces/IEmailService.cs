namespace VirtualStore.Application.Interfaces;

public interface IEmailService
{
    Task SendEmailAsync(string to, string subject, string body);
    Task SendOtpEmailAsync(string to, string otpCode);
    Task SendConfirmationEmailAsync(string to, string token);
    Task SendPasswordResetEmailAsync(string to, string token);
}

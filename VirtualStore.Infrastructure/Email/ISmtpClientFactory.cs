using MailKit.Net.Smtp;

namespace VirtualStore.Infrastructure.Email;

/// <summary>
/// Wave T0 testability seam over <c>new SmtpClient()</c>.
/// The default implementation preserves production behavior exactly;
/// tests inject a fake to avoid network I/O.
/// </summary>
public interface ISmtpClientFactory
{
    SmtpClient CreateClient();
}

/// <summary>
/// Default production implementation: creates a real MailKit <see cref="SmtpClient"/>.
/// </summary>
public sealed class SmtpClientFactory : ISmtpClientFactory
{
    public SmtpClient CreateClient() => new();
}

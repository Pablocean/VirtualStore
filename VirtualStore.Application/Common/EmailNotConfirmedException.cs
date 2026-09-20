namespace VirtualStore.Application.Common;

/// <summary>
/// Thrown when login is attempted with an unconfirmed email address.
/// Derives from <see cref="UnauthorizedAccessException"/> so existing catch sites
/// keep working; <c>ApiExceptionHandler</c> maps this specific type to 403.
/// </summary>
public sealed class EmailNotConfirmedException : UnauthorizedAccessException
{
    public EmailNotConfirmedException(string message)
        : base(message)
    {
    }
}

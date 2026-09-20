namespace VirtualStore.Application.Common;

/// <summary>
/// Thrown when an account is temporarily locked out after repeated failed logins.
/// Derives from <see cref="UnauthorizedAccessException"/> so existing catch sites
/// keep working; <c>ApiExceptionHandler</c> maps this specific type to 423 Locked.
/// </summary>
public sealed class AccountLockedException : UnauthorizedAccessException
{
    public AccountLockedException(string message)
        : base(message)
    {
    }
}

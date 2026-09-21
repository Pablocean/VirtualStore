namespace VirtualStore.Application.Common;

/// <summary>
/// Wave T0 testability seam: clock reads for EXPIRY/LOCKOUT logic.
/// Production uses <see cref="SystemDateTimeProvider"/> (identical values to
/// <see cref="DateTime.UtcNow"/>); tests inject a fixed or mocked provider
/// for deterministic expiries and lockout windows.
/// </summary>
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}

/// <summary>
/// Default production implementation: passthrough to <see cref="DateTime.UtcNow"/>.
/// </summary>
public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}

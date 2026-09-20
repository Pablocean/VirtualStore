using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Interfaces;

public interface IUserService
{
    Task<UserDto> CreateUserAsync(CreateUserDto dto);
    Task<UserDto> UpdateUserAsync(string id, UpdateUserDto dto);
    Task DeleteUserAsync(string id);
    Task<UserDto?> GetUserByIdAsync(string id);
    Task<PagedResult<UserDto>> GetUsersAsync(UserFilterDto filter);
    /// <summary>
    /// GDPR export (GET /api/me/export, ADR-0010): profile + orders + carts +
    /// token metadata WITHOUT token secrets. Throws <c>KeyNotFoundException</c>
    /// when the user does not exist.
    /// </summary>
    Task<MeExportDto> GetExportAsync(string userId);
    /// <summary>
    /// GDPR erasure (POST /api/me/purge, ADR-0010): verifies the current
    /// password with BCrypt, then hard-deletes the user document, deletes the
    /// user's carts, pseudonymizes the user's orders
    /// (<c>UserId → "deleted:{sha256hex}"</c>, address stripped), and clears
    /// OTP / email-confirmation / password-reset cache keys. The password is
    /// never logged. Throws <c>UnauthorizedAccessException</c> on a wrong
    /// password, <c>KeyNotFoundException</c> when the user does not exist.
    /// </summary>
    Task<PurgeResultDto> PurgeAsync(string userId, PurgeRequestDto request);
}
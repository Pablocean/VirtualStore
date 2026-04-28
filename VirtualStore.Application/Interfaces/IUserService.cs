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
}
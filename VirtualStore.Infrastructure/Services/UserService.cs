using AutoMapper;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Enums;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly IRepository<User> _userRepo;
    private readonly IMapper _mapper;

    public UserService(IRepository<User> userRepo, IMapper mapper)
    {
        _userRepo = userRepo;
        _mapper = mapper;
    }

    public async Task<UserDto> CreateUserAsync(CreateUserDto dto)
    {
        if (await _userRepo.ExistsAsync(u => u.Email == dto.Email))
            throw new InvalidOperationException("Email already in use");

        var user = _mapper.Map<User>(dto);
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password);
        // Ensure at least Customer role
        if (!user.Roles.Contains(UserRole.Customer))
            user.Roles.Add(UserRole.Customer);

        await _userRepo.AddAsync(user);
        return _mapper.Map<UserDto>(user);
    }

    public async Task<UserDto> UpdateUserAsync(string id, UpdateUserDto dto)
    {
        var user = await _userRepo.GetByIdAsync(id)
            ?? throw new KeyNotFoundException("User not found");

        _mapper.Map(dto, user);
        await _userRepo.UpdateAsync(id, user);
        return _mapper.Map<UserDto>(user);
    }

    public async Task DeleteUserAsync(string id)
    {
        var user = await _userRepo.GetByIdAsync(id) ?? throw new KeyNotFoundException("User not found");
        await _userRepo.DeleteAsync(id);
    }

    public async Task<UserDto?> GetUserByIdAsync(string id)
    {
        var user = await _userRepo.GetByIdAsync(id);
        return user == null ? null : _mapper.Map<UserDto>(user);
    }

    public async Task<PagedResult<UserDto>> GetUsersAsync(UserFilterDto filter)
    {
        var allUsers = await _userRepo.GetAllAsync(); // In production, use efficient query
        var query = allUsers.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.ToLower();
            query = query.Where(u => u.Email.Contains(search) || u.Username.Contains(search) ||
                                   (u.FirstName != null && u.FirstName.Contains(search)) ||
                                   (u.LastName != null && u.LastName.Contains(search)));
        }
        if (filter.Role.HasValue)
            query = query.Where(u => u.Roles.Contains(filter.Role.Value));

        var total = query.Count();
        var items = query.Skip((filter.PageNumber - 1) * filter.PageSize)
                         .Take(filter.PageSize)
                         .Select(u => _mapper.Map<UserDto>(u))
                         .ToList();

        return new PagedResult<UserDto>
        {
            Items = items,
            TotalCount = total,
            PageNumber = filter.PageNumber,
            PageSize = filter.PageSize
        };
    }
}
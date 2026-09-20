using AutoMapper;
using System.Linq.Expressions;
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
        // Multi-role list (no bitwise ops). Default to [Customer] when empty.
        if (dto.Roles is null || dto.Roles.Count == 0)
            user.Roles = new List<UserRole> { UserRole.Customer };
        else
            user.Roles = UserRoles.EnsureValid(dto.Roles);

        await _userRepo.AddAsync(user);
        return _mapper.Map<UserDto>(user);
    }

    public async Task<UserDto> UpdateUserAsync(string id, UpdateUserDto dto)
    {
        var user = await _userRepo.GetByIdAsync(id)
            ?? throw new KeyNotFoundException("User not found");

        // Validate discrete roles when provided (Admin-only controller, so assignment stays allowed).
        if (dto.Roles is not null)
            dto.Roles = UserRoles.EnsureValid(dto.Roles);

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
        var predicate = BuildPredicate(filter);

        var page = filter.PageNumber < 1 ? 1 : filter.PageNumber;
        var size = filter.PageSize <= 0 ? 20 : Math.Min(filter.PageSize, 100);

        // Server-side paging + count (single round-trips for page and total).
        var (items, total) = await _userRepo.PagedAsync(predicate, page, size, sortBy: null, desc: true);

        return new PagedResult<UserDto>
        {
            Items = items.Select(u => _mapper.Map<UserDto>(u)).ToList(),
            TotalCount = checked((int)total),
            PageNumber = page,
            PageSize = size
        };
    }

    private static Expression<Func<User, bool>> BuildPredicate(UserFilterDto filter)
    {
        Expression<Func<User, bool>> predicate = u => true;

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLower();
            Expression<Func<User, bool>> searchPredicate = u =>
                u.Email.ToLower().Contains(search) ||
                u.Username.ToLower().Contains(search) ||
                (u.FirstName != null && u.FirstName.ToLower().Contains(search)) ||
                (u.LastName != null && u.LastName.ToLower().Contains(search));
            predicate = AndAlso(predicate, searchPredicate);
        }

        if (filter.Roles is not null && filter.Roles.Count > 0)
        {
            var roles = filter.Roles.Distinct().ToList();
            Expression<Func<User, bool>> rolePredicate = u => u.Roles.Any(r => roles.Contains(r));
            predicate = AndAlso(predicate, rolePredicate);
        }

        return predicate;
    }

    private static Expression<Func<User, bool>> AndAlso(
        Expression<Func<User, bool>> left,
        Expression<Func<User, bool>> right)
    {
        var param = Expression.Parameter(typeof(User), "u");
        var leftBody = new ParameterReplacer(left.Parameters[0], param).Visit(left.Body)!;
        var rightBody = new ParameterReplacer(right.Parameters[0], param).Visit(right.Body)!;
        return Expression.Lambda<Func<User, bool>>(Expression.AndAlso(leftBody, rightBody), param);
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public ParameterReplacer(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _from ? _to : base.VisitParameter(node);
    }
}

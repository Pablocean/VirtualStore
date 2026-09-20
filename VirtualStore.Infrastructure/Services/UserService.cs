using AutoMapper;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
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
    private readonly IRepository<Order> _orderRepo;
    private readonly IRepository<Cart> _cartRepo;
    private readonly IDistributedCache _cache;
    private readonly IMapper _mapper;

    public UserService(
        IRepository<User> userRepo,
        IMapper mapper,
        IRepository<Order> orderRepo,
        IRepository<Cart> cartRepo,
        IDistributedCache cache)
    {
        _userRepo = userRepo;
        _mapper = mapper;
        _orderRepo = orderRepo;
        _cartRepo = cartRepo;
        _cache = cache;
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

    /// <inheritdoc />
    public async Task<MeExportDto> GetExportAsync(string userId)
    {
        var user = await _userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        var orders = await _orderRepo.FindAsync(o => o.UserId == userId);
        var carts = await _cartRepo.FindAsync(c => c.UserId == userId);

        return new MeExportDto
        {
            Profile = _mapper.Map<UserDto>(user),
            Orders = orders.Select(o => _mapper.Map<OrderDto>(o)).ToList(),
            Carts = carts.Select(c => _mapper.Map<CartDto>(c)).ToList(),
            TokenMetadata = user.RefreshTokens.Select(rt => new RefreshTokenMetadataDto
            {
                Created = rt.Created,
                CreatedByIp = rt.CreatedByIp,
                Expires = rt.Expires,
                Revoked = rt.Revoked,
                RevokedByIp = rt.RevokedByIp,
                HasReplacement = rt.ReplacedByToken != null,
                IsActive = rt.IsActive
            }).ToList(),
            ExportedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task<PurgeResultDto> PurgeAsync(string userId, PurgeRequestDto request)
    {
        var user = await _userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException("User not found.");

        // BCrypt-verify the current password. Never logged (no logger here by design).
        if (!BCrypt.Net.BCrypt.Verify(request.ConfirmPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Current password is incorrect.");

        // 1. Pseudonymize orders: keep items/totals/status for accounting,
        // strip the address and unlink the user (ADR-0010).
        var pseudonym = PseudonymizeUserId(userId);
        var orders = (await _orderRepo.FindAsync(o => o.UserId == userId)).ToList();
        foreach (var order in orders)
        {
            order.UserId = pseudonym;
            order.ShippingAddress = new Address();
            await _orderRepo.UpdateAsync(order.Id, order);
        }

        // 2. Delete carts (hard — no accounting value, direct PII link).
        var carts = (await _cartRepo.FindAsync(c => c.UserId == userId)).ToList();
        foreach (var cart in carts)
            await _cartRepo.HardDeleteAsync(cart.Id);

        // 3. Clear OTP / email-confirmation / password-reset cache keys.
        var emailKey = user.Email.ToLowerInvariant();
        var userKey = user.Id.ToLowerInvariant();
        await _cache.RemoveAsync($"otp_{emailKey}");
        await _cache.RemoveAsync($"otp_attempts_{emailKey}");
        await _cache.RemoveAsync($"emailconfirm_{userKey}");
        await _cache.RemoveAsync($"pwdreset_{emailKey}");

        // 4. Hard-delete the user document last (bypasses soft-delete).
        await _userRepo.HardDeleteAsync(user.Id);

        return new PurgeResultDto
        {
            UserDeleted = true,
            CartsDeleted = carts.Count,
            OrdersPseudonymized = orders.Count
        };
    }

    /// <summary>
    /// GDPR pseudonym for a deleted user id: <c>"deleted:{sha256hex(userId)}"</c>.
    /// Deterministic (repeat purges of the same id link to the same ref) and
    /// one-way (the original id cannot be recovered from the ref).
    /// </summary>
    public static string PseudonymizeUserId(string userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return "deleted:" + Convert.ToHexString(hash).ToLowerInvariant();
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

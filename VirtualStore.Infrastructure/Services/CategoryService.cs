using AutoMapper;
using System.Linq.Expressions;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.Services;

public class CategoryService : ICategoryService
{
    private readonly IRepository<Category> _categoryRepo;
    private readonly IMapper _mapper;

    public CategoryService(IRepository<Category> categoryRepo, IMapper mapper)
    {
        _categoryRepo = categoryRepo;
        _mapper = mapper;
    }

    public async Task<PagedResult<CategoryDto>> GetPagedAsync(CategoryFilterDto filter, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var predicate = BuildPredicate(filter);

        var page = filter.PageNumber < 1 ? 1 : filter.PageNumber;
        var size = filter.PageSize <= 0 ? 20 : Math.Min(filter.PageSize, 100);

        // Server-side filter + paging (Name contains translates to regex via Builders).
        // Sort by Name asc preserves the previous in-memory OrderBy(c => c.Name).
        var (items, total) = await _categoryRepo.PagedAsync(predicate, page, size, sortBy: "Name", desc: false, cancellationToken);

        return new PagedResult<CategoryDto>
        {
            Items = items.Select(_mapper.Map<CategoryDto>).ToList(),
            TotalCount = checked((int)total),
            PageNumber = page,
            PageSize = size
        };
    }

    public async Task<CategoryDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var category = await _categoryRepo.GetByIdAsync(id, cancellationToken);
        return category is null ? null : _mapper.Map<CategoryDto>(category);
    }

    public async Task<CategoryDto> CreateAsync(CreateCategoryDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var category = _mapper.Map<Category>(dto);
        await _categoryRepo.AddAsync(category, cancellationToken);
        return _mapper.Map<CategoryDto>(category);
    }

    public async Task<CategoryDto> UpdateAsync(string id, UpdateCategoryDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var category = await _categoryRepo.GetByIdAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Category not found.");
        _mapper.Map(dto, category);
        await _categoryRepo.UpdateAsync(id, category, cancellationToken);
        return _mapper.Map<CategoryDto>(category);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await _categoryRepo.GetByIdAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Category not found.");
        // Soft delete (MongoRepository flags IsDeleted instead of removing the document).
        await _categoryRepo.DeleteAsync(id, cancellationToken);
    }

    private static Expression<Func<Category, bool>> BuildPredicate(CategoryFilterDto filter)
    {
        Expression<Func<Category, bool>> predicate = c => true;

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLower();
            Expression<Func<Category, bool>> searchPredicate = c => c.Name.ToLower().Contains(search);
            predicate = AndAlso(predicate, searchPredicate);
        }
        if (filter.IsActive.HasValue)
        {
            var isActive = filter.IsActive.Value;
            Expression<Func<Category, bool>> activePredicate = c => c.IsActive == isActive;
            predicate = AndAlso(predicate, activePredicate);
        }

        return predicate;
    }

    private static Expression<Func<Category, bool>> AndAlso(
        Expression<Func<Category, bool>> left,
        Expression<Func<Category, bool>> right)
    {
        var param = Expression.Parameter(typeof(Category), "c");
        var leftBody = new ParameterReplacer(left.Parameters[0], param).Visit(left.Body)!;
        var rightBody = new ParameterReplacer(right.Parameters[0], param).Visit(right.Body)!;
        return Expression.Lambda<Func<Category, bool>>(Expression.AndAlso(leftBody, rightBody), param);
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

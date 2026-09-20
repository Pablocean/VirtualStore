using AutoMapper;
using System.Linq.Expressions;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.Services;

public class ProductService : IProductService
{
    private readonly IRepository<Product> _productRepo;
    private readonly IRepository<Category> _categoryRepo;
    private readonly IMapper _mapper;

    public ProductService(IRepository<Product> productRepo, IRepository<Category> categoryRepo, IMapper mapper)
    {
        _productRepo = productRepo;
        _categoryRepo = categoryRepo;
        _mapper = mapper;
    }

    public async Task<ProductDto?> GetProductByIdAsync(string id)
    {
        var product = await _productRepo.GetByIdAsync(id);
        if (product == null) return null;

        var dto = _mapper.Map<ProductDto>(product);
        if (!string.IsNullOrEmpty(product.CategoryId))
        {
            var cat = await _categoryRepo.GetByIdAsync(product.CategoryId);
            dto.CategoryName = cat?.Name;
        }
        return dto;
    }

    public async Task<PagedResult<ProductDto>> GetProductsAsync(ProductFilterDto filter)
    {
        var predicate = BuildPredicate(filter);

        var page = filter.PageNumber < 1 ? 1 : filter.PageNumber;
        var size = filter.PageSize <= 0 ? 20 : Math.Min(filter.PageSize, 100);

        // Server-side filter + paging (Name contains translates to regex via Builders).
        var (items, total) = await _productRepo.PagedAsync(predicate, page, size, sortBy: null, desc: true);

        // Batch category hydration (single query instead of N+1 GetById).
        var categoryIds = items
            .Where(p => !string.IsNullOrEmpty(p.CategoryId))
            .Select(p => p.CategoryId)
            .Distinct()
            .ToList();

        Dictionary<string, string> categoryNames = new();
        if (categoryIds.Count > 0)
        {
            var categories = await _categoryRepo.FindAsync(c => categoryIds.Contains(c.Id));
            categoryNames = categories.ToDictionary(c => c.Id, c => c.Name);
        }

        var dtos = items.Select(product =>
        {
            var dto = _mapper.Map<ProductDto>(product);
            if (!string.IsNullOrEmpty(product.CategoryId) &&
                categoryNames.TryGetValue(product.CategoryId, out var name))
            {
                dto.CategoryName = name;
            }
            return dto;
        }).ToList();

        return new PagedResult<ProductDto>
        {
            Items = dtos,
            TotalCount = checked((int)total),
            PageNumber = page,
            PageSize = size
        };
    }

    public async Task<ProductDto> CreateProductAsync(CreateProductDto dto)
    {
        var product = _mapper.Map<Product>(dto);
        await _productRepo.AddAsync(product);
        return _mapper.Map<ProductDto>(product);
    }

    public async Task<ProductDto> UpdateProductAsync(string id, UpdateProductDto dto)
    {
        var product = await _productRepo.GetByIdAsync(id) ?? throw new KeyNotFoundException("Product not found");
        _mapper.Map(dto, product);
        await _productRepo.UpdateAsync(id, product);
        return _mapper.Map<ProductDto>(product);
    }

    public async Task DeleteProductAsync(string id)
    {
        var product = await _productRepo.GetByIdAsync(id) ?? throw new KeyNotFoundException("Product not found");
        await _productRepo.DeleteAsync(id);
    }

    private static Expression<Func<Product, bool>> BuildPredicate(ProductFilterDto filter)
    {
        Expression<Func<Product, bool>> predicate = p => true;

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLower();
            Expression<Func<Product, bool>> searchPredicate = p => p.Name.ToLower().Contains(search);
            predicate = AndAlso(predicate, searchPredicate);
        }
        if (!string.IsNullOrWhiteSpace(filter.CategoryId))
        {
            var categoryId = filter.CategoryId;
            Expression<Func<Product, bool>> categoryPredicate = p => p.CategoryId == categoryId;
            predicate = AndAlso(predicate, categoryPredicate);
        }
        if (filter.MinPrice.HasValue)
        {
            var min = filter.MinPrice.Value;
            Expression<Func<Product, bool>> minPredicate = p => p.Price >= min;
            predicate = AndAlso(predicate, minPredicate);
        }
        if (filter.MaxPrice.HasValue)
        {
            var max = filter.MaxPrice.Value;
            Expression<Func<Product, bool>> maxPredicate = p => p.Price <= max;
            predicate = AndAlso(predicate, maxPredicate);
        }
        if (filter.IsActive.HasValue)
        {
            var isActive = filter.IsActive.Value;
            Expression<Func<Product, bool>> activePredicate = p => p.IsActive == isActive;
            predicate = AndAlso(predicate, activePredicate);
        }

        return predicate;
    }

    private static Expression<Func<Product, bool>> AndAlso(
        Expression<Func<Product, bool>> left,
        Expression<Func<Product, bool>> right)
    {
        var param = Expression.Parameter(typeof(Product), "p");
        var leftBody = new ParameterReplacer(left.Parameters[0], param).Visit(left.Body)!;
        var rightBody = new ParameterReplacer(right.Parameters[0], param).Visit(right.Body)!;
        return Expression.Lambda<Func<Product, bool>>(Expression.AndAlso(leftBody, rightBody), param);
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

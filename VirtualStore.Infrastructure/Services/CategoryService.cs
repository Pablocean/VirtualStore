using AutoMapper;
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

        var all = await _categoryRepo.GetAllAsync();
        var query = all.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
            query = query.Where(c => c.Name.Contains(filter.Search, StringComparison.OrdinalIgnoreCase));
        if (filter.IsActive.HasValue)
            query = query.Where(c => c.IsActive == filter.IsActive.Value);

        var total = query.Count();
        var items = query.OrderBy(c => c.Name)
            .Skip((filter.PageNumber - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(_mapper.Map<CategoryDto>)
            .ToList();

        return new PagedResult<CategoryDto>
        {
            Items = items,
            TotalCount = total,
            PageNumber = filter.PageNumber,
            PageSize = filter.PageSize
        };
    }

    public async Task<CategoryDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var category = await _categoryRepo.GetByIdAsync(id);
        return category is null ? null : _mapper.Map<CategoryDto>(category);
    }

    public async Task<CategoryDto> CreateAsync(CreateCategoryDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var category = _mapper.Map<Category>(dto);
        await _categoryRepo.AddAsync(category);
        return _mapper.Map<CategoryDto>(category);
    }

    public async Task<CategoryDto> UpdateAsync(string id, UpdateCategoryDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var category = await _categoryRepo.GetByIdAsync(id) ?? throw new KeyNotFoundException("Category not found.");
        _mapper.Map(dto, category);
        await _categoryRepo.UpdateAsync(id, category);
        return _mapper.Map<CategoryDto>(category);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await _categoryRepo.GetByIdAsync(id) ?? throw new KeyNotFoundException("Category not found.");
        // Soft delete (MongoRepository flags IsDeleted instead of removing the document).
        await _categoryRepo.DeleteAsync(id);
    }
}

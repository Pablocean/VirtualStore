using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Interfaces;

public interface ICategoryService
{
    Task<PagedResult<CategoryDto>> GetPagedAsync(CategoryFilterDto filter, CancellationToken cancellationToken = default);
    Task<CategoryDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<CategoryDto> CreateAsync(CreateCategoryDto dto, CancellationToken cancellationToken = default);
    Task<CategoryDto> UpdateAsync(string id, UpdateCategoryDto dto, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

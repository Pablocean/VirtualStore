using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Interfaces;

public interface IProductService
{
    Task<ProductDto?> GetProductByIdAsync(string id);
    Task<PagedResult<ProductDto>> GetProductsAsync(ProductFilterDto filter);
    Task<ProductDto> CreateProductAsync(CreateProductDto dto);
    Task<ProductDto> UpdateProductAsync(string id, UpdateProductDto dto);
    Task DeleteProductAsync(string id);
}
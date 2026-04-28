using AutoMapper;
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
        var allProducts = await _productRepo.GetAllAsync();
        var query = allProducts.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
            query = query.Where(p => p.Name.Contains(filter.Search, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.CategoryId))
            query = query.Where(p => p.CategoryId == filter.CategoryId);
        if (filter.MinPrice.HasValue)
            query = query.Where(p => p.Price >= filter.MinPrice.Value);
        if (filter.MaxPrice.HasValue)
            query = query.Where(p => p.Price <= filter.MaxPrice.Value);
        if (filter.IsActive.HasValue)
            query = query.Where(p => p.IsActive == filter.IsActive.Value);

        var total = query.Count();
        var items = query.Skip((filter.PageNumber - 1) * filter.PageSize)
                         .Take(filter.PageSize)
                         .ToList();

        var dtos = new List<ProductDto>();
        foreach (var product in items)
        {
            var dto = _mapper.Map<ProductDto>(product);
            if (!string.IsNullOrEmpty(product.CategoryId))
            {
                var cat = await _categoryRepo.GetByIdAsync(product.CategoryId);
                dto.CategoryName = cat?.Name;
            }
            dtos.Add(dto);
        }

        return new PagedResult<ProductDto>
        {
            Items = dtos,
            TotalCount = total,
            PageNumber = filter.PageNumber,
            PageSize = filter.PageSize
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
}
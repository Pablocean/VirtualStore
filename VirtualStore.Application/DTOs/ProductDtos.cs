namespace VirtualStore.Application.DTOs;

public class ProductDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "usd";
    public int StockQuantity { get; set; }
    public string? ImageUrl { get; set; }
    public string CategoryId { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool IsActive { get; set; }
}

public class CreateProductDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "usd";
    public int StockQuantity { get; set; }
    public string? ImageUrl { get; set; }
    public string CategoryId { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
}

public class UpdateProductDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public decimal? Price { get; set; }
    public string? Currency { get; set; }
    public int? StockQuantity { get; set; }
    public string? ImageUrl { get; set; }
    public string? CategoryId { get; set; }
    public List<string>? Tags { get; set; }
    public bool? IsActive { get; set; }
}

public class ProductFilterDto
{
    public string? Search { get; set; }
    public string? CategoryId { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public bool? IsActive { get; set; } = true;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
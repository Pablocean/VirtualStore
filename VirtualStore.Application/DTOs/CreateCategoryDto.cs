namespace VirtualStore.Application.DTOs;

public class CreateCategoryDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ParentCategoryId { get; set; }
}
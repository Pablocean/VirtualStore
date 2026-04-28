namespace VirtualStore.Application.DTOs;

public class UpdateCategoryDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ParentCategoryId { get; set; }
    public bool? IsActive { get; set; }
}
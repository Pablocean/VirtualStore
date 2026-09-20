using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;

namespace VirtualStore.API.Controllers;

/// <summary>Category catalog endpoints.</summary>
[ApiController]
[Route("api/[controller]")]
public class CategoriesController : ControllerBase
{
    private readonly ICategoryService _categoryService;

    public CategoriesController(ICategoryService categoryService)
    {
        _categoryService = categoryService ?? throw new ArgumentNullException(nameof(categoryService));
    }

    /// <summary>Gets a paged list of categories.</summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(Application.Common.PagedResult<CategoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCategories([FromQuery] CategoryFilterDto filter, CancellationToken cancellationToken)
    {
        var result = await _categoryService.GetPagedAsync(filter, cancellationToken);
        return Ok(result);
    }

    /// <summary>Gets a category by ID.</summary>
    [HttpGet("{id}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CategoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCategory(string id, CancellationToken cancellationToken)
    {
        var category = await _categoryService.GetByIdAsync(id, cancellationToken);
        if (category is null) return NotFound();
        return Ok(category);
    }

    /// <summary>Creates a new category. Admins and managers only.</summary>
    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(CategoryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateCategory([FromBody] CreateCategoryDto dto, CancellationToken cancellationToken)
    {
        var category = await _categoryService.CreateAsync(dto, cancellationToken);
        return CreatedAtAction(nameof(GetCategory), new { id = category.Id }, category);
    }

    /// <summary>Updates a category. Admins and managers only.</summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(CategoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCategory(string id, [FromBody] UpdateCategoryDto dto, CancellationToken cancellationToken)
    {
        var category = await _categoryService.UpdateAsync(id, dto, cancellationToken);
        return Ok(category);
    }

    /// <summary>Soft-deletes a category. Admins only.</summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCategory(string id, CancellationToken cancellationToken)
    {
        await _categoryService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}

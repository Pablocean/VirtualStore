using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;

namespace VirtualStore.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class CartController : ControllerBase
{
    private readonly ICartService _cartService;
    public CartController(ICartService cartService) => _cartService = cartService;

    private string? CurrentUserId => User.GetUserId();

    private bool TryGetUserId(out string userId)
    {
        userId = CurrentUserId ?? string.Empty;
        return !string.IsNullOrEmpty(userId);
    }

    [HttpGet]
    public async Task<ActionResult<CartDto>> GetCart()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "User identity not found" });
        return Ok(await _cartService.GetCartAsync(userId));
    }

    [HttpPost("items")]
    public async Task<ActionResult<CartDto>> AddToCart(AddToCartDto dto)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "User identity not found" });
        return Ok(await _cartService.AddToCartAsync(userId, dto));
    }

    [HttpPut("items/{productId}")]
    public async Task<ActionResult<CartDto>> UpdateItem(string productId, [FromBody] int quantity)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "User identity not found" });
        return Ok(await _cartService.UpdateCartItemAsync(userId, productId, quantity));
    }

    [HttpDelete("items/{productId}")]
    public async Task<ActionResult<CartDto>> RemoveItem(string productId)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "User identity not found" });
        return Ok(await _cartService.RemoveFromCartAsync(userId, productId));
    }

    [HttpDelete]
    public async Task<IActionResult> ClearCart()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { message = "User identity not found" });
        await _cartService.ClearCartAsync(userId);
        return NoContent();
    }
}

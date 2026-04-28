using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<ActionResult<CartDto>> GetCart() => Ok(await _cartService.GetCartAsync(UserId));

    [HttpPost("items")]
    public async Task<ActionResult<CartDto>> AddToCart(AddToCartDto dto) => Ok(await _cartService.AddToCartAsync(UserId, dto));

    [HttpPut("items/{productId}")]
    public async Task<ActionResult<CartDto>> UpdateItem(string productId, [FromBody] int quantity) =>
        Ok(await _cartService.UpdateCartItemAsync(UserId, productId, quantity));

    [HttpDelete("items/{productId}")]
    public async Task<ActionResult<CartDto>> RemoveItem(string productId) =>
        Ok(await _cartService.RemoveFromCartAsync(UserId, productId));

    [HttpDelete]
    public async Task<IActionResult> ClearCart()
    {
        await _cartService.ClearCartAsync(UserId);
        return NoContent();
    }
}
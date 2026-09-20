using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;

namespace VirtualStore.API.Controllers;

/// <summary>Order management endpoints.</summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrdersController(IOrderService orderService)
    {
        _orderService = orderService ?? throw new ArgumentNullException(nameof(orderService));
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Creates a new order for the current user. Prices are re-computed server-side.</summary>
    /// <remarks>
    /// Idempotency: send <c>Idempotency-Key</c> to make retries safe. The header wins
    /// over <c>CreateOrderDto.IdempotencyKey</c>. Same (user, key) replays return the
    /// existing order with 201; same key with a different payload is a 409.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderDto dto, CancellationToken cancellationToken)
    {
        if (Request.Headers.TryGetValue("Idempotency-Key", out var headerKey)
            && !string.IsNullOrWhiteSpace(headerKey.ToString()))
            dto.IdempotencyKey = headerKey.ToString().Trim();

        var order = await _orderService.CreateOrderAsync(UserId, dto, cancellationToken);
        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }

    /// <summary>Gets the paged order history of the current user.</summary>
    [HttpGet("my")]
    [ProducesResponseType(typeof(Application.Common.PagedResult<OrderDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyOrders([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await _orderService.GetUserOrdersAsync(UserId, pageNumber, pageSize, cancellationToken);
        return Ok(result);
    }

    /// <summary>Gets a single order. Owners and admins only.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrder(string id, CancellationToken cancellationToken)
    {
        var order = await _orderService.GetOrderByIdAsync(id, cancellationToken);
        if (order is null) return NotFound();
        if (!string.Equals(order.UserId, UserId, StringComparison.Ordinal) && !User.IsInRole("Admin"))
            return Forbid();
        return Ok(order);
    }

    /// <summary>Updates the order status. Admins only. Only allowed state transitions succeed.</summary>
    [Authorize(Roles = "Admin")]
    [HttpPatch("{id}/status")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateOrderStatus(string id, [FromBody] UpdateOrderStatusDto dto, CancellationToken cancellationToken)
    {
        var order = await _orderService.UpdateOrderStatusAsync(id, dto.Status, cancellationToken);
        return Ok(order);
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;

namespace VirtualStore.API.Controllers;

/// <summary>
/// Self-service privacy endpoints (ADR-0010). Any authenticated role; the
/// user id always comes from claims (<c>User.GetUserId()</c>) — never from
/// the body. Distinct from <c>UsersController</c> (Admin-only soft-delete):
/// <c>DELETE /api/users/{id}</c> flags <c>IsDeleted</c> and keeps the data,
/// while <c>POST /api/me/purge</c> hard-deletes the user, deletes carts,
/// and pseudonymizes orders.
/// </summary>
[Authorize]
[ApiController]
[Route("api/me")]
public class MeController : ControllerBase
{
    private readonly IUserService _userService;

    public MeController(IUserService userService)
    {
        _userService = userService ?? throw new ArgumentNullException(nameof(userService));
    }

    /// <summary>Exports the current user's profile, orders, carts, and token metadata (no secrets).</summary>
    [HttpGet("export")]
    [ProducesResponseType(typeof(MeExportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Export()
    {
        var userId = User.GetUserId()
            ?? throw new UnauthorizedAccessException("Identity missing.");
        return Ok(await _userService.GetExportAsync(userId));
    }

    /// <summary>Erases the current user (GDPR right to erasure). Requires the current password as confirmation.</summary>
    [HttpPost("purge")]
    [ProducesResponseType(typeof(PurgeResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Purge(PurgeRequestDto request)
    {
        var userId = User.GetUserId()
            ?? throw new UnauthorizedAccessException("Identity missing.");
        return Ok(await _userService.PurgeAsync(userId, request));
    }
}

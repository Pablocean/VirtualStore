using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Interfaces;

namespace VirtualStore.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var response = await _authService.LoginAsync(request, ipAddress);
        SetRefreshTokenCookie(response.RefreshToken);
        return Ok(response);
    }

    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken()
    {
        var refreshToken = Request.Cookies["refreshToken"];
        if (string.IsNullOrEmpty(refreshToken))
            return BadRequest(new { message = "Refresh token not provided" });

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var response = await _authService.RefreshTokenAsync(refreshToken, ipAddress);
        SetRefreshTokenCookie(response.RefreshToken);
        return Ok(response);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var refreshToken = Request.Cookies["refreshToken"];
        if (string.IsNullOrEmpty(refreshToken))
            return BadRequest(new { message = "Refresh token not provided" });

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        await _authService.RevokeTokenAsync(refreshToken, ipAddress);
        Response.Cookies.Delete("refreshToken");
        return Ok(new { message = "Logged out successfully" });
    }

    private void SetRefreshTokenCookie(string token)
    {
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Expires = DateTime.UtcNow.AddDays(7),
            Secure = true,
            SameSite = SameSiteMode.Strict
        };
        Response.Cookies.Append("refreshToken", token, cookieOptions);
    }
}
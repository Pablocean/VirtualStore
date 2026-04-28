using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Infrastructure.Services;

namespace VirtualStore.API.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService ?? throw new ArgumentNullException(nameof(userService));
    }

    [HttpGet]
    public async Task<IActionResult> GetUsers([FromQuery] UserFilterDto filter)
        => Ok(await _userService.GetUsersAsync(filter));

    [HttpPost]
    public async Task<IActionResult> CreateUser(CreateUserDto dto)
        => Ok(await _userService.CreateUserAsync(dto));

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUser(string id, UpdateUserDto dto)
        => Ok(await _userService.UpdateUserAsync(id, dto));

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        await _userService.DeleteUserAsync(id);
        return NoContent();
    }
}

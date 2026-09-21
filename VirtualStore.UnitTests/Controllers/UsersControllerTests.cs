using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using VirtualStore.API.Controllers;
using VirtualStore.Application.Common;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using Xunit;

namespace VirtualStore.UnitTests.Controllers;

public class UsersControllerTests
{
    private static (UsersController Controller, Mock<IUserService> Users) Create()
    {
        var users = new Mock<IUserService>();
        var controller = new UsersController(users.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (controller, users);
    }

    [Fact]
    public void Ctor_NullService_Throws()
    {
        var act = () => new UsersController(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetUsers_Returns_Ok()
    {
        var (controller, users) = Create();
        var filter = new UserFilterDto { Search = "x" };
        var paged = new PagedResult<UserDto> { Items = new List<UserDto> { new() { Id = "u1" } }, TotalCount = 1 };
        users.Setup(u => u.GetUsersAsync(filter)).ReturnsAsync(paged);

        var result = await controller.GetUsers(filter);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(paged);
    }

    [Fact]
    public async Task CreateUser_Returns_Ok()
    {
        var (controller, users) = Create();
        var dto = new CreateUserDto { Email = "e", Username = "u", Password = "p" };
        var created = new UserDto { Id = "u1" };
        users.Setup(u => u.CreateUserAsync(dto)).ReturnsAsync(created);

        var result = await controller.CreateUser(dto);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(created);
    }

    [Fact]
    public async Task UpdateUser_Returns_Ok()
    {
        var (controller, users) = Create();
        var dto = new UpdateUserDto { Username = "u2" };
        var updated = new UserDto { Id = "u1", Username = "u2" };
        users.Setup(u => u.UpdateUserAsync("u1", dto)).ReturnsAsync(updated);

        var result = await controller.UpdateUser("u1", dto);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(updated);
    }

    [Fact]
    public async Task DeleteUser_Returns_NoContent()
    {
        var (controller, users) = Create();

        var result = await controller.DeleteUser("u1");

        result.Should().BeOfType<NoContentResult>();
        users.Verify(u => u.DeleteUserAsync("u1"), Times.Once);
    }
}

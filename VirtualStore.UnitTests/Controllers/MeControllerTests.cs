using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using VirtualStore.API.Controllers;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using Xunit;

namespace VirtualStore.UnitTests.Controllers;

public class MeControllerTests
{
    private static (MeController Controller, Mock<IUserService> Users) Create(params Claim[] claims)
    {
        var users = new Mock<IUserService>();
        var controller = new MeController(users.Object);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, users);
    }

    [Fact]
    public void Ctor_NullService_Throws()
    {
        var act = () => new MeController(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Export_NoIdentity_Throws()
    {
        var (controller, _) = Create();

        var act = () => controller.Export();

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Export_SubClaim_Returns_Ok()
    {
        var (controller, users) = Create(new Claim("sub", "user-1"));
        var export = new MeExportDto { Profile = new UserDto { Id = "user-1" } };
        users.Setup(u => u.GetExportAsync("user-1")).ReturnsAsync(export);

        var result = await controller.Export();

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(export);
    }

    [Fact]
    public async Task Export_NameIdentifierFallback_Returns_Ok()
    {
        var (controller, users) = Create(new Claim(ClaimTypes.NameIdentifier, "user-2"));
        users.Setup(u => u.GetExportAsync("user-2")).ReturnsAsync(new MeExportDto());

        var result = await controller.Export();

        result.Should().BeOfType<OkObjectResult>();
        users.Verify(u => u.GetExportAsync("user-2"), Times.Once);
    }

    [Fact]
    public async Task Purge_NoIdentity_Throws()
    {
        var (controller, _) = Create();

        var act = () => controller.Purge(new PurgeRequestDto { ConfirmPassword = "p" });

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Purge_Passes_UserId_And_Request()
    {
        var (controller, users) = Create(new Claim("sub", "user-1"));
        var request = new PurgeRequestDto { ConfirmPassword = "secret" };
        var expected = new PurgeResultDto { UserDeleted = true, CartsDeleted = 1, OrdersPseudonymized = 2 };
        users.Setup(u => u.PurgeAsync("user-1", request)).ReturnsAsync(expected);

        var result = await controller.Purge(request);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(expected);
    }
}

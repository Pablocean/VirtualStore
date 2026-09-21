using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using VirtualStore.API.Controllers;
using VirtualStore.Application.DTOs.Auth;
using VirtualStore.Application.Interfaces;
using Xunit;

namespace VirtualStore.UnitTests.Controllers;

public class AuthControllerTests
{
    private static (AuthController Controller, Mock<IAuthService> Auth, DefaultHttpContext Http) Create(
        string? ip = "1.2.3.4",
        string? cookie = null,
        params Claim[] claims)
    {
        var auth = new Mock<IAuthService>();
        var controller = new AuthController(auth.Object);
        var http = new DefaultHttpContext();
        if (ip is not null)
            http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (cookie is not null)
            http.Request.Headers.Cookie = $"refreshToken={cookie}";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, auth, http);
    }

    [Fact]
    public void Ctor_NullService_Throws()
    {
        var act = () => new AuthController(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Login_Passes_RemoteIp_And_Sets_Cookie()
    {
        var (controller, auth, http) = Create(ip: "1.2.3.4");
        var response = new TokenResponse { AccessToken = "a", RefreshToken = "r", ExpiresAt = DateTime.UtcNow };
        auth.Setup(a => a.LoginAsync(It.IsAny<LoginRequest>(), "1.2.3.4")).ReturnsAsync(response);

        var result = await controller.Login(new LoginRequest { Email = "e", Password = "p" });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(response);
        auth.Verify(a => a.LoginAsync(It.IsAny<LoginRequest>(), "1.2.3.4"), Times.Once);
        http.Response.Headers["Set-Cookie"].ToString().Should().Contain("refreshToken=r");
    }

    [Fact]
    public async Task Login_NullIp_Uses_Unknown()
    {
        var (controller, auth, _) = Create(ip: null);
        auth.Setup(a => a.LoginAsync(It.IsAny<LoginRequest>(), "unknown"))
            .ReturnsAsync(new TokenResponse { AccessToken = "a", RefreshToken = "r" });

        var result = await controller.Login(new LoginRequest { Email = "e", Password = "p" });

        result.Should().BeOfType<OkObjectResult>();
        auth.Verify(a => a.LoginAsync(It.IsAny<LoginRequest>(), "unknown"), Times.Once);
    }

    [Fact]
    public async Task RefreshToken_MissingCookie_Returns_400()
    {
        var (controller, auth, _) = Create(cookie: null);

        var result = await controller.RefreshToken();

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var problem = bad.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Detail.Should().Be("Refresh token not provided.");
        auth.Verify(a => a.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RefreshToken_EmptyCookie_Returns_400()
    {
        var (controller, auth, _) = Create(cookie: string.Empty);

        var result = await controller.RefreshToken();

        result.Should().BeOfType<BadRequestObjectResult>();
        auth.Verify(a => a.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RefreshToken_WithCookie_Passes_Ip_And_Sets_Cookie()
    {
        var (controller, auth, http) = Create(ip: "5.6.7.8", cookie: "tok");
        var response = new TokenResponse { AccessToken = "a", RefreshToken = "new", ExpiresAt = DateTime.UtcNow };
        auth.Setup(a => a.RefreshTokenAsync("tok", "5.6.7.8")).ReturnsAsync(response);

        var result = await controller.RefreshToken();

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(response);
        http.Response.Headers["Set-Cookie"].ToString().Should().Contain("refreshToken=new");
    }

    [Fact]
    public async Task RefreshToken_NullIp_Uses_Unknown()
    {
        var (controller, auth, _) = Create(ip: null, cookie: "tok");
        auth.Setup(a => a.RefreshTokenAsync("tok", "unknown"))
            .ReturnsAsync(new TokenResponse { AccessToken = "a", RefreshToken = "r" });

        await controller.RefreshToken();

        auth.Verify(a => a.RefreshTokenAsync("tok", "unknown"), Times.Once);
    }

    [Fact]
    public async Task Logout_MissingCookie_Returns_400()
    {
        var (controller, auth, _) = Create(cookie: null);

        var result = await controller.Logout();

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<ProblemDetails>().Subject.Detail.Should().Be("Refresh token not provided.");
        auth.Verify(a => a.RevokeTokenAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Logout_EmptyCookie_Returns_400()
    {
        var (controller, auth, _) = Create(cookie: string.Empty);

        var result = await controller.Logout();

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Logout_WithCookie_Revokes_With_Ip()
    {
        var (controller, auth, _) = Create(ip: "9.9.9.9", cookie: "tok");

        var result = await controller.Logout();

        result.Should().BeOfType<OkObjectResult>();
        auth.Verify(a => a.RevokeTokenAsync("tok", "9.9.9.9"), Times.Once);
    }

    [Fact]
    public async Task Logout_NullIp_Uses_Unknown()
    {
        var (controller, auth, _) = Create(ip: null, cookie: "tok");

        await controller.Logout();

        auth.Verify(a => a.RevokeTokenAsync("tok", "unknown"), Times.Once);
    }

    [Fact]
    public async Task ConfirmEmail_Ok()
    {
        var (controller, auth, _) = Create();
        var dto = new ConfirmEmailDto { Email = "e", Token = "t" };

        var result = await controller.ConfirmEmail(dto);

        result.Should().BeOfType<OkObjectResult>();
        auth.Verify(a => a.ConfirmEmailAsync(dto), Times.Once);
    }

    [Fact]
    public async Task ResendConfirmation_Ok()
    {
        var (controller, auth, _) = Create();
        var dto = new ResendConfirmationDto { Email = "e" };

        var result = await controller.ResendConfirmation(dto);

        result.Should().BeOfType<OkObjectResult>();
        auth.Verify(a => a.ResendConfirmationAsync(dto), Times.Once);
    }

    [Fact]
    public async Task ForgotPassword_Ok()
    {
        var (controller, auth, _) = Create();
        var dto = new ForgotPasswordDto { Email = "e" };

        var result = await controller.ForgotPassword(dto);

        result.Should().BeOfType<OkObjectResult>();
        auth.Verify(a => a.ForgotPasswordAsync(dto), Times.Once);
    }

    [Fact]
    public async Task ResetPassword_Passes_Ip()
    {
        var (controller, auth, _) = Create(ip: "2.2.2.2");
        var dto = new ResetPasswordDto { Email = "e", Token = "t", NewPassword = "n" };

        var result = await controller.ResetPassword(dto);

        result.Should().BeOfType<OkObjectResult>();
        auth.Verify(a => a.ResetPasswordAsync(dto, "2.2.2.2"), Times.Once);
    }

    [Fact]
    public async Task ResetPassword_NullIp_Uses_Unknown()
    {
        var (controller, auth, _) = Create(ip: null);
        var dto = new ResetPasswordDto { Email = "e", Token = "t", NewPassword = "n" };

        await controller.ResetPassword(dto);

        auth.Verify(a => a.ResetPasswordAsync(dto, "unknown"), Times.Once);
    }

    [Fact]
    public async Task ChangePassword_NoIdentity_Throws()
    {
        var (controller, _, _) = Create();

        var act = () => controller.ChangePassword(new ChangePasswordDto { CurrentPassword = "o", NewPassword = "n" });

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ChangePassword_SubClaim_Passes_UserId_And_Ip()
    {
        var (controller, auth, _) = Create(ip: "3.3.3.3", claims: new Claim("sub", "user-1"));
        var dto = new ChangePasswordDto { CurrentPassword = "o", NewPassword = "n" };

        var result = await controller.ChangePassword(dto);

        result.Should().BeOfType<OkObjectResult>();
        auth.Verify(a => a.ChangePasswordAsync("user-1", dto, "3.3.3.3"), Times.Once);
    }

    [Fact]
    public async Task ChangePassword_NameIdentifierFallback_And_NullIp()
    {
        var (controller, auth, _) = Create(ip: null, claims: new Claim(ClaimTypes.NameIdentifier, "user-2"));
        var dto = new ChangePasswordDto { CurrentPassword = "o", NewPassword = "n" };

        await controller.ChangePassword(dto);

        auth.Verify(a => a.ChangePasswordAsync("user-2", dto, "unknown"), Times.Once);
    }
}

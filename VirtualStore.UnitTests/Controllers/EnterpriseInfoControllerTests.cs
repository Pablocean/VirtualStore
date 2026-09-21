using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using VirtualStore.API.Controllers;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using Xunit;

namespace VirtualStore.UnitTests.Controllers;

public class EnterpriseInfoControllerTests
{
    private static (EnterpriseInfoController Controller, Mock<IEnterpriseInfoService> Service) Create()
    {
        var service = new Mock<IEnterpriseInfoService>();
        var controller = new EnterpriseInfoController(service.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (controller, service);
    }

    [Fact]
    public void Ctor_NullService_Throws()
    {
        var act = () => new EnterpriseInfoController(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetEnterpriseInfo_Unknown_Returns_NotFound()
    {
        var (controller, service) = Create();
        service.Setup(s => s.GetEnterpriseInfoAsync(It.IsAny<CancellationToken>())).ReturnsAsync((EnterpriseInfoDto?)null);

        var result = await controller.GetEnterpriseInfo(CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetEnterpriseInfo_Found_Returns_Ok_And_Passes_Token()
    {
        var (controller, service) = Create();
        var dto = new EnterpriseInfoDto { Id = "e1", CompanyName = "Acme" };
        using var cts = new CancellationTokenSource();
        service.Setup(s => s.GetEnterpriseInfoAsync(cts.Token)).ReturnsAsync(dto);

        var result = await controller.GetEnterpriseInfo(cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task UpdateEnterpriseInfo_Returns_Ok_And_Passes_Token()
    {
        var (controller, service) = Create();
        var dto = new UpdateEnterpriseInfoDto { CompanyName = "Acme" };
        var updated = new EnterpriseInfoDto { Id = "e1", CompanyName = "Acme" };
        using var cts = new CancellationTokenSource();
        service.Setup(s => s.UpdateEnterpriseInfoAsync(dto, cts.Token)).ReturnsAsync(updated);

        var result = await controller.UpdateEnterpriseInfo(dto, cts.Token);

        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeSameAs(updated);
    }
}

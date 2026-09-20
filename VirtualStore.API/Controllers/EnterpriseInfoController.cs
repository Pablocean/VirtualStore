using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;

namespace VirtualStore.API.Controllers;

/// <summary>Public enterprise information with admin-only updates.</summary>
[ApiController]
[Route("api/enterprise-info")]
public class EnterpriseInfoController : ControllerBase
{
    private readonly IEnterpriseInfoService _enterpriseInfoService;

    public EnterpriseInfoController(IEnterpriseInfoService enterpriseInfoService)
    {
        _enterpriseInfoService = enterpriseInfoService ?? throw new ArgumentNullException(nameof(enterpriseInfoService));
    }

    /// <summary>Gets the public enterprise information.</summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(EnterpriseInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEnterpriseInfo(CancellationToken cancellationToken)
    {
        var info = await _enterpriseInfoService.GetEnterpriseInfoAsync(cancellationToken);
        if (info is null) return NotFound();
        return Ok(info);
    }

    /// <summary>Creates or updates the enterprise information. Admins only.</summary>
    [HttpPut]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(EnterpriseInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateEnterpriseInfo([FromBody] UpdateEnterpriseInfoDto dto, CancellationToken cancellationToken)
    {
        var info = await _enterpriseInfoService.UpdateEnterpriseInfoAsync(dto, cancellationToken);
        return Ok(info);
    }
}

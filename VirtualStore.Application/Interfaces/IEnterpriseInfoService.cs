using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Interfaces;

public interface IEnterpriseInfoService
{
    Task<EnterpriseInfoDto?> GetEnterpriseInfoAsync();
    Task<EnterpriseInfoDto> UpdateEnterpriseInfoAsync(UpdateEnterpriseInfoDto dto);
}
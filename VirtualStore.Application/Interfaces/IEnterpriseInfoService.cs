using VirtualStore.Application.DTOs;

namespace VirtualStore.Application.Interfaces;

public interface IEnterpriseInfoService
{
    Task<EnterpriseInfoDto?> GetEnterpriseInfoAsync(CancellationToken cancellationToken = default);
    Task<EnterpriseInfoDto> UpdateEnterpriseInfoAsync(UpdateEnterpriseInfoDto dto, CancellationToken cancellationToken = default);
}

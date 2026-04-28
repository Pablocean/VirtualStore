using AutoMapper;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.Services;

public class EnterpriseInfoService : IEnterpriseInfoService
{
    private readonly IRepository<EnterpriseInfo> _infoRepo;
    private readonly IMapper _mapper;

    public EnterpriseInfoService(IRepository<EnterpriseInfo> infoRepo, IMapper mapper)
    {
        _infoRepo = infoRepo;
        _mapper = mapper;
    }

    public async Task<EnterpriseInfoDto?> GetEnterpriseInfoAsync()
    {
        var info = await _infoRepo.GetAllAsync();
        var entity = info.FirstOrDefault(); // singleton: always first
        return entity == null ? null : _mapper.Map<EnterpriseInfoDto>(entity);
    }

    public async Task<EnterpriseInfoDto> UpdateEnterpriseInfoAsync(UpdateEnterpriseInfoDto dto)
    {
        var infoList = await _infoRepo.GetAllAsync();
        var info = infoList.FirstOrDefault();
        if (info == null)
        {
            info = new EnterpriseInfo();
            _mapper.Map(dto, info);
            await _infoRepo.AddAsync(info);
        }
        else
        {
            _mapper.Map(dto, info);
            await _infoRepo.UpdateAsync(info.Id, info);
        }
        return _mapper.Map<EnterpriseInfoDto>(info);
    }
}
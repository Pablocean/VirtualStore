using AutoMapper;
using VirtualStore.Application.DTOs;
using VirtualStore.Application.Interfaces;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.Services;

public class EnterpriseInfoService : IEnterpriseInfoService
{
    private const string CacheKey = "enterprise:info";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly IRepository<EnterpriseInfo> _infoRepo;
    private readonly IMapper _mapper;
    private readonly ICacheService _cache;

    public EnterpriseInfoService(IRepository<EnterpriseInfo> infoRepo, IMapper mapper, ICacheService cache)
    {
        _infoRepo = infoRepo;
        _mapper = mapper;
        _cache = cache;
    }

    public async Task<EnterpriseInfoDto?> GetEnterpriseInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGet<EnterpriseInfoDto>(CacheKey, out var cached) && cached is not null)
            return cached;

        var info = await _infoRepo.GetAllAsync();
        var entity = info.FirstOrDefault(); // singleton: always first
        if (entity == null) return null; // Nulls are not cached.

        var dto = _mapper.Map<EnterpriseInfoDto>(entity);
        _cache.Set(CacheKey, dto, CacheLifetime);
        return dto;
    }

    public async Task<EnterpriseInfoDto> UpdateEnterpriseInfoAsync(UpdateEnterpriseInfoDto dto, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        _cache.Remove(CacheKey);
        return _mapper.Map<EnterpriseInfoDto>(info);
    }
}

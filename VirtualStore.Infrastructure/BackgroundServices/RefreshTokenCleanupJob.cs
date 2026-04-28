using Microsoft.Extensions.Logging;
using Quartz;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.BackgroundServices;

[DisallowConcurrentExecution]
public class RefreshTokenCleanupJob : IJob
{
    private readonly IRepository<User> _userRepository;
    private readonly ILogger<RefreshTokenCleanupJob> _logger;

    public RefreshTokenCleanupJob(IRepository<User> userRepository, ILogger<RefreshTokenCleanupJob> logger)
    {
        _userRepository = userRepository;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting refresh token cleanup.");
        var users = await _userRepository.FindAsync(u => u.RefreshTokens.Any(rt => rt.Expires < DateTime.UtcNow));
        int totalRevoked = 0;
        foreach (var user in users)
        {
            var revoked = user.RefreshTokens.RemoveAll(rt => rt.Expires < DateTime.UtcNow && rt.Revoked == null);
            if (revoked > 0)
            {
                user.UpdatedAt = DateTime.UtcNow;
                await _userRepository.UpdateAsync(user.Id, user);
                totalRevoked += revoked;
            }
        }
        _logger.LogInformation("Removed {Count} expired refresh tokens.", totalRevoked);
    }
}
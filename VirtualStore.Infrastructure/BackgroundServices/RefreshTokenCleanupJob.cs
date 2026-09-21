using Microsoft.Extensions.Logging;
using Quartz;
using VirtualStore.Application.Common;
using VirtualStore.Domain.Entities;
using VirtualStore.Domain.Interfaces;

namespace VirtualStore.Infrastructure.BackgroundServices;

/// <summary>
/// Nightly refresh-token retention sweep (ADR-0010).
///
/// Purge rule per token (evaluated at run time <c>now</c>, forensics cutoff
/// <c>now - 90d</c>):
/// <list type="bullet">
///   <item>Expired AND never-revoked → purge (pre-existing behavior).</item>
///   <item>Expired AND revoked with <c>Revoked</c> older than 90d → purge.</item>
///   <item>Revoked within the 90-day forensics window → KEEP (reuse-detection
///   evidence + audit trail).</item>
/// </list>
///
/// Retry story: the job is idempotent (re-running only removes tokens that
/// still match the rule), so a failed batch is retried by the next-night run.
/// One bad document never aborts the run — each page is wrapped in try/catch
/// and failures are logged with the page number. The Quartz trigger uses
/// <c>WithMisfireHandlingInstructionDoNothing</c>: a missed 03:00 firing is
/// skipped and the next night catches up (same benign-miss semantics as before).
/// </summary>
[DisallowConcurrentExecution]
public class RefreshTokenCleanupJob : IJob
{
    /// <summary>
    /// Revoked-but-expired tokens are kept this long for forensics (reuse
    /// detection evidence). After the window they are purged with everything
    /// else expired.
    /// </summary>
    public static readonly TimeSpan RevokedForensicsRetention = TimeSpan.FromDays(90);

    private const int PageSize = 100;

    private readonly IRepository<User> _userRepository;
    private readonly ILogger<RefreshTokenCleanupJob> _logger;
    private readonly IDateTimeProvider _clock;

    public RefreshTokenCleanupJob(
        IRepository<User> userRepository,
        ILogger<RefreshTokenCleanupJob> logger,
        IDateTimeProvider? clock = null)
    {
        _userRepository = userRepository;
        _logger = logger;
        _clock = clock ?? new SystemDateTimeProvider();
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting refresh token cleanup.");
        var now = _clock.UtcNow;
        var forensicsCutoff = now - RevokedForensicsRetention;

        int totalPurged = 0;
        int usersTouched = 0;
        int page = 1;

        while (true)
        {
            IReadOnlyList<User> items;
            try
            {
                // Dirty-token prefilter (translatable to Mongo): any expired token.
                // The exact purge rule is applied in memory per user below.
                var (paged, _) = await _userRepository.PagedAsync(
                    u => u.RefreshTokens.Any(rt => rt.Expires < now),
                    page,
                    PageSize,
                    sortBy: null,
                    desc: true,
                    context.CancellationToken);
                items = paged;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refresh token cleanup: failed to read page {Page}; continuing with next-night retry.", page);
                break;
            }

            if (items.Count == 0)
                break;

            foreach (var user in items)
            {
                try
                {
                    var removed = PurgeExpiredTokens(user, now, forensicsCutoff);
                    if (removed > 0)
                    {
                        user.UpdatedAt = now;
                        await _userRepository.UpdateAsync(user.Id, user, context.CancellationToken);
                        totalPurged += removed;
                        usersTouched++;
                    }
                }
                catch (Exception ex)
                {
                    // One bad doc must not abort the run; the next-night run retries it.
                    _logger.LogWarning(ex, "Refresh token cleanup: failed to purge tokens for user {UserId}; will retry next run.", user.Id);
                }
            }

            if (items.Count < PageSize)
                break;
            page++;
        }

        _logger.LogInformation("Removed {Count} expired refresh tokens across {Users} users.", totalPurged, usersTouched);
    }

    /// <summary>
    /// Removes purgeable tokens from <paramref name="user"/> in memory and
    /// returns the number removed. Pure function of (user, now, cutoff) —
    /// unit-tested without Mongo.
    /// </summary>
    public static int PurgeExpiredTokens(User user, DateTime now, DateTime forensicsCutoff)
        => user.RefreshTokens.RemoveAll(rt =>
            rt.Expires < now &&
            (rt.Revoked == null || rt.Revoked < forensicsCutoff));
}

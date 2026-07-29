using System.Threading.RateLimiting;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Static factory for creating <see cref="TokenBucketRateLimiter"/> instances.
/// Centralizes rate limiter construction previously duplicated between
/// <see cref="DispatchService"/> and <see cref="ConsolidationDispatchHandler"/>.
/// </summary>
internal static class RateLimiterFactory
{
    /// <summary>
    /// Creates a <see cref="TokenBucketRateLimiter"/> configured with the given rate limit,
    /// 1-second replenishment period, and no queuing.
    /// </summary>
    /// <param name="rateLimitPerSecond">Maximum tokens (job creations) per second.</param>
    public static TokenBucketRateLimiter CreateTokenBucket(int rateLimitPerSecond) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = rateLimitPerSecond,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        TokensPerPeriod = rateLimitPerSecond,
        AutoReplenishment = true
    });
}
using System.Threading.RateLimiting;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Shared factory for creating <see cref="TokenBucketRateLimiter"/> instances
/// configured from a rate-limit-per-second parameter.
/// Eliminates the duplicated CreateRateLimiter logic across DispatchService
/// and ConsolidationDispatchHandler.
/// </summary>
public static class RateLimiterFactory
{
    /// <summary>
    /// Creates a <see cref="TokenBucketRateLimiter"/> with the specified per-second token limit.
    /// Uses OldestFirst queue ordering, no queue, 1-second replenishment period, and auto-replenishment.
    /// </summary>
    /// <param name="rateLimitPerSecond">Maximum tokens (jobs) per second. Must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="rateLimitPerSecond"/> is zero or negative.</exception>
    public static TokenBucketRateLimiter CreateTokenBucket(int rateLimitPerSecond)
    {
        if (rateLimitPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(rateLimitPerSecond), rateLimitPerSecond, "Rate limit must be greater than zero.");

        return new(new TokenBucketRateLimiterOptions
        {
            TokenLimit = rateLimitPerSecond,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = rateLimitPerSecond,
            AutoReplenishment = true
        });
    }
}
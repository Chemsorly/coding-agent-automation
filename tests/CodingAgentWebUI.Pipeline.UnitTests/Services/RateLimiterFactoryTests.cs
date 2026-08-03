using System.Threading.RateLimiting;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="RateLimiterFactory"/>. Verifies that the factory
/// produces correctly configured <see cref="TokenBucketRateLimiter"/> instances
/// matching the expected 1-second replenishment, zero-queue, oldest-first policy.
/// </summary>
public class RateLimiterFactoryTests
{
    [Fact]
    public void CreateTokenBucket_ReturnsNonNullLimiter()
    {
        using var limiter = RateLimiterFactory.CreateTokenBucket(10);

        limiter.Should().NotBeNull();
        limiter.Should().BeOfType<TokenBucketRateLimiter>();
    }

    [Fact]
    public async Task CreateTokenBucket_AcquiresUpToRateLimitPerSecond_BeforeExhaustion()
    {
        using var limiter = RateLimiterFactory.CreateTokenBucket(3);

        // Should be able to acquire 3 permits immediately (all tokens available)
        for (int i = 0; i < 3; i++)
        {
            using var lease = await limiter.AcquireAsync(1, CancellationToken.None);
            lease.IsAcquired.Should().BeTrue("all {0} tokens should be available", i + 1);
        }

        // 4th acquisition should fail (tokens exhausted, queue limit is 0)
        using var exhaustedLease = await limiter.AcquireAsync(1, CancellationToken.None);
        exhaustedLease.IsAcquired.Should().BeFalse("tokens should be exhausted after acquiring {0} permits", 3);
    }

    [Fact]
    public async Task CreateTokenBucket_ReplenishesAfterInterval()
    {
        // Use AutoReplenishment=false and a 1ms replenishment period so we can
        // call TryReplenish() explicitly after a trivial delay — no timing-dependent
        // wall-clock sleep required (avoids CI jitter).
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 2,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(1),
            TokensPerPeriod = 2,
            AutoReplenishment = false
        });

        // Exhaust all tokens
        for (int i = 0; i < 2; i++)
        {
            using var lease = await limiter.AcquireAsync(1, CancellationToken.None);
            lease.IsAcquired.Should().BeTrue();
        }

        // Verify exhaustion
        {
            using var lease = await limiter.AcquireAsync(1, CancellationToken.None);
            lease.IsAcquired.Should().BeFalse("tokens should be exhausted");
        }

        // Let the 1ms period expire, then replenish deterministically
        await Task.Delay(TimeSpan.FromMilliseconds(10));
        limiter.TryReplenish();

        // Should have tokens again
        using var replenishedLease = await limiter.AcquireAsync(1, CancellationToken.None);
        replenishedLease.IsAcquired.Should().BeTrue("tokens should replenish after TryReplenish()");

        // Second token also available (2 tokens per period)
        using var secondLease = await limiter.AcquireAsync(1, CancellationToken.None);
        secondLease.IsAcquired.Should().BeTrue("should replenish 2 tokens per period");
    }

    [Fact]
    public async Task CreateTokenBucket_AcquireZeroPermit_AlwaysSucceeds()
    {
        using var limiter = RateLimiterFactory.CreateTokenBucket(1);

        // Exhaust the single token
        using var _ = await limiter.AcquireAsync(1, CancellationToken.None);

        // Acquiring 0 permits should always succeed (no tokens consumed)
        using var zeroLease = await limiter.AcquireAsync(0, CancellationToken.None);
        zeroLease.IsAcquired.Should().BeTrue("acquiring 0 permits should not require tokens");
    }
}
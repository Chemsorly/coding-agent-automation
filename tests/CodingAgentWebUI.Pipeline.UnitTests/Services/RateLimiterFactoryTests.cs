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
        using var limiter = RateLimiterFactory.CreateTokenBucket(2);

        // Exhaust all tokens
        for (int i = 0; i < 2; i++)
        {
            using var lease = await limiter.AcquireAsync(1, CancellationToken.None);
            lease.IsAcquired.Should().BeTrue();
        }

        // Verify exhaustion
        {
            using var lease = await limiter.AcquireAsync(1, CancellationToken.None);
            lease.IsAcquired.Should().BeFalse();
        }

        // Wait for replenishment (1 second period)
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        // Should have tokens again
        using var replenishedLease = await limiter.AcquireAsync(1, CancellationToken.None);
        replenishedLease.IsAcquired.Should().BeTrue("tokens should replenish after 1-second period");

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
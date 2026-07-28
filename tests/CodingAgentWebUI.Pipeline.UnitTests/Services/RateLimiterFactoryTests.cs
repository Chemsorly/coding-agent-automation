using System.Threading.RateLimiting;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="RateLimiterFactory"/>.
/// Validates that CreateTokenBucket produces correctly configured TokenBucketRateLimiter instances
/// and that the limiter actually enforces rate limits.
/// Issue #1731: eliminates duplicated CreateRateLimiter across DispatchService and ConsolidationDispatchHandler.
/// </summary>
public class RateLimiterFactoryTests
{
    [Fact]
    public void CreateTokenBucket_ConfiguresAllOptionsCorrectly()
    {
        using var limiter = RateLimiterFactory.CreateTokenBucket(5);

        var stats = limiter.GetStatistics()!;
        stats.CurrentAvailablePermits.Should().Be(5);
        stats.CurrentQueuedCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateTokenBucket_WithRateLimit1_RejectsSecondAcquisitionWithinSameSecond()
    {
        using var limiter = RateLimiterFactory.CreateTokenBucket(1);

        using var first = await limiter.AcquireAsync(1);
        first.IsAcquired.Should().BeTrue();

        using var second = await limiter.AcquireAsync(1);
        second.IsAcquired.Should().BeFalse();
    }

    [Fact]
    public async Task CreateTokenBucket_WithRateLimit2_AllowsTwoAcquisitions()
    {
        using var limiter = RateLimiterFactory.CreateTokenBucket(2);

        using var first = await limiter.AcquireAsync(1);
        first.IsAcquired.Should().BeTrue();

        using var second = await limiter.AcquireAsync(1);
        second.IsAcquired.Should().BeTrue();
    }

    [Fact]
    public void CreateTokenBucket_ZeroRateLimit_ThrowsArgumentOutOfRangeException()
    {
        var ex = Record.Exception(() => RateLimiterFactory.CreateTokenBucket(0));
        ex.Should().BeOfType<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("rateLimitPerSecond");
    }

    [Fact]
    public void CreateTokenBucket_NegativeRateLimit_ThrowsArgumentOutOfRangeException()
    {
        var ex = Record.Exception(() => RateLimiterFactory.CreateTokenBucket(-1));
        ex.Should().BeOfType<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("rateLimitPerSecond");
    }
}
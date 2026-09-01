using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="InfiniteRetryPolicy"/> — the SignalR reconnection backoff policy
/// used by <see cref="AgentHubConnection"/>.
///
/// The policy implements: delay = 2^min(retryCount, 7) seconds + jitter [0, 1000) ms,
/// capped at 120 s total (applied after jitter).
/// </summary>
public class InfiniteRetryPolicyTests
{
    private static readonly InfiniteRetryPolicy _policy = new();

    private static RetryContext MakeContext(int previousRetryCount) =>
        new()
        {
            PreviousRetryCount = previousRetryCount,
            ElapsedTime = TimeSpan.Zero,
            RetryReason = null
        };

    // ── Policy never gives up ─────────────────────────────────────────────────

    [Fact]
    public void NextRetryDelay_AnyRetryCount_NeverReturnsNull()
    {
        // InfiniteRetryPolicy must always return a non-null delay (never gives up).
        foreach (var count in new[] { 0, 1, 5, 7, 8, 100, int.MaxValue })
        {
            _policy.NextRetryDelay(MakeContext(count))
                .Should().NotBeNull($"policy must never give up (retryCount={count})");
        }
    }

    // ── Backoff: 2^min(n, 7) seconds base ────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]    // 2^0 = 1 s
    [InlineData(1, 2)]    // 2^1 = 2 s
    [InlineData(2, 4)]    // 2^2 = 4 s
    [InlineData(3, 8)]    // 2^3 = 8 s
    [InlineData(4, 16)]   // 2^4 = 16 s
    [InlineData(5, 32)]   // 2^5 = 32 s
    [InlineData(6, 64)]   // 2^6 = 64 s
    [InlineData(7, 128)]  // 2^7 = 128 s base, but capped to 120 s before jitter
    [InlineData(8, 128)]  // exponent clamped at 7 → same as above
    [InlineData(100, 128)] // high count still clamped
    public void NextRetryDelay_RetryCount_YieldsExpectedBase(int retryCount, int expectedBaseSeconds)
    {
        var delay = _policy.NextRetryDelay(MakeContext(retryCount))!.Value;

        // Base is capped at 120 s before jitter is added. Jitter is [0, 1000) ms.
        // So effective base for the assertion is min(expectedBaseSeconds, 120).
        var effectiveBase = Math.Min(expectedBaseSeconds * 1000, 120_000);
        var expectedMinMs = effectiveBase;               // jitter never subtracts
        var expectedMaxMs = effectiveBase + 1000;        // cap + up to 1 s jitter

        delay.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(expectedMinMs,
            $"retryCount={retryCount}: delay must be at least effective-base {effectiveBase / 1000}s");
        delay.TotalMilliseconds.Should().BeLessThanOrEqualTo(expectedMaxMs,
            $"retryCount={retryCount}: delay must not exceed effective-base + 1 s jitter");
    }

    // ── Cap at 120 s ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(1000)]
    public void NextRetryDelay_HighRetryCount_NeverExceeds121Seconds(int retryCount)
    {
        // The policy caps the BASE delay at 120 s, then adds up to 1000 ms of jitter.
        // Final maximum = 120 s + 1000 ms = 121 s.
        var delay = _policy.NextRetryDelay(MakeContext(retryCount))!.Value;
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(121_000),
            $"retryCount={retryCount}: cap + jitter must not exceed 121 s");
    }

    // ── Jitter is non-negative (additive) ────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void NextRetryDelay_Jitter_IsAlwaysNonNegative(int retryCount)
    {
        // The base delay without jitter is 2^min(n,7) seconds. The delay must be >= base,
        // confirming jitter is additive (never subtracts from the base).
        var delay = _policy.NextRetryDelay(MakeContext(retryCount))!.Value;
        var baseSeconds = Math.Pow(2, Math.Min(retryCount, 7));
        delay.TotalSeconds.Should().BeGreaterThanOrEqualTo(baseSeconds,
            $"retryCount={retryCount}: jitter must be non-negative");
    }

    // ── Property: delay always in [2^min(n,7) s, 120 s] ─────────────────────

    [Property(MaxTest = 200)]
    public bool ForAllRetryCount_DelayWithinExpectedBounds(int rawCount)
    {
        // Use absolute value to get a non-negative count, bound to [0, 1000]
        var retryCount = Math.Abs(rawCount % 1000);
        var delay = _policy.NextRetryDelay(MakeContext(retryCount))!.Value;

        // Base is capped at 120 s, then jitter [0, 1000) ms is added.
        // Min = min(2^min(n,7), 120) s; Max = min(2^min(n,7), 120) s + 1000 ms.
        var baseMs = Math.Min(Math.Pow(2, Math.Min(retryCount, 7)) * 1000, 120_000);
        var minExpected = baseMs;
        var maxExpected = baseMs + 1_000.0;

        return delay.TotalMilliseconds >= minExpected
            && delay.TotalMilliseconds <= maxExpected;
    }
}

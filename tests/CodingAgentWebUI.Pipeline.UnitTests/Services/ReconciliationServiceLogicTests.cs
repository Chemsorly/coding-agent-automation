using CodingAgentWebUI.Orchestration.Dispatch;
using AwesomeAssertions;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for ReconciliationService's static helper methods:
/// - Timeout detection (IsTimedOut)
/// - Stale cleanup threshold (IsStale)
/// </summary>
public class ReconciliationServiceLogicTests
{
    // ── Timeout Detection ────────────────────────────────────────────────

    [Fact]
    public void IsTimedOut_ReturnsFalse_WhenTimeoutNotElapsed()
    {
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var timeoutSeconds = 600; // 10 minutes
        var now = DateTimeOffset.UtcNow;

        ReconciliationService.IsTimedOut(dispatchedAt, timeoutSeconds, now)
            .Should().BeFalse();
    }

    [Fact]
    public void IsTimedOut_ReturnsTrue_WhenTimeoutElapsed()
    {
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        var timeoutSeconds = 600; // 10 minutes
        var now = DateTimeOffset.UtcNow;

        ReconciliationService.IsTimedOut(dispatchedAt, timeoutSeconds, now)
            .Should().BeTrue();
    }

    [Fact]
    public void IsTimedOut_ReturnsTrue_ExactlyAtBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddSeconds(-300);
        var timeoutSeconds = 300;

        ReconciliationService.IsTimedOut(dispatchedAt, timeoutSeconds, now)
            .Should().BeTrue();
    }

    [Fact]
    public void IsTimedOut_ReturnsFalse_OneSecondBeforeBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddSeconds(-299);
        var timeoutSeconds = 300;

        ReconciliationService.IsTimedOut(dispatchedAt, timeoutSeconds, now)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(0, true)]   // zero timeout → always timed out
    [InlineData(1, true)]   // 1 second timeout, 10s elapsed
    [InlineData(100, true)] // 100s timeout, 200s elapsed
    public void IsTimedOut_VariousTimeouts_WhenElapsed(int timeoutSeconds, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddSeconds(-(timeoutSeconds + 100));

        ReconciliationService.IsTimedOut(dispatchedAt, timeoutSeconds, now)
            .Should().Be(expected);
    }

    [Fact]
    public void IsTimedOut_HandlesLargeTimeout()
    {
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddHours(-1);
        var timeoutSeconds = 7200; // 2 hours

        ReconciliationService.IsTimedOut(dispatchedAt, timeoutSeconds, now)
            .Should().BeFalse();
    }

    // ── Dispatch-anchored Timeout (Bug Fix) ──────────────────────────────

    [Fact]
    public void IsTimedOut_UsesDispatchTime_NotCreationTime()
    {
        // Scenario: Item created 2h ago but dispatched only 5 minutes ago.
        // With a 7200s timeout, it should NOT be timed out — execution just started.
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddMinutes(-5); // dispatched 5 min ago

        ReconciliationService.IsTimedOut(dispatchedAt, 7200, now)
            .Should().BeFalse("timeout should measure from dispatch, not creation");
    }

    [Fact]
    public void IsTimedOut_ReturnsTrue_WhenDispatchTimeExceedsTimeout()
    {
        // Scenario: Item dispatched 3h ago with 2h timeout — should be timed out.
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddHours(-3);

        ReconciliationService.IsTimedOut(dispatchedAt, 7200, now)
            .Should().BeTrue("3h elapsed since dispatch exceeds 2h timeout");
    }

    [Fact]
    public void IsTimedOut_QueuedItemDispatchedJustNow_ShouldNotTimeout()
    {
        // Regression: Item sat in queue for 4h, then dispatched now.
        // Should NOT timeout even though creation was 4h ago.
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now; // just dispatched

        ReconciliationService.IsTimedOut(dispatchedAt, 7200, now)
            .Should().BeFalse("freshly dispatched item must not timeout regardless of queue time");
    }

    // ── Canary Invariant: Minimum Execution Age ─────────────────────────

    [Fact]
    public void ShouldEnforceTimeout_ReturnsFalse_WhenExecutionTimeBelowMinimum()
    {
        // Canary: if an item appears timed out but has been executing for < 60s,
        // something is wrong with the timestamp — refuse to kill it.
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddSeconds(-30); // only 30s of execution

        ReconciliationService.ShouldEnforceTimeout(dispatchedAt, timeoutSeconds: 10, now)
            .Should().BeFalse("canary: execution time (30s) below minimum plausible threshold for a real timeout");
    }

    [Fact]
    public void ShouldEnforceTimeout_ReturnsTrue_WhenExecutionTimeAboveMinimumAndTimedOut()
    {
        // Normal timeout: item has been running for 2.5h with a 2h timeout.
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddHours(-2.5);

        ReconciliationService.ShouldEnforceTimeout(dispatchedAt, timeoutSeconds: 7200, now)
            .Should().BeTrue("legitimate timeout: 2.5h execution exceeds 2h limit");
    }

    [Fact]
    public void ShouldEnforceTimeout_ReturnsFalse_WhenNotTimedOut()
    {
        // Not timed out at all — should not enforce regardless.
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddMinutes(-30);

        ReconciliationService.ShouldEnforceTimeout(dispatchedAt, timeoutSeconds: 7200, now)
            .Should().BeFalse("not timed out yet");
    }

    [Fact]
    public void ShouldEnforceTimeout_ReturnsFalse_WhenTimeoutIs10s_ButExecutionOnly5s()
    {
        // Edge case: very short timeout configured, but execution time even shorter.
        // This catches "timeout computed from wrong anchor" bugs.
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddSeconds(-5);

        ReconciliationService.ShouldEnforceTimeout(dispatchedAt, timeoutSeconds: 3, now)
            .Should().BeFalse("canary: execution time (5s) below minimum plausible threshold");
    }

    [Fact]
    public void ShouldEnforceTimeout_ReturnsTrue_WhenTimeoutAndExecutionBothExceedMinimum()
    {
        // Short timeout (5 min) with sufficient execution (10 min) — valid timeout.
        var now = DateTimeOffset.UtcNow;
        var dispatchedAt = now.AddMinutes(-10);

        ReconciliationService.ShouldEnforceTimeout(dispatchedAt, timeoutSeconds: 300, now)
            .Should().BeTrue("10 min execution exceeds 5 min timeout and exceeds minimum threshold");
    }

    // ── Stale Cleanup Threshold ──────────────────────────────────────────

    [Fact]
    public void IsStale_ReturnsFalse_WhenCompletedAtNull()
    {
        ReconciliationService.IsStale(null, 7, DateTimeOffset.UtcNow)
            .Should().BeFalse();
    }

    [Fact]
    public void IsStale_ReturnsFalse_WhenWithinRetentionPeriod()
    {
        var now = DateTimeOffset.UtcNow;
        var completedAt = now.AddDays(-3);

        ReconciliationService.IsStale(completedAt, 7, now)
            .Should().BeFalse();
    }

    [Fact]
    public void IsStale_ReturnsTrue_WhenBeyondRetentionPeriod()
    {
        var now = DateTimeOffset.UtcNow;
        var completedAt = now.AddDays(-10);

        ReconciliationService.IsStale(completedAt, 7, now)
            .Should().BeTrue();
    }

    [Fact]
    public void IsStale_ReturnsTrue_ExactlyAtRetentionBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        var completedAt = now.AddDays(-7);

        ReconciliationService.IsStale(completedAt, 7, now)
            .Should().BeTrue();
    }

    [Fact]
    public void IsStale_ReturnsFalse_OneSecondBeforeRetention()
    {
        var now = DateTimeOffset.UtcNow;
        var completedAt = now.AddDays(-7).AddSeconds(1);

        ReconciliationService.IsStale(completedAt, 7, now)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 2, true)]   // 1 day retention, completed 2 days ago
    [InlineData(30, 31, true)] // 30 day retention, completed 31 days ago
    [InlineData(90, 89, false)] // 90 day retention, completed 89 days ago
    [InlineData(90, 91, true)]  // 90 day retention, completed 91 days ago
    public void IsStale_VariousRetentionPeriods(int retentionDays, int daysAgo, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var completedAt = now.AddDays(-daysAgo);

        ReconciliationService.IsStale(completedAt, retentionDays, now)
            .Should().Be(expected);
    }
}

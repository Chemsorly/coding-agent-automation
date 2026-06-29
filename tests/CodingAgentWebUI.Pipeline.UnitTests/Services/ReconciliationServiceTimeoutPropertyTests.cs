using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using FsCheck;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for ReconciliationService timeout enforcement logic.
/// **Validates: Requirements 7.3**
/// </summary>
public class ReconciliationServiceTimeoutPropertyTests
{
    /// <summary>
    /// Property 10: Timeout Enforcement
    /// For any work item in Dispatched/Running state with random CreatedAt and TimeoutSeconds,
    /// IsTimedOut returns true if and only if elapsed time >= TimeoutSeconds.
    /// </summary>
    [Property]
    public bool TimeoutDetection_MatchesElapsedVsTimeoutSeconds(PositiveInt timeoutSecondsRaw, PositiveInt elapsedSecondsRaw)
    {
        var timeoutSeconds = timeoutSecondsRaw.Get % 86400 + 1; // 1..86400 seconds (up to 24h)
        var elapsedSeconds = elapsedSecondsRaw.Get % 172800;     // 0..172800 seconds (up to 48h)

        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddSeconds(-elapsedSeconds);

        var result = ReconciliationService.IsTimedOut(createdAt, timeoutSeconds, now);
        var expected = elapsedSeconds >= timeoutSeconds;

        return result == expected;
    }

    /// <summary>
    /// Property 10 (supplementary): A work item created exactly at timeout boundary is timed out.
    /// For any positive timeout, now == createdAt + timeoutSeconds → IsTimedOut returns true.
    /// </summary>
    [Property]
    public bool TimeoutDetection_ExactBoundary_AlwaysTimedOut(PositiveInt timeoutSecondsRaw)
    {
        var timeoutSeconds = timeoutSecondsRaw.Get % 86400 + 1;
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddSeconds(-timeoutSeconds);

        return ReconciliationService.IsTimedOut(createdAt, timeoutSeconds, now);
    }

    /// <summary>
    /// Property 10 (supplementary): A work item created 1 second before timeout is NOT timed out.
    /// For any timeout > 1, elapsed == timeoutSeconds - 1 → IsTimedOut returns false.
    /// </summary>
    [Property]
    public bool TimeoutDetection_OneSecondBeforeBoundary_NotTimedOut(PositiveInt timeoutSecondsRaw)
    {
        var timeoutSeconds = timeoutSecondsRaw.Get % 86400 + 2; // minimum 2 to allow -1
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddSeconds(-(timeoutSeconds - 1));

        return !ReconciliationService.IsTimedOut(createdAt, timeoutSeconds, now);
    }
}

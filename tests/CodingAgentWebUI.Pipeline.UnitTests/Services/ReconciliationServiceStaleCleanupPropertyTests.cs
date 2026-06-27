using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using FsCheck;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for ReconciliationService stale work item cleanup logic.
/// **Validates: Requirements 7.4**
/// </summary>
public class ReconciliationServiceStaleCleanupPropertyTests
{
    /// <summary>
    /// Property 11: Stale Work Item Cleanup
    /// For any terminal work item with a random CompletedAt and retention period,
    /// IsStale returns true if and only if age >= retentionDays.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool StaleCleanup_MatchesAgeVsRetentionPeriod(PositiveInt retentionDaysRaw, PositiveInt elapsedDaysRaw)
    {
        var retentionDays = retentionDaysRaw.Get % 365 + 1; // 1..365 days
        var elapsedDays = elapsedDaysRaw.Get % 730;          // 0..730 days (up to 2 years)

        var now = DateTimeOffset.UtcNow;
        var completedAt = now.AddDays(-elapsedDays);

        var result = ReconciliationService.IsStale(completedAt, retentionDays, now);
        var expected = elapsedDays >= retentionDays;

        return result == expected;
    }

    /// <summary>
    /// Property 11 (supplementary): A work item completed exactly at retention boundary is stale.
    /// For any positive retention period, now == completedAt + retentionDays → IsStale returns true.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool StaleCleanup_ExactBoundary_AlwaysStale(PositiveInt retentionDaysRaw)
    {
        var retentionDays = retentionDaysRaw.Get % 365 + 1;
        var now = DateTimeOffset.UtcNow;
        var completedAt = now.AddDays(-retentionDays);

        return ReconciliationService.IsStale(completedAt, retentionDays, now);
    }

    /// <summary>
    /// Property 11 (supplementary): A null CompletedAt is never stale.
    /// Terminal items without CompletedAt should not be cleaned up.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool StaleCleanup_NullCompletedAt_NeverStale(PositiveInt retentionDaysRaw)
    {
        var retentionDays = retentionDaysRaw.Get % 365 + 1;
        var now = DateTimeOffset.UtcNow;

        return !ReconciliationService.IsStale(null, retentionDays, now);
    }

    /// <summary>
    /// Property 11 (supplementary): A work item completed 1 second before retention is NOT stale.
    /// For any retention > 0, age just under retentionDays → IsStale returns false.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool StaleCleanup_OneSecondBeforeBoundary_NotStale(PositiveInt retentionDaysRaw)
    {
        var retentionDays = retentionDaysRaw.Get % 365 + 1;
        var now = DateTimeOffset.UtcNow;
        var completedAt = now.AddDays(-retentionDays).AddSeconds(1);

        return !ReconciliationService.IsStale(completedAt, retentionDays, now);
    }
}

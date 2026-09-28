using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Unit tests for <see cref="InsightsBucketer.BuildBuckets"/>.
/// All tests inject a fixed <c>now</c> so results are deterministic regardless of wall-clock time.
/// </summary>
public class InsightsBucketerTests
{
    // Fixed reference point: 2026-09-23 15:30:00 UTC
    // windowEnd for hourly windows = 15:00 UTC (truncated to current hour).
    // Trailing partial bucket = [15:00, 15:30).
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 15, 30, 0, TimeSpan.Zero);

    // Fixed reference point at an exact hour boundary (no trailing bucket should be added).
    private static readonly DateTimeOffset NowOnHourBoundary = new(2026, 9, 23, 15, 0, 0, TimeSpan.Zero);

    // ── helpers ──────────────────────────────────────────────────────────

    private static PipelineRunSummary MakeRun(DateTimeOffset startedAt, PipelineStep finalStep = PipelineStep.Completed)
        => new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = (IssueIdentifier)"owner/repo#1",
            IssueTitle = "Test run",
            FinalStep = finalStep,
            StartedAtOffset = startedAt,
        };

    // ── Empty input ───────────────────────────────────────────────────────

    /// <summary>Empty input produces an empty list regardless of window.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(24)]
    [InlineData(168)]
    public void BuildBuckets_EmptyInput_ReturnsEmptyList(int windowHours)
    {
        var result = InsightsBucketer.BuildBuckets([], windowHours, Now);

        result.Should().BeEmpty("empty input must produce no buckets for any window value");
    }

    // ── 1-hour window ─────────────────────────────────────────────────────

    /// <summary>
    /// 1h window with now=15:30 → 2 buckets: [14:00,15:00) plus trailing [15:00,15:30).
    /// A run at 14:30 is counted in the first bucket.
    /// </summary>
    [Fact]
    public void BuildBuckets_1hWindow_Returns1HourlyBucket()
    {
        // now = 15:30 UTC. Regular bucket: [14:00, 15:00). Trailing: [15:00, 15:30).
        var run = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 30, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run], windowHours: 1, Now);

        result.Should().HaveCount(2, "1h window produces 1 regular bucket plus 1 trailing partial bucket");
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero),
            "the first bucket SlotStart must be 1 hour before the truncated-to-hour now");
        result[0].Succeeded.Should().Be(1, "the run at 14:30 must be counted in the [14:00,15:00) bucket");
        result[0].Total.Should().Be(1);
        // Trailing bucket: [15:00, 15:30) — the run at 14:30 is NOT in it.
        result[1].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 15, 0, 0, TimeSpan.Zero),
            "the trailing bucket starts at the current hour start (windowEnd)");
        result[1].Total.Should().Be(0, "the run at 14:30 is not in the trailing [15:00,15:30) bucket");
    }

    /// <summary>A run started before the 1h window start is excluded from all buckets.</summary>
    [Fact]
    public void BuildBuckets_1hWindow_RunOutsideWindow_IsExcluded()
    {
        // now = 15:30; regular bucket covers [14:00, 15:00). A run at 13:59 is outside.
        var run = MakeRun(new DateTimeOffset(2026, 9, 23, 13, 59, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run], windowHours: 1, Now);

        result.Should().HaveCount(2, "1h window produces 2 buckets even when all runs are outside");
        result.Sum(b => b.Total).Should().Be(0, "run started before the 1h window start must not be counted");
    }

    /// <summary>
    /// A run started in the current partial hour (>= windowEnd, &lt; now) must appear
    /// in the trailing bucket and count in the total.
    /// </summary>
    [Fact]
    public void BuildBuckets_1hWindow_TrailingPartialBucket_CountsCurrentHourRuns()
    {
        // now = 15:30. Run at 15:20 is inside [15:00, 15:30).
        var run = MakeRun(new DateTimeOffset(2026, 9, 23, 15, 20, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run], windowHours: 1, Now);

        result.Should().HaveCount(2, "1h window produces 1 regular + 1 trailing partial bucket");
        // Regular bucket [14:00, 15:00): run at 15:20 is NOT here.
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero));
        result[0].Total.Should().Be(0, "the run at 15:20 is not in the regular [14:00,15:00) bucket");
        // Trailing bucket [15:00, 15:30): run at 15:20 IS here.
        result[1].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 15, 0, 0, TimeSpan.Zero),
            "trailing bucket starts at windowEnd (current hour start)");
        result[1].Succeeded.Should().Be(1, "the run at 15:20 must be in the trailing [15:00,15:30) bucket");
        result[1].Total.Should().Be(1);
    }

    // ── 6-hour window ─────────────────────────────────────────────────────

    /// <summary>
    /// 6h window with now=15:30 → 7 buckets (6 regular + 1 trailing [15:00,15:30)).
    /// run2 at 15:00 was previously excluded; after the fix it is in the trailing bucket.
    /// </summary>
    [Fact]
    public void BuildBuckets_6hWindow_Returns6HourlyBuckets()
    {
        // now = 15:30 UTC → windowEnd = 15:00, windowStart = 09:00. Regular buckets: [09,10)...[14,15).
        var run1 = MakeRun(new DateTimeOffset(2026, 9, 23, 13, 0, 0, TimeSpan.Zero));
        var run2 = MakeRun(new DateTimeOffset(2026, 9, 23, 15, 0, 0, TimeSpan.Zero), PipelineStep.Failed);
        var runOutside = MakeRun(new DateTimeOffset(2026, 9, 23, 8, 59, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run1, run2, runOutside], windowHours: 6, Now);

        result.Should().HaveCount(7, "6h window produces 6 regular buckets plus 1 trailing partial bucket");
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero),
            "first bucket must start 6 h before the current hour");

        // run1 at 13:00 is in the [13:00, 14:00) bucket (index 4 of 7)
        var bucket13 = result.Single(b => b.SlotStart.Hour == 13 && b.SlotStart.Date == new DateOnly(2026, 9, 23).ToDateTime(TimeOnly.MinValue));
        bucket13.Succeeded.Should().Be(1);

        // runOutside at 08:59 is before [09:00, so it must not appear in any bucket.
        result[0].Total.Should().Be(0, "runOutside at 08:59 is before windowStart 09:00");

        // Trailing bucket [15:00, 15:30): run2 at 15:00 is now counted here (fix for #3077).
        var trailingBucket = result[^1];
        trailingBucket.SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 15, 0, 0, TimeSpan.Zero),
            "trailing bucket SlotStart is windowEnd = 15:00");
        trailingBucket.Failed.Should().Be(1, "run2 at 15:00 must be in the trailing [15:00,15:30) bucket");
        trailingBucket.Total.Should().Be(1);

        result.Sum(b => b.Total).Should().Be(2,
            "run1 (13:00) is in regular bucket; run2 (15:00) is in trailing bucket; runOutside (08:59) is excluded");
    }

    // ── 24-hour window ────────────────────────────────────────────────────

    /// <summary>24h window → 25 buckets (24 regular + 1 trailing). A run from 25 h ago is excluded.</summary>
    [Fact]
    public void BuildBuckets_24hWindow_Returns24HourlyBuckets()
    {
        // now = 15:30 UTC on 2026-09-23 → windowEnd=15:00, windowStart=15:00 on 2026-09-22.
        var runInWindow = MakeRun(new DateTimeOffset(2026, 9, 22, 20, 0, 0, TimeSpan.Zero));
        var runOutside = MakeRun(new DateTimeOffset(2026, 9, 22, 14, 0, 0, TimeSpan.Zero)); // 25h before now's hour

        var result = InsightsBucketer.BuildBuckets([runInWindow, runOutside], windowHours: 24, Now);

        result.Should().HaveCount(25, "24h window produces 24 regular buckets plus 1 trailing partial bucket");
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 22, 15, 0, 0, TimeSpan.Zero),
            "first bucket covers [15:00 yesterday, 16:00 yesterday)");
        result.Sum(b => b.Total).Should().Be(1,
            "only runInWindow is inside the 24h range; runOutside (25h ago) must be excluded");
    }

    /// <summary>
    /// A run started at exactly windowEnd (15:00:00) is in the trailing bucket, not in any regular bucket.
    /// (a) Not in the last regular bucket [14:00, 15:00) — the &lt; upper bound excludes it.
    /// (b) In the trailing bucket [15:00, 15:30) — the >= lower bound includes it.
    /// </summary>
    [Fact]
    public void BuildBuckets_HourlyWindow_RunAtExactCurrentHourStart_IsInTrailingBucket_NotPreviousBucket()
    {
        // now = 15:30, 24h window. Run at exactly 15:00:00.
        var run = MakeRun(new DateTimeOffset(2026, 9, 23, 15, 0, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run], windowHours: 24, Now);

        result.Should().HaveCount(25);

        // (a) The last regular bucket covers [14:00, 15:00) — run at 15:00 is excluded by < upper bound.
        var lastRegular = result[^2];
        lastRegular.SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero),
            "the last regular bucket spans [14:00, 15:00)");
        lastRegular.Total.Should().Be(0, "run at exactly 15:00 must NOT be in the [14:00, 15:00) bucket");

        // (b) The trailing bucket covers [15:00, 15:30) — run at 15:00 is included by >= lower bound.
        var trailing = result[^1];
        trailing.SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 15, 0, 0, TimeSpan.Zero),
            "the trailing bucket starts at windowEnd = 15:00");
        trailing.Total.Should().Be(1, "run at exactly 15:00 must be in the trailing [15:00, 15:30) bucket");
    }

    /// <summary>
    /// When now is at an exact hour boundary, windowEnd == now and the trailing bucket
    /// would be a zero-duration range — no trailing bucket should be added.
    /// </summary>
    [Fact]
    public void BuildBuckets_HourlyWindow_WhenNowIsExactHourBoundary_NoTrailingBucket()
    {
        // now = 15:00:00 exactly. windowEnd = 15:00. now == windowEnd → no trailing bucket.
        var run = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 30, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run], windowHours: 1, NowOnHourBoundary);

        result.Should().HaveCount(1,
            "when now == windowEnd there is no partial current-hour duration, so no trailing bucket");
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero));
        result[0].Total.Should().Be(1);
    }

    // ── 7-day window ──────────────────────────────────────────────────────

    /// <summary>7d window → exactly 7 daily buckets. A run from 8 days ago is excluded.</summary>
    [Fact]
    public void BuildBuckets_7dWindow_Returns7DailyBuckets()
    {
        // now = 2026-09-23 → window covers [2026-09-17, 2026-09-24)
        var runInWindow = MakeRun(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero));
        var runOutside = MakeRun(new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero)); // 8 days ago

        var result = InsightsBucketer.BuildBuckets([runInWindow, runOutside], windowHours: 168, Now);

        result.Should().HaveCount(7, "7d window must produce exactly 7 daily buckets");
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero),
            "first bucket starts 6 days before today UTC");
        result[^1].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero),
            "last bucket is today UTC");
        result.Sum(b => b.Total).Should().Be(1,
            "only runInWindow (Sep 20) is inside [Sep 17, Sep 24); runOutside (Sep 15) must be excluded");
    }

    // ── "All" window ──────────────────────────────────────────────────────

    /// <summary>
    /// "All" window → daily buckets from the earliest run's date to today.
    /// Two runs 10 days apart produce 11 buckets (inclusive range).
    /// </summary>
    [Fact]
    public void BuildBuckets_AllWindow_ReturnsRangeFromEarliestRunToNow()
    {
        // now = 2026-09-23; earliest run = 2026-09-13 → should produce 11 buckets (Sep 13 to Sep 23)
        var run1 = MakeRun(new DateTimeOffset(2026, 9, 13, 8, 0, 0, TimeSpan.Zero));
        var run2 = MakeRun(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run1, run2], windowHours: 0, Now);

        result.Should().HaveCount(11, "inclusive range from Sep 13 to Sep 23 is 11 days");
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero));
        result[^1].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        result.Sum(b => b.Total).Should().Be(2, "both runs must be counted");
    }

    /// <summary>
    /// "All" window caps at 365 days even when runs span more than 365 days.
    /// </summary>
    [Fact]
    public void BuildBuckets_AllWindow_CapsAt365Buckets()
    {
        // now = 2026-09-23; run 400 days ago
        var runOld = MakeRun(Now.AddDays(-400));
        var runRecent = MakeRun(Now.AddDays(-1));

        var result = InsightsBucketer.BuildBuckets([runOld, runRecent], windowHours: 0, Now);

        result.Should().HaveCount(365, "result must be capped at 365 daily buckets");
        result.Sum(b => b.Total).Should().Be(1,
            "the 400-day-old run is before the capped window start; only the recent run is counted");
    }

    /// <summary>
    /// "All" window with a run whose timestamp is in the future (clock drift) → empty list.
    /// </summary>
    [Fact]
    public void BuildBuckets_AllWindow_FutureTimestamps_ReturnsEmptyList()
    {
        var futureRun = MakeRun(Now.AddDays(5));

        var result = InsightsBucketer.BuildBuckets([futureRun], windowHours: 0, Now);

        result.Should().BeEmpty(
            "when all runs have future timestamps the clamped range is empty");
    }

    // ── Boundary inclusion ────────────────────────────────────────────────

    /// <summary>
    /// A run started exactly at the window boundary (slot start) must be included in the first bucket.
    /// </summary>
    [Fact]
    public void BuildBuckets_RunExactlyAtWindowStart_IsIncludedInFirstBucket()
    {
        // 6h window, now = 15:30. Window start = 09:00. Run at exactly 09:00 must be in first bucket.
        var run = MakeRun(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run], windowHours: 6, Now);

        result.Should().HaveCount(7, "6h window with trailing bucket");
        result[0].SlotStart.Hour.Should().Be(9);
        result[0].Total.Should().Be(1, "run at exactly the slot start is inclusive");
    }

    // ── Outcome counts ────────────────────────────────────────────────────

    /// <summary>Completed, Failed, and Cancelled runs are counted separately per bucket.</summary>
    [Fact]
    public void BuildBuckets_MixedOutcomes_CountedSeparatelyPerBucket()
    {
        // now = 15:30 UTC; 1h window → regular bucket covers [14:00, 15:00).
        // Place all three runs in that regular bucket.
        var completed = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 5, 0, TimeSpan.Zero), PipelineStep.Completed);
        var failed = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 10, 0, TimeSpan.Zero), PipelineStep.Failed);
        var cancelled = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 15, 0, TimeSpan.Zero), PipelineStep.Cancelled);

        var result = InsightsBucketer.BuildBuckets([completed, failed, cancelled], windowHours: 1, Now);

        // 2 buckets: regular [14:00,15:00) + trailing [15:00,15:30)
        result.Should().HaveCount(2, "1h window now produces 2 buckets");
        result[0].Succeeded.Should().Be(1);
        result[0].Failed.Should().Be(1);
        result[0].Cancelled.Should().Be(1);
        result[0].Total.Should().Be(3);
        result[1].Total.Should().Be(0, "trailing bucket is empty (no runs started in [15:00,15:30))");
    }

    /// <summary>
    /// Terminal-like steps are classified like everywhere else in the UI: a merged PR counts as
    /// succeeded, a closed PR as cancelled, and a conflict restart as restarted — none are dropped.
    /// </summary>
    [Fact]
    public void BuildBuckets_TerminalLikeSteps_AreClassifiedNotDropped()
    {
        var merged = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 5, 0, TimeSpan.Zero), PipelineStep.PrMerged);
        var closed = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 10, 0, TimeSpan.Zero), PipelineStep.PrClosed);
        var restarted = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 15, 0, TimeSpan.Zero), PipelineStep.ConflictRestart);

        var result = InsightsBucketer.BuildBuckets([merged, closed, restarted], windowHours: 1, Now);

        // 2 buckets: regular [14:00,15:00) + trailing [15:00,15:30)
        result.Should().HaveCount(2, "1h window now produces 2 buckets");
        result[0].Succeeded.Should().Be(1);
        result[0].Cancelled.Should().Be(1);
        result[0].Restarted.Should().Be(1);
        result[0].Total.Should().Be(3);
        result[1].Total.Should().Be(0, "trailing bucket is empty (no runs started in [15:00,15:30))");
    }
}

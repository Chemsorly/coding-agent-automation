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
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 15, 30, 0, TimeSpan.Zero);

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
    /// 1h window → exactly 1 hourly bucket. A run in that bucket is counted.
    /// With now = 15:30 UTC, windowEnd = 15:00 (truncated to hour), windowStart = 14:00.
    /// The single bucket covers [14:00, 15:00).
    /// </summary>
    [Fact]
    public void BuildBuckets_1hWindow_Returns1HourlyBucket()
    {
        // now = 15:30 UTC. The single 1h bucket covers [14:00, 15:00).
        var run = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 30, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run], windowHours: 1, Now);

        result.Should().HaveCount(1, "1h window must produce exactly 1 bucket");
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero),
            "the bucket SlotStart must be 1 hour before the truncated-to-hour now");
        result[0].Completed.Should().Be(1, "the run at 14:30 must be counted in the [14:00,15:00) bucket");
        result[0].Total.Should().Be(1);
    }

    /// <summary>A run started before the 1h window start is excluded.</summary>
    [Fact]
    public void BuildBuckets_1hWindow_RunOutsideWindow_IsExcluded()
    {
        // now = 15:30; bucket covers [14:00, 15:00). A run at 13:59 is outside.
        var run = MakeRun(new DateTimeOffset(2026, 9, 23, 13, 59, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run], windowHours: 1, Now);

        result.Should().HaveCount(1);
        result[0].Total.Should().Be(0, "run started before the 1h window start must not be counted");
    }

    // ── 6-hour window ─────────────────────────────────────────────────────

    /// <summary>6h window → exactly 6 hourly buckets with correct slot assignment.</summary>
    [Fact]
    public void BuildBuckets_6hWindow_Returns6HourlyBuckets()
    {
        // now = 15:30 UTC → window start = 09:00 UTC. Buckets: [09,10), [10,11), ..., [14,15)
        var run1 = MakeRun(new DateTimeOffset(2026, 9, 23, 13, 0, 0, TimeSpan.Zero));
        var run2 = MakeRun(new DateTimeOffset(2026, 9, 23, 15, 0, 0, TimeSpan.Zero), PipelineStep.Failed);
        var runOutside = MakeRun(new DateTimeOffset(2026, 9, 23, 8, 59, 0, TimeSpan.Zero));

        var result = InsightsBucketer.BuildBuckets([run1, run2, runOutside], windowHours: 6, Now);

        result.Should().HaveCount(6, "6h window must produce exactly 6 hourly buckets");
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero),
            "first bucket must start 6 h before the current hour");
        result[^1].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero));

        // run1 is in the [13:00, 14:00) bucket (index 4 of 6)
        var bucket13 = result.Single(b => b.SlotStart.Hour == 13);
        bucket13.Completed.Should().Be(1);

        // run2 is in the [15:00, 16:00) bucket… but the window is [09:00, 15:00), so:
        // wait — now = 15:30, window end = now's hour = 15:00 truncated + 6 = last bucket is [14:00, 15:00)
        // run2 at 15:00 is inside [15:00, 16:00) which is OUTSIDE the 6h window
        result.Sum(b => b.Total).Should().Be(1,
            "only run1 (13:00) is inside [09:00,15:00); run2 at 15:00 and runOutside at 08:59 are outside");
    }

    // ── 24-hour window ────────────────────────────────────────────────────

    /// <summary>24h window → exactly 24 hourly buckets. A run from 25 h ago is excluded.</summary>
    [Fact]
    public void BuildBuckets_24hWindow_Returns24HourlyBuckets()
    {
        // now = 15:30 UTC on 2026-09-23 → window covers [15:00 on 2026-09-22, 15:00 on 2026-09-23)
        var runInWindow = MakeRun(new DateTimeOffset(2026, 9, 22, 20, 0, 0, TimeSpan.Zero));
        var runOutside = MakeRun(new DateTimeOffset(2026, 9, 22, 14, 0, 0, TimeSpan.Zero)); // 25h before now's hour

        var result = InsightsBucketer.BuildBuckets([runInWindow, runOutside], windowHours: 24, Now);

        result.Should().HaveCount(24, "24h window must produce exactly 24 hourly buckets");
        result[0].SlotStart.Should().Be(new DateTimeOffset(2026, 9, 22, 15, 0, 0, TimeSpan.Zero),
            "first bucket covers [15:00 yesterday, 16:00 yesterday)");
        result.Sum(b => b.Total).Should().Be(1,
            "only runInWindow is inside the 24h range; runOutside (25h ago) must be excluded");
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

        result.Should().HaveCount(6);
        result[0].SlotStart.Hour.Should().Be(9);
        result[0].Total.Should().Be(1, "run at exactly the slot start is inclusive");
    }

    // ── Outcome counts ────────────────────────────────────────────────────

    /// <summary>Completed, Failed, and Cancelled runs are counted separately per bucket.</summary>
    [Fact]
    public void BuildBuckets_MixedOutcomes_CountedSeparatelyPerBucket()
    {
        // now = 15:30 UTC; 1h window → bucket covers [14:00, 15:00).
        // Place all three runs in that bucket.
        var completed = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 5, 0, TimeSpan.Zero), PipelineStep.Completed);
        var failed = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 10, 0, TimeSpan.Zero), PipelineStep.Failed);
        var cancelled = MakeRun(new DateTimeOffset(2026, 9, 23, 14, 15, 0, TimeSpan.Zero), PipelineStep.Cancelled);

        var result = InsightsBucketer.BuildBuckets([completed, failed, cancelled], windowHours: 1, Now);

        result.Should().HaveCount(1);
        result[0].Completed.Should().Be(1);
        result[0].Failed.Should().Be(1);
        result[0].Cancelled.Should().Be(1);
        result[0].Total.Should().Be(3);
    }
}

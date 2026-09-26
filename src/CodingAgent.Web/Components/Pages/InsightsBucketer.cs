using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;

namespace CodingAgent.Web.Components.Pages;

/// <summary>
/// Bucketing logic for the Insights chart — extracted from Insights.razor so it can be unit-tested
/// without bUnit. All DateTime references use the injected <paramref name="now"/> (UTC) so tests
/// are deterministic.
/// </summary>
internal static class InsightsBucketer
{
    private const int MaxAllWindowBuckets = 365;

    /// <summary>
    /// Builds a list of <see cref="TimeBucket"/> objects covering the requested time window.
    /// </summary>
    /// <param name="items">
    /// The run summaries to bucket. An empty list always returns an empty bucket list.
    /// </param>
    /// <param name="windowHours">
    /// Number of hours to cover.
    /// <list type="bullet">
    ///   <item>1, 6, 24 — hourly buckets, one per hour in the window.</item>
    ///   <item>168 (7 days) — daily buckets, one per day in the window.</item>
    ///   <item>0 ("All") — daily buckets spanning from the earliest run UTC date to <paramref name="now"/>,
    ///   capped at <see cref="MaxAllWindowBuckets"/> days.</item>
    /// </list>
    /// </param>
    /// <param name="now">Reference point for bucket generation (UTC). Injected so tests are deterministic.</param>
    /// <returns>Ordered list of buckets from oldest to newest. Empty if <paramref name="items"/> is empty.</returns>
    public static IReadOnlyList<TimeBucket> BuildBuckets(
        IReadOnlyList<PipelineRunSummary> items,
        int windowHours,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
            return [];

        // Hourly windows: 1h, 6h, 24h
        if (windowHours is 1 or 6 or 24)
            return BuildHourlyBuckets(items, windowHours, now);

        // Weekly window: 168h = 7 days
        if (windowHours == 168)
            return BuildDailyBuckets(items, dayCount: 7, now);

        // TODO: [WARNING] Unsupported windowHours values (e.g. a future "30d" = 720h option) fall
        // through to BuildAllWindowBuckets, silently producing daily buckets from the earliest run
        // rather than a N-day window. Add an explicit dispatch branch or throw ArgumentOutOfRangeException
        // for unrecognised values once additional windows are introduced.

        // "All" window: daily buckets from earliest run to now
        return BuildAllWindowBuckets(items, now);
    }

    private static IReadOnlyList<TimeBucket> BuildHourlyBuckets(
        IReadOnlyList<PipelineRunSummary> items,
        int windowHours,
        DateTimeOffset now)
    {
        // TODO: [WARNING] The private helper methods (BuildHourlyBuckets, BuildDailyBuckets,
        // BuildAllWindowBuckets) do not null-guard their `items` parameter. They are currently
        // reachable only through the public BuildBuckets entry point, which calls
        // ArgumentNullException.ThrowIfNull(items). If any helper is ever made internal/public
        // or called directly, a NullReferenceException will be thrown inside a LINQ chain rather
        // than at the parameter boundary. Add explicit null guards if the visibility changes.

        // TODO: [WARNING] The nested .Where(...).ToList() inside Enumerable.Range(0, windowHours)
        // performs one full pass over `items` per slot (e.g. 24 passes for the 24h window). At the
        // current page cap of 500 items this is acceptable. If the page cap is raised significantly,
        // consider sorting items once and using a sweep or binary-search approach. There is no
        // CancellationToken parameter on BuildBuckets, so no cancellation path exists for this loop;
        // adding one would require a signature change.

        // Truncate now to the start of the current hour (UTC)
        // TODO: [WARNING] windowEnd is the start of the *current* hour, not the current instant.
        // Runs in the current partial hour (e.g. 15:30–16:00 when now=15:30) are excluded from
        // the chart. The x-axis rightmost label therefore reads "14:00 UTC" (one hour before now)
        // rather than "now", which may confuse users who expect "last 24h" to include recent minutes.
        // Fix: add a trailing partial-hour bucket [windowEnd, now) to capture the current hour's runs.
        var windowEnd = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month,
            now.UtcDateTime.Day, now.UtcDateTime.Hour, 0, 0, TimeSpan.Zero);
        var windowStart = windowEnd.AddHours(-windowHours);

        return Enumerable.Range(0, windowHours)
            .Select(i =>
            {
                var slotStart = windowStart.AddHours(i);
                var slotEnd = slotStart.AddHours(1);
                var slotItems = items
                    .Where(r => r.StartedAtOffset >= slotStart && r.StartedAtOffset < slotEnd);
                return ToBucket(slotStart, slotItems);
            })
            .ToList();
    }

    private static IReadOnlyList<TimeBucket> BuildDailyBuckets(
        IReadOnlyList<PipelineRunSummary> items,
        int dayCount,
        DateTimeOffset now)
    {
        // TODO: [WARNING] `now` is expected to be UTC. `now.UtcDateTime.Date` correctly normalises
        // any non-UTC DateTimeOffset to UTC before extracting the date, so the logic is safe. However
        // the parameter type (DateTimeOffset) does not enforce UTC, and a non-UTC value would still
        // produce a correct result silently. All callers (InsightsBucketer.BuildBuckets and
        // Insights.razor LoadAsync) pass DateTimeOffset.UtcNow — keep this invariant when refactoring.

        // Start of today UTC
        var todayUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var windowStart = todayUtc.AddDays(-(dayCount - 1));

        return BuildDailySlots(items, windowStart, dayCount);
    }

    private static IReadOnlyList<TimeBucket> BuildAllWindowBuckets(
        IReadOnlyList<PipelineRunSummary> items,
        DateTimeOffset now)
    {
        var todayUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        // TODO: [WARNING] Chart-vs-headline divergence for the "All" window when history spans
        // >365 days. This method caps at MaxAllWindowBuckets (365) days, so runs older than 365
        // days have no chart bucket. However, Insights.razor LoadAsync applies no cutoff for the
        // "All" window (windowStart = DateTimeOffset.MinValue), so _total and all rate metrics
        // count every fetched run including those older than 365 days. The chart bar sum will be
        // less than the headline _total. Fix: either increase MaxAllWindowBuckets to match the
        // fetch page cap, or apply the same 365-day window start to the pre-filter in LoadAsync
        // when windowHours == 0 (requires passing the computed windowStart back to the caller or
        // exposing it from this method).

        // TODO: [WARNING] items.Min() will throw InvalidOperationException on an empty sequence.
        // The public BuildBuckets entry point guards against empty input (returns [] at line ~38),
        // so no current caller hits this path with an empty list. If BuildAllWindowBuckets is ever
        // called directly in a future refactor, add a guard: if (items.Count == 0) return [];

        // Earliest UTC date across all runs, clamped to now to handle future timestamps (clock drift).
        var earliestRun = items.Min(r => r.StartedAtOffset.UtcDateTime.Date);
        var earliestUtc = new DateTimeOffset(earliestRun, TimeSpan.Zero);
        if (earliestUtc > todayUtc)
            return [];

        var totalDays = (int)(todayUtc - earliestUtc).TotalDays + 1;
        var bucketCount = Math.Min(totalDays, MaxAllWindowBuckets);
        // If capped, start from the most recent MaxAllWindowBuckets days
        var windowStart = todayUtc.AddDays(-(bucketCount - 1));

        return BuildDailySlots(items, windowStart, bucketCount);
    }

    /// <summary>
    /// Builds <paramref name="bucketCount"/> consecutive one-day <see cref="TimeBucket"/> objects
    /// starting at <paramref name="windowStart"/>. Shared by <see cref="BuildDailyBuckets"/> and
    /// <see cref="BuildAllWindowBuckets"/> to avoid duplicating the slot-construction loop.
    /// </summary>
    private static IReadOnlyList<TimeBucket> BuildDailySlots(
        IReadOnlyList<PipelineRunSummary> items,
        DateTimeOffset windowStart,
        int bucketCount)
    {
        return Enumerable.Range(0, bucketCount)
            .Select(i =>
            {
                var slotStart = windowStart.AddDays(i);
                var slotEnd = slotStart.AddDays(1);
                var slotItems = items
                    .Where(r => r.StartedAtOffset >= slotStart && r.StartedAtOffset < slotEnd);
                return ToBucket(slotStart, slotItems);
            })
            .ToList();
    }

    /// <summary>
    /// Counts a slot's runs per outcome with <see cref="RunOutcomeDisplay.Classify"/>, so the chart agrees with
    /// the Runs list and the outcome mix: a merged PR counts as succeeded, a closed PR as cancelled, and conflict
    /// restarts get their own series. Runs that are still in flight are not counted.
    /// </summary>
    private static TimeBucket ToBucket(DateTimeOffset slotStart, IEnumerable<PipelineRunSummary> slotItems)
    {
        int succeeded = 0, failed = 0, cancelled = 0, restarted = 0;
        foreach (var run in slotItems)
        {
            switch (RunOutcomeDisplay.Classify(run.FinalStep))
            {
                case RunOutcome.Succeeded: succeeded++; break;
                case RunOutcome.Failed: failed++; break;
                case RunOutcome.Cancelled: cancelled++; break;
                case RunOutcome.Restarted: restarted++; break;
            }
        }

        return new TimeBucket(slotStart, succeeded, failed, cancelled, restarted);
    }
}

/// <summary>
/// A single time-slot bucket for the Insights chart. Replaces the old <c>DayBucket</c> record —
/// now supports both hourly (1h/6h/24h) and daily (7d/All) granularity.
/// </summary>
/// <param name="SlotStart">UTC start of the bucket (truncated to the hour for hourly, to the day for daily).</param>
/// <param name="Succeeded">Runs that completed or whose PR was merged.</param>
/// <param name="Failed">Failed runs.</param>
/// <param name="Cancelled">Cancelled runs, including runs whose PR was closed.</param>
/// <param name="Restarted">Runs superseded by a conflict restart.</param>
internal sealed record TimeBucket(DateTimeOffset SlotStart, int Succeeded, int Failed, int Cancelled, int Restarted)
{
    public int Total => Succeeded + Failed + Cancelled + Restarted;
}

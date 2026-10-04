using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests covering the Runs page filters, sorting, paging, and row navigation.
///
/// Seed strategy: all runs are inserted directly into InMemoryPipelineRunHistoryService
/// via AddRunSummaryAsync — no agent connections, no dispatches. This matches the pattern
/// established in InsightsAndAttentionTests and makes the suite fast and deterministic.
///
/// Page size on Runs.razor is 25.  The paging tests seed 60 runs so two full pages plus a
/// partial third page exist, making both "Next disabled on last page" and "Page 2 has no
/// duplicates" meaningful.
///
/// Scenarios from issue #3111:
/// 1. Tabs — each tab shows only its outcome; counts match seeded data.
/// 2. Filters combine — Type + Initiated-by + Feedback-only narrow correctly; clearing restores.
/// 3. Sorting — When asc/desc and Duration produce correctly ordered rows.
/// 4. Paging — Prev disabled on page 1; Next disabled on last page; no duplicates/gaps.
/// 5. Navigation — clicking a row opens /runs/{id}; issue and PR links point to seeded URLs.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "UI")]
[Collection(E2ECollection.Name)]
public sealed class RunsListTests : E2ETestBase
{
    public RunsListTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NewRunId() => Guid.NewGuid().ToString();

    /// <summary>
    /// Creates a <see cref="PipelineRunSummary"/> with the given parameters.
    /// <paramref name="durationSeconds"/> controls CompletedAtOffset relative to StartedAtOffset.
    /// </summary>
    // TODO [WARNING]: The #pragma warning disable CS0618 suppressions below assign to the obsolete
    // StartedAt / CompletedAt (DateTime) properties. If those properties are removed in a future
    // refactor, these assignments become compile errors rather than warnings, surfacing the breakage
    // immediately. Prefer removing the obsolete assignments entirely and relying solely on
    // StartedAtOffset / CompletedAtOffset if the model allows it.
    private static PipelineRunSummary MakeRun(
        string runId,
        string issueId,
        PipelineStep finalStep,
        DateTimeOffset startedAt,
        PipelineRunType runType = PipelineRunType.Implementation,
        string initiatedBy = InitiatedByConstants.Manual,
        int durationSeconds = 60,
        string? issueUrl = null,
        string? pullRequestUrl = null,
        RunFeedback? feedback = null,
        string? projectId = null) => new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier(issueId),
            IssueTitle = $"Issue {issueId}",
            FinalStep = finalStep,
            RunType = runType,
            InitiatedBy = initiatedBy,
            StartedAtOffset = startedAt,
#pragma warning disable CS0618
            StartedAt = startedAt.DateTime,
#pragma warning restore CS0618
            CompletedAtOffset = startedAt.AddSeconds(durationSeconds),
#pragma warning disable CS0618
            CompletedAt = startedAt.AddSeconds(durationSeconds).DateTime,
#pragma warning restore CS0618
            IssueUrl = issueUrl,
            PullRequestUrl = pullRequestUrl,
            Feedback = feedback,
            ProjectId = projectId,
        };

    // ── Scenario 1: Tabs ─────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 1: Each outcome tab shows only runs with that outcome.
    /// Seeded: 5 Completed, 3 Failed, 2 Cancelled (= 10 total).
    /// "All" shows all 10; each specific tab shows its own subset.
    /// </summary>
    [Fact]
    public async Task Runs_Scenario1_Tabs_EachTabShowsOnlyItsOutcome()
    {
        var now = DateTimeOffset.UtcNow;
        var completedIds = Enumerable.Range(1, 5).Select(i => $"TAB-C{i}").ToList();
        var failedIds    = Enumerable.Range(1, 3).Select(i => $"TAB-F{i}").ToList();
        var cancelledIds = Enumerable.Range(1, 2).Select(i => $"TAB-X{i}").ToList();

        foreach (var id in completedIds)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), id, PipelineStep.Completed, now.AddMinutes(-10)));

        foreach (var id in failedIds)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), id, PipelineStep.Failed, now.AddMinutes(-8)));

        foreach (var id in cancelledIds)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), id, PipelineStep.Cancelled, now.AddMinutes(-5)));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // All tab — all 10 rows visible
        // TODO [WARNING]: This assertion depends on a clean store. E2ETestBase.InitializeAsync calls
        // ResetAllAsync which clears _history before each test, so this is safe today. If test isolation
        // ever weakens (e.g. parallel execution enabled), this count-based assertion will become fragile.
        // A more robust approach would assert on seeded-ID membership rather than absolute counts.
        var allCount = await runsPage.GetRowCountAsync();
        Assert.Equal(10, allCount);

        // Completed tab
        await runsPage.SelectTabAsync("Completed");
        var completedCount = await runsPage.GetRowCountAsync();
        Assert.Equal(5, completedCount);
        // Spot-check: a failed issue must not appear
        Assert.False(await runsPage.IsRunVisibleAsync("TAB-F1"),
            "Failed run must not appear on the Completed tab");
        foreach (var id in completedIds)
            Assert.True(await runsPage.IsRunVisibleAsync(id), $"Completed run {id} must be visible");

        // Failed tab
        await runsPage.SelectTabAsync("Failed");
        var failedCount = await runsPage.GetRowCountAsync();
        Assert.Equal(3, failedCount);
        Assert.False(await runsPage.IsRunVisibleAsync("TAB-C1"),
            "Completed run must not appear on the Failed tab");
        foreach (var id in failedIds)
            Assert.True(await runsPage.IsRunVisibleAsync(id), $"Failed run {id} must be visible");

        // Cancelled tab
        await runsPage.SelectTabAsync("Cancelled");
        var cancelledCount = await runsPage.GetRowCountAsync();
        Assert.Equal(2, cancelledCount);
        Assert.False(await runsPage.IsRunVisibleAsync("TAB-F1"),
            "Failed run must not appear on the Cancelled tab");
        foreach (var id in cancelledIds)
            Assert.True(await runsPage.IsRunVisibleAsync(id), $"Cancelled run {id} must be visible");

        // Back to All
        await runsPage.SelectTabAsync("All");
        // TODO [WARNING]: Count-only check here. If a bug caused SelectTabAsync to land on the wrong
        // tab (e.g. CSS class mismatch), 10 rows might still show from a lingering filter state,
        // masking the defect. Consider asserting spot-membership across all three outcome groups.
        Assert.Equal(10, await runsPage.GetRowCountAsync());
    }

    // ── Scenario 2: Filters combine ──────────────────────────────────────────

    /// <summary>
    /// Scenario 2: Type + Initiated-by + Feedback-only filters narrow the list correctly.
    /// Seeded:
    ///   - 5 Review / loop:review        (no feedback)
    ///   - 3 Review / loop:review + feedback
    ///   - 1 Review / manual             (no feedback)  ← discriminator for the Initiated-by filter
    ///   - 4 Implementation / loop:issue
    ///   - 4 Decomposition / loop:decomposition
    ///
    /// The extra Review/manual run ensures that applying InitiatedBy=loop:review after
    /// Type=Review visibly narrows the count from 9 to 8. Without it the filter would be
    /// tautological (all Review rows already have loop:review), so a broken filter would
    /// still pass.
    /// </summary>
    [Fact]
    public async Task Runs_Scenario2_FiltersConjoin_NarrowCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        var feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback { Category = "e2e-test" }
        };

        // Review / loop:review without feedback
        for (var i = 1; i <= 5; i++)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), $"F-REV-{i}", PipelineStep.Completed, now.AddMinutes(-i),
                    runType: PipelineRunType.Review, initiatedBy: InitiatedByConstants.LoopReview));

        // Review / loop:review WITH feedback
        for (var i = 1; i <= 3; i++)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), $"F-REV-FB-{i}", PipelineStep.Completed, now.AddMinutes(-i - 10),
                    runType: PipelineRunType.Review, initiatedBy: InitiatedByConstants.LoopReview,
                    feedback: feedback));

        // Review / manual — discriminator that proves InitiatedBy filter actually narrows
        await Fixture.HistoryService.AddRunSummaryAsync(
            MakeRun(NewRunId(), "F-REV-MANUAL", PipelineStep.Completed, now.AddMinutes(-20),
                runType: PipelineRunType.Review, initiatedBy: InitiatedByConstants.Manual));

        // Implementation / loop:issue
        for (var i = 1; i <= 4; i++)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), $"F-IMPL-{i}", PipelineStep.Completed, now.AddMinutes(-i - 30),
                    runType: PipelineRunType.Implementation, initiatedBy: InitiatedByConstants.LoopIssue));

        // Decomposition / loop:decomposition
        for (var i = 1; i <= 4; i++)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), $"F-DECOMP-{i}", PipelineStep.Completed, now.AddMinutes(-i - 40),
                    runType: PipelineRunType.Decomposition, initiatedBy: InitiatedByConstants.LoopDecomposition));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // Baseline: all 17 visible (5+3+1+4+4)
        Assert.Equal(17, await runsPage.GetRowCountAsync());

        // Apply Type=Review: should show 5 + 3 + 1 = 9 rows
        await runsPage.SelectTypeFilterAsync("Review");
        Assert.Equal(9, await runsPage.GetRowCountAsync());

        // Apply Initiated by=loop:review: narrows from 9 to 8 (excludes the Review/manual run).
        // This is the key discriminating assertion: a broken filter would keep the count at 9.
        await runsPage.SelectInitiatedByFilterAsync(InitiatedByConstants.LoopReview);
        Assert.Equal(8, await runsPage.GetRowCountAsync());
        // The manual-initiated Review run must not be visible
        Assert.False(await runsPage.IsRunVisibleAsync("F-REV-MANUAL"),
            "Review/manual run must be hidden when Initiated by = loop:review");

        // Also enable Feedback-only: should narrow to the 3 Review+loop:review+feedback rows
        await runsPage.SetFeedbackOnlyAsync(true);
        // Feedback-only is a server-side filter → reloads; wait is baked into SetFeedbackOnlyAsync
        // TODO [WARNING]: This asserts only a count of 3. A bug that kept three arbitrary non-feedback
        // rows visible would still pass. Consider also asserting ID membership, e.g.:
        //   var fbIds = await runsPage.GetVisibleIssueIdentifiersAsync();
        //   Assert.All(fbIds, id => Assert.StartsWith("F-REV-FB-", id));
        Assert.Equal(3, await runsPage.GetRowCountAsync());

        // Clear Feedback-only → back to 8
        await runsPage.SetFeedbackOnlyAsync(false);
        Assert.Equal(8, await runsPage.GetRowCountAsync());

        // Clear Type filter → still Initiated by=loop:review; 5+3 = 8 (the manual Review run
        // was Review type, but InitiatedBy=loop:review still excludes it; the Impl/Decomp rows
        // have different initiatedBy values so they are also excluded)
        // TODO [WARNING]: This asserts a count of 8 but does not verify which specific rows are
        // visible. If the Initiated-by filter were inverted (showing everything except loop:review),
        // a coincidental count of 8 could still occur. Consider adding an Assert.All check that
        // visible IDs match expected loop:review runs for a stronger guarantee.
        await runsPage.SelectTypeFilterAsync("");
        Assert.Equal(8, await runsPage.GetRowCountAsync());

        // Clear Initiated by filter → back to all 17
        await runsPage.SelectInitiatedByFilterAsync("");
        Assert.Equal(17, await runsPage.GetRowCountAsync());
    }

    // ── Scenario 3: Sorting ───────────────────────────────────────────────────

    /// <summary>
    /// Scenario 3a: "When" descending (default) shows newest run first;
    /// toggling to ascending shows oldest run first.
    /// </summary>
    [Fact]
    public async Task Runs_Scenario3a_When_Sorting_DefaultDescThenAsc()
    {
        var baseTime = DateTimeOffset.UtcNow.AddHours(-10);

        // Seed 5 runs with 1-hour gaps — IDs encode their relative age so ordering is obvious.
        // SORT-W-1 is oldest, SORT-W-5 is newest.
        for (var i = 1; i <= 5; i++)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), $"SORT-W-{i}", PipelineStep.Completed,
                    baseTime.AddHours(i)));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // Default is When DESC (newest first).
        var idsDesc = await runsPage.GetVisibleIssueIdentifiersAsync();
        // Filter to only our seeded IDs (the store is reset before each test, so history is clean).
        var filtered = idsDesc.Where(id => id.StartsWith("SORT-W-")).ToList();
        Assert.Equal(5, filtered.Count);
        // Assert the full descending order: newest (SORT-W-5) → oldest (SORT-W-1)
        Assert.Equal(new[] { "SORT-W-5", "SORT-W-4", "SORT-W-3", "SORT-W-2", "SORT-W-1" }, filtered);

        // Toggle When to ascending.
        await runsPage.ClickSortHeaderAsync("When");
        var idsAsc = await runsPage.GetVisibleIssueIdentifiersAsync();
        var filteredAsc = idsAsc.Where(id => id.StartsWith("SORT-W-")).ToList();
        Assert.Equal(5, filteredAsc.Count);
        // Assert the full ascending order: oldest (SORT-W-1) → newest (SORT-W-5)
        Assert.Equal(new[] { "SORT-W-1", "SORT-W-2", "SORT-W-3", "SORT-W-4", "SORT-W-5" }, filteredAsc);

        // Toggle back to descending.
        await runsPage.ClickSortHeaderAsync("When");
        var idsDesc2 = await runsPage.GetVisibleIssueIdentifiersAsync();
        var filteredDesc2 = idsDesc2.Where(id => id.StartsWith("SORT-W-")).ToList();
        Assert.Equal(new[] { "SORT-W-5", "SORT-W-4", "SORT-W-3", "SORT-W-2", "SORT-W-1" }, filteredDesc2);
    }

    /// <summary>
    /// Scenario 3b: Duration ascending sorts the shortest run first.
    /// </summary>
    [Fact]
    public async Task Runs_Scenario3b_Duration_SortingAscending()
    {
        var now = DateTimeOffset.UtcNow;

        // Seed 4 runs with explicit durations: 10s, 30s, 2m (120s), 5m (300s)
        var durations = new[] { (id: "DUR-10", secs: 10), (id: "DUR-30", secs: 30),
                                (id: "DUR-120", secs: 120), (id: "DUR-300", secs: 300) };

        foreach (var (id, secs) in durations)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), id, PipelineStep.Completed, now.AddMinutes(-10),
                    durationSeconds: secs));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // Click Duration header once → ascending (shortest first)
        await runsPage.ClickSortHeaderAsync("Duration");
        var ids = await runsPage.GetVisibleIssueIdentifiersAsync();
        var filtered = ids.Where(id => id.StartsWith("DUR-")).ToList();
        Assert.Equal(4, filtered.Count);
        // Shortest (10s) must come before longest (300s)
        // TODO [WARNING]: Only the two extreme positions (DUR-10 vs DUR-300) are checked. A partial-sort
        // defect that leaves DUR-30 and DUR-120 in the wrong relative order would go undetected.
        // Consider asserting the full expected ordering: [DUR-10, DUR-30, DUR-120, DUR-300].
        Assert.True(filtered.IndexOf("DUR-10") < filtered.IndexOf("DUR-300"),
            "DUR-10 (10s) must appear before DUR-300 (300s) when sorted by Duration asc");

        // Toggle to descending → longest first
        await runsPage.ClickSortHeaderAsync("Duration");
        var idsDesc = await runsPage.GetVisibleIssueIdentifiersAsync();
        var filteredDesc = idsDesc.Where(id => id.StartsWith("DUR-")).ToList();
        // TODO [WARNING]: Same partial-assertion caveat as above for the descending direction.
        Assert.True(filteredDesc.IndexOf("DUR-300") < filteredDesc.IndexOf("DUR-10"),
            "DUR-300 (300s) must appear before DUR-10 (10s) when sorted by Duration desc");
    }

    // ── Scenario 4: Paging ────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 4: 60 runs seeded.
    /// - Page 1 (25 rows): Prev disabled, Next enabled.
    /// - Page 2 (25 rows): Prev enabled, no IDs from page 1 present.
    /// - Page 3 (10 rows): Next disabled.
    /// - Filters applied before paging survive the page turn.
    /// </summary>
    [Fact]
    public async Task Runs_Scenario4_Paging_NoDuplicatesAndBoundaryButtons()
    {
        var now = DateTimeOffset.UtcNow;
        const int total = 60;
        const int pageSize = 25;

        // Guard: InMemoryPipelineRunHistoryService.GetRunHistoryAsync filters by IsTerminal().
        // If Completed ever stops being terminal, all 60 rows would be invisible and every
        // assertion in this test would fail with a confusing count mismatch rather than an
        // obvious failure message. Assert this invariant up-front so the failure is immediate.
        // TODO [WARNING]: If PipelineStep.Completed stops being terminal the guard below will
        // fail first, making the root cause obvious rather than a mysterious count mismatch later.
        Assert.True(PipelineStep.Completed.IsTerminal(),
            "PipelineStep.Completed must be terminal for the seeded runs to appear in GetRunHistoryAsync. " +
            "If this assertion fails, update the seeded step to a terminal step (e.g. PrMerged).");

        // Seed 60 Implementation / manual runs with staggered timestamps so they land in a
        // deterministic order on the default When-desc sort.
        for (var i = 1; i <= total; i++)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), $"PG-{i:D3}", PipelineStep.Completed,
                    now.AddSeconds(-i)));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // Page 1 assertions
        Assert.True(await runsPage.IsPrevDisabledAsync(),
            "Prev must be disabled on page 1");
        Assert.False(await runsPage.IsNextDisabledAsync(),
            "Next must be enabled when more pages exist");
        Assert.Equal(pageSize, await runsPage.GetRowCountAsync());

        var page1Ids = await runsPage.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(pageSize, page1Ids.Count);

        // Go to page 2
        await runsPage.GoToNextPageAsync();

        Assert.False(await runsPage.IsPrevDisabledAsync(),
            "Prev must be enabled on page 2");
        Assert.False(await runsPage.IsNextDisabledAsync(),
            "Next must be enabled on page 2 (page 3 exists)");
        Assert.Equal(pageSize, await runsPage.GetRowCountAsync());

        var page2Ids = await runsPage.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(pageSize, page2Ids.Count);

        // No overlap between page 1 and page 2
        var overlap = page1Ids.Intersect(page2Ids).ToList();
        Assert.Empty(overlap);

        // Go to page 3 (last)
        await runsPage.GoToNextPageAsync();

        Assert.False(await runsPage.IsPrevDisabledAsync(),
            "Prev must be enabled on page 3");
        Assert.True(await runsPage.IsNextDisabledAsync(),
            "Next must be disabled on the last page");

        var page3Ids = await runsPage.GetVisibleIssueIdentifiersAsync();
        // 60 total − 25 page1 − 25 page2 = 10 on page 3
        Assert.Equal(total - 2 * pageSize, page3Ids.Count);

        // No overlap between page 3 and pages 1/2
        // TODO [WARNING]: The intermediate pairwise overlap checks add limited value over the
        // final Distinct().Count() == 60 assertion below, which already guarantees full coverage
        // with no duplicates across all three pages. The intermediate checks are kept for
        // early-failure diagnostic clarity.
        var overlap3 = page3Ids.Intersect(page1Ids.Concat(page2Ids)).ToList();
        Assert.Empty(overlap3);

        // Together all three pages cover exactly 60 unique IDs
        var allIds = page1Ids.Concat(page2Ids).Concat(page3Ids).ToList();
        Assert.Equal(total, allIds.Distinct().Count());
    }

    /// <summary>
    /// Scenario 4 (filter persistence): Applying a Type filter on page 1, then paging to
    /// page 2, still shows only the filtered type.
    ///
    /// Seed layout (InMemoryPipelineRunHistoryService uses Insert(0,...), so last-inserted
    /// item is at index 0):
    ///   - 30 Implementation runs seeded first → stored at indices 29..0 after Insert(0,…)
    ///   - 30 Review runs seeded second → Review-030..Review-001 pushed to indices 0..29,
    ///     Implementation-030..Implementation-001 pushed to indices 30..59
    ///
    /// Resulting _history order: [Rev-030, …, Rev-001, Impl-030, …, Impl-001]
    ///   Page 1 (server rows 0–24):  25 Review rows
    ///   Page 2 (server rows 25–49): 5 Review + 20 Implementation rows
    ///   Page 3 (server rows 50–59): 10 Implementation rows
    ///
    /// With Type=Review client-side filter:
    ///   Page 1 shows 25 rows (all Review — filter keeps all)
    ///   Page 2 shows 5 rows  (only the 5 Review rows pass; the 20 Impl rows are hidden)
    ///   → This is a real discriminating assertion: on page 2 the filter must actively
    ///     remove 20 rows rather than being a no-op.
    /// </summary>
    // TODO [WARNING]: This test depends on InMemoryPipelineRunHistoryService using Insert(0,...) (prepend)
    // so that later-seeded Review runs occupy page 1. If the service changes to append order or sorts by
    // StartedAtOffset, the Implementation runs (which have newer timestamps: AddSeconds(-i)) would land on
    // page 1 instead, breaking the Assert.Equal(25, page1FilteredCount) and Assert.All(...StartsWith("PF-REV-"))
    // assertions with a confusing count mismatch. To remove this fragility, seed timestamps so that the
    // expected page composition is correct under both insertion-order and timestamp-descending ordering
    // (e.g. give all Review runs newer timestamps than all Implementation runs).
    [Fact]
    public async Task Runs_Scenario4b_FiltersPersistAcrossPages()
    {
        var now = DateTimeOffset.UtcNow;

        // Seed Implementation first so they end up AFTER Review in insertion order.
        for (var i = 1; i <= 30; i++)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), $"PF-IMPL-{i:D3}", PipelineStep.Completed,
                    now.AddSeconds(-i),
                    runType: PipelineRunType.Implementation));

        // Seed Review second; Insert(0,...) pushes them to the front of _history.
        for (var i = 1; i <= 30; i++)
            await Fixture.HistoryService.AddRunSummaryAsync(
                MakeRun(NewRunId(), $"PF-REV-{i:D3}", PipelineStep.Completed,
                    now.AddSeconds(-i - 100),
                    runType: PipelineRunType.Review));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // Verify baseline without filter: page 1 has 25 rows (first 25 in insertion order = all Review).
        Assert.Equal(25, await runsPage.GetRowCountAsync());

        // Apply Type=Review client-side filter.
        // Page 1 server rows are all Review → all 25 pass the filter.
        await runsPage.SelectTypeFilterAsync("Review");
        var page1FilteredCount = await runsPage.GetRowCountAsync();
        Assert.Equal(25, page1FilteredCount);
        var page1Ids = await runsPage.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(25, page1Ids.Count);
        Assert.All(page1Ids, id => Assert.StartsWith("PF-REV-", id));

        // Navigate to page 2 (server-side). The Type=Review filter must still be active.
        await runsPage.GoToNextPageAsync();

        // Page 2 server rows: Rev-005..Rev-001 (5 rows) + Impl-030..Impl-006 (20 rows).
        // With Type=Review filter: only the 5 Review rows pass — the 20 Impl rows are hidden.
        // This is the key discriminating assertion: on page 2 the filter actively removes rows.
        var page2FilteredCount = await runsPage.GetRowCountAsync();
        Assert.Equal(5, page2FilteredCount);

        // Verify only Review rows are visible on page 2
        var page2Ids = await runsPage.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(5, page2Ids.Count);
        Assert.All(page2Ids, id => Assert.StartsWith("PF-REV-", id));

        // No overlap between page 1 and page 2 Review IDs
        var overlap = page1Ids.Intersect(page2Ids).ToList();
        Assert.Empty(overlap);

        // Clear the filter — page 2 should now show all 25 server rows (5 Review + 20 Impl)
        await runsPage.SelectTypeFilterAsync("");
        Assert.Equal(25, await runsPage.GetRowCountAsync());
    }

    // ── Scenario 5: Navigation and links ─────────────────────────────────────

    /// <summary>
    /// Scenario 5a: Clicking a run row opens the Run detail page at /runs/{id}.
    /// </summary>
    [Fact]
    public async Task Runs_Scenario5a_RowClick_OpensRunDetailPage()
    {
        var now = DateTimeOffset.UtcNow;
        var runId = NewRunId();

        await Fixture.HistoryService.AddRunSummaryAsync(
            MakeRun(runId, "NAV-001", PipelineStep.Completed, now.AddMinutes(-5)));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        Assert.True(await runsPage.IsRunVisibleAsync("NAV-001"),
            "Run NAV-001 must be visible before clicking");

        await runsPage.ClickRunRowAsync("NAV-001");

        // After clicking, URL must contain /runs/{runId}
        // TODO [WARNING]: This only checks URL navigation, not that the detail page loaded without
        // error. If the RunDetail page renders a "Run not found" error state, this assertion still
        // passes. Consider adding a WaitForSelectorAsync on a known success element (e.g. the run
        // detail heading) to verify successful page load.
        Assert.Contains($"/runs/{runId}", Page.Url);
    }

    /// <summary>
    /// Scenario 5b: Issue and PR link columns contain the seeded URLs.
    /// </summary>
    [Fact]
    public async Task Runs_Scenario5b_IssueLinkAndPrLink_PointToSeededUrls()
    {
        var now = DateTimeOffset.UtcNow;
        const string issueUrl = "https://github.com/e2e-org/repo/issues/42";
        const string prUrl    = "https://github.com/e2e-org/repo/pull/99";

        await Fixture.HistoryService.AddRunSummaryAsync(
            MakeRun(NewRunId(), "LINK-001", PipelineStep.Completed, now.AddMinutes(-3),
                issueUrl: issueUrl, pullRequestUrl: prUrl));

        // No issue URL, no PR URL — links column must be empty
        await Fixture.HistoryService.AddRunSummaryAsync(
            MakeRun(NewRunId(), "LINK-002", PipelineStep.Completed, now.AddMinutes(-4)));

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();

        // Verify LINK-001 has both links
        var issueHref = await runsPage.GetIssueLinkHrefAsync("LINK-001");
        Assert.Equal(issueUrl, issueHref);

        var prHref = await runsPage.GetPrLinkHrefAsync("LINK-001");
        Assert.Equal(prUrl, prHref);

        // Verify LINK-002 has no links
        var issueHref2 = await runsPage.GetIssueLinkHrefAsync("LINK-002");
        Assert.Null(issueHref2);

        var prHref2 = await runsPage.GetPrLinkHrefAsync("LINK-002");
        Assert.Null(prHref2);
    }
}

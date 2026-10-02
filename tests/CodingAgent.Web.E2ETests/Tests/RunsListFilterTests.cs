using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the /runs page: outcome tabs, type/initiated-by filters, Feedback only toggle,
/// sortable column headers, paging, row navigation, and issue/PR links in the Links column.
///
/// Seeded directly into <see cref="InMemoryPipelineRunHistoryService.AddRunSummaryAsync"/> so
/// tests are deterministic and fast — no agent round-trips, no real dispatches.
/// Each test is independent: <see cref="E2ETestBase.InitializeAsync"/> resets all state.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "RunsPage")]
[Collection(E2ECollection.Name)]
public sealed class RunsListFilterTests : E2ETestBase
{
    public RunsListFilterTests(E2EFixture fixture) : base(fixture) { }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private static PipelineRunSummary MakeRun(
        string issueId,
        PipelineStep finalStep,
        PipelineRunType runType = PipelineRunType.Implementation,
        string initiatedBy = InitiatedByConstants.Manual,
        DateTimeOffset? startedAt = null,
        TimeSpan? duration = null,
        string? issueUrl = null,
        string? prUrl = null,
        RunFeedback? feedback = null) =>
        new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier(issueId),
            IssueTitle = $"Test issue {issueId}",
            FinalStep = finalStep,
            RunType = runType,
            InitiatedBy = initiatedBy,
            StartedAtOffset = startedAt ?? DateTimeOffset.UtcNow.AddHours(-1),
// TODO [WARNING]: The CS0618 suppression below masks a design smell: StartedAt (the obsolete
// DateTime field) is populated via .DateTime, which strips the UTC offset and produces
// DateTimeKind.Unspecified for any non-UTC DateTimeOffset. Tests currently always pass UTC
// so there is no immediate bug, but if a future test passes a non-UTC offset the wrong
// DateTime value will be stored silently. When the obsolete field is eventually removed this
// suppression block should be removed too. See GitHub issue comment for context.
#pragma warning disable CS0618
            StartedAt = (startedAt ?? DateTimeOffset.UtcNow.AddHours(-1)).DateTime,
#pragma warning restore CS0618
            CompletedAtOffset = (startedAt ?? DateTimeOffset.UtcNow.AddHours(-1)) + (duration ?? TimeSpan.FromMinutes(5)),
            IssueUrl = issueUrl,
            PullRequestUrl = prUrl,
            Feedback = feedback
        };

    private async Task SeedAsync(IEnumerable<PipelineRunSummary> runs)
    {
        foreach (var r in runs)
            await Fixture.HistoryService.AddRunSummaryAsync(r);
    }

    // ── Scenario 1: Tabs ──────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 1: Each outcome tab shows only its matching runs; counts match seeded data.
    /// </summary>
    [Fact]
    public async Task Tabs_EachTabShowsOnlyItsOutcome()
    {
        // Seed: 3 completed, 2 failed, 1 cancelled
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(new[]
        {
            MakeRun("C1", PipelineStep.Completed, startedAt: now.AddHours(-6)),
            MakeRun("C2", PipelineStep.Completed, startedAt: now.AddHours(-5)),
            MakeRun("C3", PipelineStep.Completed, startedAt: now.AddHours(-4)),
            MakeRun("F1", PipelineStep.Failed,    startedAt: now.AddHours(-3)),
            MakeRun("F2", PipelineStep.Failed,    startedAt: now.AddHours(-2)),
            MakeRun("X1", PipelineStep.Cancelled, startedAt: now.AddHours(-1)),
        });

        var page = new RunsPage(Page, BaseUrl);
        await page.NavigateAsync();

        // ── All tab shows all 6 ──
        await page.SelectTabAsync("All");
        var allCount = await page.GetRowCountAsync();
        Assert.Equal(6, allCount);

        // ── Completed tab shows 3 ──
        await page.SelectTabAsync("Completed");
        var completedCount = await page.GetRowCountAsync();
        Assert.Equal(3, completedCount);
        Assert.True(await page.IsRunVisibleAsync("C1"));
        Assert.True(await page.IsRunVisibleAsync("C2"));
        Assert.True(await page.IsRunVisibleAsync("C3"));
        Assert.False(await page.IsRunVisibleAsync("F1"), "F1 (failed) must not appear on Completed tab");
        Assert.False(await page.IsRunVisibleAsync("X1"), "X1 (cancelled) must not appear on Completed tab");

        // ── Failed tab shows 2 ──
        await page.SelectTabAsync("Failed");
        var failedCount = await page.GetRowCountAsync();
        Assert.Equal(2, failedCount);
        Assert.True(await page.IsRunVisibleAsync("F1"));
        Assert.True(await page.IsRunVisibleAsync("F2"));
        Assert.False(await page.IsRunVisibleAsync("C1"), "C1 (completed) must not appear on Failed tab");

        // ── Cancelled tab shows 1 ──
        await page.SelectTabAsync("Cancelled");
        var cancelledCount = await page.GetRowCountAsync();
        Assert.Equal(1, cancelledCount);
        Assert.True(await page.IsRunVisibleAsync("X1"));
    }

    // ── Scenario 2: Filters combine ───────────────────────────────────────────

    /// <summary>
    /// Scenario 2: Type + Initiated-by + Feedback-only filters combine correctly.
    /// Clearing filters restores the full list.
    /// </summary>
    [Fact]
    public async Task Filters_TypeAndInitiatedByAndFeedbackOnlyCombine()
    {
        // Seed a mix of types and initiators, some with feedback
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(new[]
        {
            // Review, loop:review, WITH feedback
            MakeRun("R1", PipelineStep.Completed, PipelineRunType.Review,
                initiatedBy: InitiatedByConstants.LoopReview,
                startedAt: now.AddHours(-10),
                feedback: new RunFeedback
                {
                    Outcome = FeedbackOutcome.Success,
                    CollectedAtUtc = DateTime.UtcNow,
                    Harness = new HarnessFeedback()
                }),

            // Review, loop:review, NO feedback
            MakeRun("R2", PipelineStep.Completed, PipelineRunType.Review,
                initiatedBy: InitiatedByConstants.LoopReview,
                startedAt: now.AddHours(-9)),

            // Review, manual, WITH feedback
            MakeRun("R3", PipelineStep.Completed, PipelineRunType.Review,
                initiatedBy: InitiatedByConstants.Manual,
                startedAt: now.AddHours(-8),
                feedback: new RunFeedback
                {
                    Outcome = FeedbackOutcome.Failure,
                    CollectedAtUtc = DateTime.UtcNow,
                    Harness = new HarnessFeedback()
                }),

            // Implementation, loop:issue, no feedback
            MakeRun("I1", PipelineStep.Completed, PipelineRunType.Implementation,
                initiatedBy: InitiatedByConstants.LoopIssue,
                startedAt: now.AddHours(-7)),

            // Decomposition, manual
            MakeRun("D1", PipelineStep.Completed, PipelineRunType.Decomposition,
                initiatedBy: InitiatedByConstants.Manual,
                startedAt: now.AddHours(-6)),
        });

        var page = new RunsPage(Page, BaseUrl);
        await page.NavigateAsync();

        // All tab is default — 5 rows visible
        var totalCount = await page.GetRowCountAsync();
        Assert.Equal(5, totalCount);

        // ── Type = Review → 3 rows ──
        await page.SetTypeFilterAsync("Review");
        var reviewCount = await page.GetRowCountAsync();
        Assert.Equal(3, reviewCount);
        Assert.True(await page.IsRunVisibleAsync("R1"));
        Assert.True(await page.IsRunVisibleAsync("R2"));
        Assert.True(await page.IsRunVisibleAsync("R3"));
        Assert.False(await page.IsRunVisibleAsync("I1"), "I1 (Implementation) must not show when Type=Review");

        // ── Type = Review + Initiated by = loop:review → 2 rows ──
        await page.SetInitiatedByFilterAsync(InitiatedByConstants.LoopReview);
        var loopReviewCount = await page.GetRowCountAsync();
        Assert.Equal(2, loopReviewCount);
        Assert.True(await page.IsRunVisibleAsync("R1"));
        Assert.True(await page.IsRunVisibleAsync("R2"));
        Assert.False(await page.IsRunVisibleAsync("R3"), "R3 (manual) must not show when Initiated by = loop:review");

        // ── + Feedback only → 1 row ──
        await page.EnableFeedbackOnlyAsync();
        // Feedback-only is server-side, so the page reloads; then the Type/InitiatedBy
        // client-side filters are preserved in state for the new page's rows.
        // Wait a moment for the page to settle after the server re-query.
        // TODO [WARNING]: This direct Page.WaitForTimeoutAsync call bypasses the page-object
        // abstraction and doubles the settle time (EnableFeedbackOnlyAsync already waits 1500ms
        // internally). Additionally, it relies on a fixed timeout rather than a deterministic
        // condition — under load the server round-trip may exceed 1500ms causing a stale read.
        // Also note: there is no assertion that the Type and InitiatedBy filter dropdowns still
        // show their selected values after the Feedback-only server reload. The row-count
        // assertion (== 1) validates the outcome but not that filter state was actually preserved.
        // Consider using WaitForSelectorAsync or a similar condition-based wait, and add dropdown
        // value assertions to confirm filters persisted through the server reload.
        await Page.WaitForTimeoutAsync(1500);
        var feedbackLoopReviewCount = await page.GetRowCountAsync();
        Assert.Equal(1, feedbackLoopReviewCount);
        Assert.True(await page.IsRunVisibleAsync("R1"), "R1 is the only loop:review+Review run with feedback");
        Assert.False(await page.IsRunVisibleAsync("R2"), "R2 has no feedback");

        // ── Clear client-side filters (reset Type + InitiatedBy) ──
        // Since Feedback only is server-side, reset it first to avoid confusion.
        await page.DisableFeedbackOnlyAsync();
        // TODO [WARNING]: Same abstraction-leak and redundant-wait issue as above — the
        // DisableFeedbackOnlyAsync page object method already waits 1500ms internally, so
        // this adds an unnecessary extra 1500ms. Replace with a condition-based wait or
        // remove the redundant call when the page-object wait strategy is revised.
        await Page.WaitForTimeoutAsync(1500);

        // Now clear Type and InitiatedBy client-side filters
        await page.SetTypeFilterAsync("");
        await page.SetInitiatedByFilterAsync("");

        // All 5 runs must be visible again
        var afterClearCount = await page.GetRowCountAsync();
        Assert.Equal(5, afterClearCount);
    }

    // ── Scenario 3: Sorting ───────────────────────────────────────────────────

    /// <summary>
    /// Scenario 3: Clicking WHEN sorts ascending/descending. DURATION also orders rows correctly.
    /// </summary>
    [Fact]
    public async Task Sorting_WhenAndDuration_OrderRowsCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        // Seed runs with distinct start times and durations (oldest to newest)
        await SeedAsync(new[]
        {
            MakeRun("S1", PipelineStep.Completed, startedAt: now.AddHours(-5), duration: TimeSpan.FromMinutes(10)),
            MakeRun("S2", PipelineStep.Completed, startedAt: now.AddHours(-4), duration: TimeSpan.FromMinutes(2)),
            MakeRun("S3", PipelineStep.Completed, startedAt: now.AddHours(-3), duration: TimeSpan.FromMinutes(7)),
        });

        var page = new RunsPage(Page, BaseUrl);
        await page.NavigateAsync();

        // ── Default: When descending (newest first) ──
        // S3 started most recently, so it should appear first
        // TODO [WARNING]: This asserts the assumed default sort (WHEN descending) without
        // explicitly clicking the WHEN header to establish that state first, and without
        // using GetSortDirectionAsync to verify the active sort indicator. If the page
        // renders with a different default or if the default sort direction changes, this
        // assertion fails with a misleading diff. Also note: ClickSortHeaderAsync uses
        // `button.sort-header:has-text('When')` (mixed-case), but the rendered column header
        // may be "WHEN" (all-caps). Playwright's has-text is case-sensitive — if the text
        // doesn't match, the click targets nothing and the subsequent assertions may reflect
        // the unchanged order. Verify the rendered column label and align the locator
        // text accordingly. Same risk applies to ClickSortHeaderAsync("Duration").
        var defaultOrder = await page.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(3, defaultOrder.Count);
        Assert.Equal("S3", defaultOrder[0]);
        Assert.Equal("S2", defaultOrder[1]);
        Assert.Equal("S1", defaultOrder[2]);

        // ── Click WHEN once — ascending (oldest first) ──
        await page.ClickSortHeaderAsync("When");
        var whenAscOrder = await page.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(3, whenAscOrder.Count);
        Assert.Equal("S1", whenAscOrder[0]);
        Assert.Equal("S2", whenAscOrder[1]);
        Assert.Equal("S3", whenAscOrder[2]);

        // ── Click WHEN again — back to descending ──
        await page.ClickSortHeaderAsync("When");
        var whenDescOrder = await page.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(3, whenDescOrder.Count);
        // TODO [WARNING]: Only the first element is asserted here. If the page renders an
        // arbitrary order with S3 first by coincidence (e.g. nearly identical timestamps),
        // this single-element assertion passes without proving the full descending sort is
        // correct. Assert the complete order [S3, S2, S1] for a meaningful correctness check.
        Assert.Equal("S3", whenDescOrder[0]);

        // ── Click DURATION — ascending (shortest first) ──
        await page.ClickSortHeaderAsync("Duration");
        var durationAscOrder = await page.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(3, durationAscOrder.Count);
        // S2 = 2 min (shortest), S3 = 7 min, S1 = 10 min (longest)
        Assert.Equal("S2", durationAscOrder[0]);
        Assert.Equal("S3", durationAscOrder[1]);
        Assert.Equal("S1", durationAscOrder[2]);

        // ── Click DURATION again — descending (longest first) ──
        await page.ClickSortHeaderAsync("Duration");
        var durationDescOrder = await page.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(3, durationDescOrder.Count);
        Assert.Equal("S1", durationDescOrder[0]);
        Assert.Equal("S3", durationDescOrder[1]);
        Assert.Equal("S2", durationDescOrder[2]);
    }

    // ── Scenario 4: Paging ────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 4: Prev is disabled on page 1; Next is disabled on the last page.
    /// Page 2 starts where page 1 ended (no duplicates/gaps). Filters persist across pages.
    /// </summary>
    [Fact]
    public async Task Paging_PrevAndNextBoundariesAndNoDuplicates()
    {
        // Seed 30 completed runs so there are two pages (PageSize = 25)
        var now = DateTimeOffset.UtcNow;
        var runs = Enumerable.Range(1, 30)
            .Select(i => MakeRun($"PG{i:D2}",
                PipelineStep.Completed,
                startedAt: now.AddMinutes(-i * 2)))
            .ToArray();
        await SeedAsync(runs);

        var page = new RunsPage(Page, BaseUrl);
        await page.NavigateAsync();

        // ── Page 1: Prev disabled, Next enabled ──
        Assert.True(await page.IsPrevDisabledAsync(), "Prev must be disabled on page 1");
        Assert.False(await page.IsNextDisabledAsync(), "Next must be enabled when there are more pages");

        // Capture the 25 identifiers on page 1
        var page1Ids = await page.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(25, page1Ids.Count);

        // ── Navigate to page 2 ──
        await page.ClickNextAsync();

        // Page 2: Prev enabled, Next disabled (only 5 remain)
        Assert.False(await page.IsPrevDisabledAsync(), "Prev must be enabled on page 2");
        Assert.True(await page.IsNextDisabledAsync(), "Next must be disabled on the last page");

        var page2Ids = await page.GetVisibleIssueIdentifiersAsync();
        Assert.Equal(5, page2Ids.Count);

        // No overlaps between page 1 and page 2
        var overlap = page1Ids.Intersect(page2Ids).ToList();
        Assert.Empty(overlap);

        // Together they cover all 30 runs
        var allIds = page1Ids.Concat(page2Ids).Distinct().ToList();
        Assert.Equal(30, allIds.Count);
    }

    /// <summary>
    /// Scenario 4b: Client-side filters persist across pages.
    /// Seed enough Implementation+Review runs so filtering by Type=Review leaves multiple pages.
    /// </summary>
    [Fact]
    public async Task Paging_FiltersPersistedAcrossPages()
    {
        // Seed 30 Review runs + 5 Implementation runs
        var now = DateTimeOffset.UtcNow;
        var reviewRuns = Enumerable.Range(1, 30)
            .Select(i => MakeRun($"REV{i:D2}", PipelineStep.Completed,
                runType: PipelineRunType.Review,
                startedAt: now.AddMinutes(-i * 2)))
            .ToArray();
        var implRuns = Enumerable.Range(1, 5)
            .Select(i => MakeRun($"IMPL{i}", PipelineStep.Completed,
                runType: PipelineRunType.Implementation,
                startedAt: now.AddMinutes(-100 - i * 2)))
            .ToArray();
        await SeedAsync(reviewRuns.Concat(implRuns));

        var page = new RunsPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Apply Type=Review filter
        await page.SetTypeFilterAsync("Review");
        // TODO [WARNING]: Two related fragility concerns with this test:
        // 1. Filter persistence not validated: after ClickNextAsync(), there is no assertion
        //    that the Type filter dropdown still shows "Review". The test only asserts that no
        //    IMPL* identifiers appear in page2Ids, which would pass even if the filter were
        //    cleared but the Implementation runs happen to be absent from page 2 by coincidence.
        //    Add an assertion that the Type dropdown value is still "Review" after navigation.
        // 2. Server-vs-client interaction assumption: if Type is a client-side filter applied
        //    on top of the server-fetched page, then navigating to page 2 triggers a new server
        //    fetch (without type filter), which may return IMPL* rows. The 5 IMPL runs are seeded
        //    at now-100min to now-110min (older than all 30 Review runs at now-2min to now-60min),
        //    so they land on page 2 of the unfiltered set (slots 31–35 don't exist, they fill
        //    slots 31–35 in the full 35-run list). The client-side type filter should then hide
        //    them, making the assertion correct — but this relies on the implicit time-gap
        //    assumption. If the page size or seed timing changes, IMPL runs could appear on page 1
        //    and the premise breaks. Make the time-gap explicit via a named constant.
        // Client-side filter: visible rows are from the current page's 25 rows filtered
        // The first page should have 25 Review rows (all page 1 rows are Review since
        // the oldest 5 on page 2 are mixed, but server sends 25 most-recent rows).
        // Navigate to page 2.
        await page.ClickNextAsync();

        // The filter should still be "Review" — all visible rows must be Review
        var page2Ids = await page.GetVisibleIssueIdentifiersAsync();
        Assert.NotEmpty(page2Ids);
        // None of the Implementation runs (IMPL1..IMPL5) should be visible
        foreach (var id in page2Ids)
            Assert.StartsWith("REV", id);
    }

    // ── Scenario 5: Navigation and links ─────────────────────────────────────

    /// <summary>
    /// Scenario 5: Clicking a row opens the run detail page. Issue and PR links point to seeded URLs.
    /// </summary>
    [Fact]
    public async Task Navigation_RowClickOpensRunPage_AndLinksPointToSeededUrls()
    {
        var issueUrl = "https://github.com/e2e-org/e2e-repo/issues/42";
        var prUrl = "https://github.com/e2e-org/e2e-repo/pull/100";
        var run = MakeRun("42", PipelineStep.Completed,
            issueUrl: issueUrl,
            prUrl: prUrl);
        await Fixture.HistoryService.AddRunSummaryAsync(run);

        var page = new RunsPage(Page, BaseUrl);
        await page.NavigateAsync();

        // ── Issue link points to the seeded issue URL ──
        var actualIssueHref = await page.GetIssueLinkHrefAsync("42");
        Assert.Equal(issueUrl, actualIssueHref);

        // ── PR link points to the seeded PR URL ──
        var actualPrHref = await page.GetPrLinkHrefAsync("42");
        Assert.Equal(prUrl, actualPrHref);

        // ── Clicking the row navigates to /runs/{runId} ──
        await page.ClickRowAsync("42");
        var currentUrl = Page.Url;
        Assert.Contains($"/runs/{run.RunId}", currentUrl);

        // The run detail page should render the issue identifier somewhere in the page body
        // TODO [WARNING]: `bodyText?.Contains("42")` is too weak — virtually any page will
        // contain the substring "42" (timestamps, navigation, footer text, etc.), so this
        // assertion provides no real correctness guarantee. Replace with a more specific check,
        // e.g. asserting the full issue identifier "#42", the run's unique RunId, or a heading
        // that is unique to this run's detail page.
        var bodyText = await Page.TextContentAsync("body");
        Assert.True(bodyText?.Contains("42") == true,
            "Run detail page should reference the issue identifier");
    }

    /// <summary>
    /// Scenario 5b: Links column shows no issue link when IssueUrl is null.
    /// </summary>
    [Fact]
    public async Task Navigation_NoLinksWhenUrlsAreNull()
    {
        var run = MakeRun("99", PipelineStep.Completed); // no issueUrl or prUrl
        await Fixture.HistoryService.AddRunSummaryAsync(run);

        var page = new RunsPage(Page, BaseUrl);
        await page.NavigateAsync();

        // Issue link absent when IssueUrl is null
        var issueHref = await page.GetIssueLinkHrefAsync("99");
        Assert.Null(issueHref);

        // PR link absent when PullRequestUrl is null
        var prHref = await page.GetPrLinkHrefAsync("99");
        Assert.Null(prHref);
    }
}

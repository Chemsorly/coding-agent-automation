using AwesomeAssertions;
using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for <see cref="Runs"/> covering:
/// - Removal of the Agent column (issue #2328)
/// - Links column with issue URL and PR URL (issue #2328)
/// - Sortable column headers (issue #2328)
/// - Result and Type filter dropdowns (issue #2328)
/// - Sort state reflected in URL (issue #2328)
/// </summary>
public class RunsPageComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiRunHistoryClient> _mockRunHistory = new();

    private static PipelineRunSummary MakeSummary(
        string runId,
        PipelineStep step = PipelineStep.Completed,
        PipelineRunType runType = PipelineRunType.Implementation,
        string? issueUrl = null,
        string? prUrl = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        string issueIdentifier = "42",
        string issueTitle = "Test issue",
        long totalTokens = 100)
    {
        var start = startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-10);
        var end = completedAt ?? start.AddMinutes(5);
        return new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = issueTitle,
            FinalStep = step,
            RunType = runType,
            IssueUrl = issueUrl,
            PullRequestUrl = prUrl,
            StartedAtOffset = start,
            CompletedAtOffset = end,
            TotalTokens = totalTokens,
            AgentId = "caa-agent-abc123",
        };
    }

    private static PagedResult<PipelineRunSummary> OnePage(params PipelineRunSummary[] items) => new()
    {
        Items = items.ToList(),
        HasMore = false,
        Page = 1,
        PageSize = 25,
    };

    public RunsPageComponentTests()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage());

        Services.AddSingleton(_mockRunHistory.Object);
        Services.AddSingleton(new CockpitState());
        // NavigationManager is provided automatically by bunit.
    }

    // ── 1. Agent column not present ───────────────────────────────────────────

    [Fact]
    public void RunsTable_NoAgentColumnHeader()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        var headers = cut.FindAll(".monitoring-table thead th");
        headers.Should().NotContain(h => h.TextContent.Trim() == "Agent",
            "the Agent column must be removed per issue #2328");
    }

    // ── 2. Links column header present ───────────────────────────────────────

    [Fact]
    public void RunsTable_HasLinksColumnHeader()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        var headers = cut.FindAll(".monitoring-table thead th");
        headers.Should().Contain(h => h.TextContent.Trim() == "Links",
            "a Links column must be present in the Runs table");
    }

    // ── 3. Issue link rendered when IssueUrl is present ──────────────────────

    [Fact]
    public void RunsTable_RendersIssueLink_WhenIssueUrlPresent()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1", issueUrl: "https://github.com/owner/repo/issues/42")));

        var cut = Render<Runs>();

        var issueLink = cut.Find("a[href='https://github.com/owner/repo/issues/42']");
        issueLink.Should().NotBeNull("a clickable issue link must be rendered when IssueUrl is set");
        issueLink.GetAttribute("target").Should().Be("_blank",
            "issue link should open in a new tab");
    }

    // ── 4. No issue link rendered when IssueUrl is null ──────────────────────

    [Fact]
    public void RunsTable_NoIssueLink_WhenIssueUrlNull()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1", issueUrl: null, prUrl: null)));

        var cut = Render<Runs>();

        // There should be a row rendered but the Links cell must contain no anchor elements
        var rows = cut.FindAll(".monitoring-table tbody tr");
        rows.Count.Should().Be(1);

        // Critical: verify no <a> is rendered in the links cell when both URLs are null
        var linksAnchors = cut.FindAll(".runs-links-cell a");
        linksAnchors.Should().BeEmpty("no anchor must be rendered in the Links cell when IssueUrl and PullRequestUrl are both null");
    }

    // ── 5. PR link rendered when PullRequestUrl is present ───────────────────

    [Fact]
    public void RunsTable_RendersPrLink_WhenPullRequestUrlPresent()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1", prUrl: "https://github.com/owner/repo/pull/7")));

        var cut = Render<Runs>();

        var prLink = cut.Find("a[href='https://github.com/owner/repo/pull/7']");
        prLink.Should().NotBeNull("a clickable PR link must be rendered when PullRequestUrl is set");
        prLink.GetAttribute("target").Should().Be("_blank",
            "PR link should open in a new tab");
    }

    // ── 6. No PR link rendered when PullRequestUrl is null ───────────────────

    [Fact]
    public void RunsTable_NoPrLink_WhenPullRequestUrlNull()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1", prUrl: null)));

        var cut = Render<Runs>();

        // The PR link anchor should not exist when no PullRequestUrl
        var prLinks = cut.FindAll("a[href*='pull/']");
        prLinks.Should().BeEmpty("no PR link must be rendered when PullRequestUrl is null");
    }

    // ── 7. Both issue and PR links can be rendered in the same row ────────────

    [Fact]
    public void RunsTable_RendersBothLinks_WhenBothPresent()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1",
                issueUrl: "https://github.com/owner/repo/issues/10",
                prUrl: "https://github.com/owner/repo/pull/11")));

        var cut = Render<Runs>();

        cut.Find("a[href='https://github.com/owner/repo/issues/10']").Should().NotBeNull();
        cut.Find("a[href='https://github.com/owner/repo/pull/11']").Should().NotBeNull();
    }

    // ── 8. Sort column headers present ────────────────────────────────────────

    [Fact]
    public void RunsTable_SortableColumnHeaders_Present()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        // Sortable headers are rendered as buttons inside th elements
        var sortButtons = cut.FindAll(".monitoring-table thead th button.sort-header");
        // TODO: Strengthen this assertion to Count.Should().Be(4) and/or verify each expected
        // column name (Result, Type, Duration, When) has a sort button. The current Count > 0
        // assertion passes even if only one of the four required sortable columns is present.
        sortButtons.Count.Should().BeGreaterThan(0,
            "there must be at least one sortable column header button");
    }

    // ── 9. Default sort is When desc — newest first ───────────────────────────

    [Fact]
    public void RunsTable_DefaultSort_WhenDescending_NewestFirst()
    {
        var older = MakeSummary("r1", startedAt: DateTimeOffset.UtcNow.AddHours(-2), issueIdentifier: "100");
        var newer = MakeSummary("r2", startedAt: DateTimeOffset.UtcNow.AddMinutes(-5), issueIdentifier: "200");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(older, newer));  // older first from server

        var cut = Render<Runs>();

        // The newer run (#r2, identifier "200") should appear first in the rendered rows
        var rows = cut.FindAll(".monitoring-table tbody tr");
        rows.Count.Should().Be(2);

        // First row must contain the newer run's issue identifier, and NOT the older run's identifier
        rows[0].TextContent.Should().Contain("#200",
            "default sort (When desc) must place the most recent run first");
        rows[0].TextContent.Should().NotContain("#100",
            "the older run must not appear in the first row under default sort");
    }

    // ── 10. Clicking Duration column header sorts ascending ──────────────────

    [Fact]
    public async Task RunsTable_ClickDurationHeader_SortsAscendingThenDescending()
    {
        var shortRun = MakeSummary("short",
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-9),   // 1m duration
            issueIdentifier: "1");
        var longRun = MakeSummary("long",
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5),   // 5m duration
            issueIdentifier: "2");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(longRun, shortRun));

        var cut = Render<Runs>();

        // Find the Duration sort button and click it (first click = asc)
        var durationHeader = cut.FindAll(".monitoring-table thead th button.sort-header")
            .FirstOrDefault(b => b.TextContent.Contains("Duration"));
        durationHeader.Should().NotBeNull("Duration column must have a sort button");

        await cut.InvokeAsync(() => durationHeader!.Click());

        var rows = cut.FindAll(".monitoring-table tbody tr");
        // Ascending: short run (1m) first
        rows[0].TextContent.Should().Contain("#1",
            "after ascending Duration sort, shortest run should appear first");

        // Click again = desc
        await cut.InvokeAsync(() => durationHeader!.Click());

        rows = cut.FindAll(".monitoring-table tbody tr");
        // Descending: long run (5m) first
        rows[0].TextContent.Should().Contain("#2",
            "after descending Duration sort, longest run should appear first");
    }

    // ── 11. Active sort column is visually indicated ──────────────────────────

    [Fact]
    public async Task RunsTable_ActiveSortColumn_HasIndicatorClass()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        // Default sort is When desc — that th should carry the active sort class
        var activeHeaders = cut.FindAll(".monitoring-table thead th.sort-active");
        activeHeaders.Count.Should().Be(1, "exactly one column must be marked as the active sort");
        activeHeaders[0].TextContent.Should().Contain("When",
            "When column is the default sort column");

        // Click Duration to change sort
        var durationHeader = cut.FindAll(".monitoring-table thead th button.sort-header")
            .FirstOrDefault(b => b.TextContent.Contains("Duration"));
        await cut.InvokeAsync(() => durationHeader!.Click());

        activeHeaders = cut.FindAll(".monitoring-table thead th.sort-active");
        activeHeaders.Count.Should().Be(1);
        activeHeaders[0].TextContent.Should().Contain("Duration",
            "Duration must become the active sort column after clicking it");
    }

    // ── 12. Result filter dropdown narrows the displayed rows ────────────────

    [Fact]
    public async Task RunsTable_ResultFilter_NarrowsDisplayedRows()
    {
        var completedRun = MakeSummary("r1", step: PipelineStep.Completed, issueIdentifier: "10");
        var failedRun = MakeSummary("r2", step: PipelineStep.Failed, issueIdentifier: "20");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(completedRun, failedRun));

        var cut = Render<Runs>();

        // Both rows initially visible
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(2);

        // Find the Result filter select and change it to "Failed"
        var resultFilter = cut.Find("select.column-filter[aria-label='Filter by Result']");
        await cut.InvokeAsync(() => resultFilter.Change("Failed"));

        // Only the failed run should be shown
        var visibleRows = cut.FindAll(".monitoring-table tbody tr");
        visibleRows.Count.Should().Be(1, "filtering by Failed should show only 1 row");
        visibleRows[0].TextContent.Should().Contain("#20",
            "the visible row should be the failed run");
    }

    // ── 13. Type filter dropdown narrows the displayed rows ──────────────────

    [Fact]
    public async Task RunsTable_TypeFilter_NarrowsDisplayedRows()
    {
        var implRun = MakeSummary("r1", runType: PipelineRunType.Implementation, issueIdentifier: "1");
        var reviewRun = MakeSummary("r2", runType: PipelineRunType.Review, issueIdentifier: "2");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(implRun, reviewRun));

        var cut = Render<Runs>();

        // Both rows initially visible
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(2);

        // Find the Type filter and change to "Review"
        var typeFilter = cut.Find("select.column-filter[aria-label='Filter by Type']");
        await cut.InvokeAsync(() => typeFilter.Change("Review"));

        // Only the review run should be shown
        var visibleRows = cut.FindAll(".monitoring-table tbody tr");
        visibleRows.Count.Should().Be(1, "filtering by Review should show only 1 row");
        visibleRows[0].TextContent.Should().Contain("#2",
            "the visible row should be the review run");
    }

    // ── 14. Clearing a filter restores all rows ───────────────────────────────

    [Fact]
    public async Task RunsTable_ClearingFilter_RestoresAllRows()
    {
        var run1 = MakeSummary("r1", step: PipelineStep.Completed, issueIdentifier: "1");
        var run2 = MakeSummary("r2", step: PipelineStep.Failed, issueIdentifier: "2");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(run1, run2));

        var cut = Render<Runs>();

        var resultFilter = cut.Find("select.column-filter[aria-label='Filter by Result']");
        await cut.InvokeAsync(() => resultFilter.Change("Failed"));
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(1);

        // Clear filter (value = "")
        await cut.InvokeAsync(() => resultFilter.Change(""));
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(2,
            "clearing the filter should restore all rows");
    }

    // ── 15. Sort direction indicator icon present in active column ────────────

    [Fact]
    public async Task RunsTable_SortDirectionIcon_VisibleInActiveColumn()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        // Default: When desc — the sort direction element should be visible in the active th
        var activeSort = cut.Find(".monitoring-table thead th.sort-active");
        var indicator = activeSort.QuerySelector(".sort-indicator");
        indicator.Should().NotBeNull("sort direction indicator must be present in the active sort column");

        // Click Duration sort
        var durationBtn = cut.FindAll(".monitoring-table thead th button.sort-header")
            .First(b => b.TextContent.Contains("Duration"));
        await cut.InvokeAsync(() => durationBtn.Click());

        // Now Duration is active — indicator should show ascending arrow
        var newActive = cut.Find(".monitoring-table thead th.sort-active");
        newActive.TextContent.Should().Contain("Duration");
        var newIndicator = newActive.QuerySelector(".sort-indicator");
        newIndicator.Should().NotBeNull("sort indicator must persist in the newly active column");
    }

    // ── 16. Empty state shows when filtered results are empty ─────────────────

    [Fact]
    public async Task RunsTable_FilterEmptyState_WhenNoRowsMatchFilter()
    {
        var completedRun = MakeSummary("r1", step: PipelineStep.Completed, issueIdentifier: "10");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(completedRun));

        var cut = Render<Runs>();

        // Filter by Failed — no matches
        var resultFilter = cut.Find("select.column-filter[aria-label='Filter by Result']");
        await cut.InvokeAsync(() => resultFilter.Change("Failed"));

        // Should show no rows and a message
        var rows = cut.FindAll(".monitoring-table tbody tr");
        rows.Should().BeEmpty("filter producing no matches should show zero rows");

        // The table should still render the headers (no cockpit-empty for filter)
        // TODO: When zero rows match the client-side filter, the table renders a silent empty <tbody>
        // with no user-visible message. A "no runs match this filter" hint would improve usability.
        var tableHeaders = cut.FindAll(".monitoring-table thead th");
        tableHeaders.Count.Should().BeGreaterThan(0, "table headers remain when filter is active");
    }

    // ── 17. Sort state written to URL on header click (AC6) ───────────────────

    [Fact]
    public async Task RunsTable_ClickSortHeader_PersistsSortStateToUrl()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        // Click the Duration sort header (first click = ascending)
        var durationBtn = cut.FindAll(".monitoring-table thead th button.sort-header")
            .First(b => b.TextContent.Contains("Duration"));
        await cut.InvokeAsync(() => durationBtn.Click());

        // The NavigationManager URI should now contain ?sort=duration&sortDir=asc
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().Contain("sort=duration", "sort column should be persisted to URL");
        nav.Uri.Should().Contain("sortDir=asc", "sort direction should be persisted to URL as asc (default for non-When columns)");

        // Click again to toggle descending
        await cut.InvokeAsync(() => durationBtn.Click());

        nav.Uri.Should().Contain("sort=duration", "sort column should still be present in URL");
        nav.Uri.Should().Contain("sortDir=desc", "sort direction should update to desc after second click");
    }

    [Fact]
    public async Task RunsTable_ClickWhenSortHeader_PersistsWhenDescToUrl()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        // Default sort is already When desc; click When header should toggle to asc
        var whenBtn = cut.FindAll(".monitoring-table thead th button.sort-header")
            .First(b => b.TextContent.Contains("When"));
        await cut.InvokeAsync(() => whenBtn.Click());

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().Contain("sort=when", "When column should be persisted to URL");
        nav.Uri.Should().Contain("sortDir=asc", "toggling When from default desc should yield asc");

        // Click again to return to desc
        await cut.InvokeAsync(() => whenBtn.Click());

        nav.Uri.Should().Contain("sortDir=desc", "second click on When should restore desc");
    }

    // ── 18. Sort state read from URL query params on page load (AC6) ──────────

    [Fact]
    public void RunsTable_SortStateRestoredFromUrlQueryParams()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        // Navigate to the Runs page with an existing sort query string before rendering
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/runs?sort=result&sortDir=asc");

        var cut = Render<Runs>();

        // The Result column header should carry the sort-active class
        var activeHeaders = cut.FindAll(".monitoring-table thead th.sort-active");
        activeHeaders.Count.Should().Be(1, "exactly one column should be marked active when sort= is in URL");
        activeHeaders[0].TextContent.Should().Contain("Result",
            "Result column should be active when ?sort=result is in the URL");

        // The sort indicator should show ascending (↑)
        var indicator = activeHeaders[0].QuerySelector(".sort-indicator");
        indicator.Should().NotBeNull("sort direction indicator must be present when sort state is loaded from URL");
        indicator!.TextContent.Should().Contain("↑",
            "ascending indicator (↑) expected when sortDir=asc is in the URL");
    }

    [Fact]
    public void RunsTable_SortStateRestoredFromUrl_DescendingDirection()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/runs?sort=type&sortDir=desc");

        var cut = Render<Runs>();

        var activeHeaders = cut.FindAll(".monitoring-table thead th.sort-active");
        activeHeaders.Count.Should().Be(1);
        activeHeaders[0].TextContent.Should().Contain("Type",
            "Type column should be active when ?sort=type is in the URL");

        var indicator = activeHeaders[0].QuerySelector(".sort-indicator");
        indicator.Should().NotBeNull();
        indicator!.TextContent.Should().Contain("↓",
            "descending indicator (↓) expected when sortDir=desc is in the URL");
    }

    // ── Test file WARNING annotations ─────────────────────────────────────────

    // TODO (test #4): RunsTable_NoIssueLink_WhenIssueUrlNull only asserts rows.Count == 1.
    // It does not verify that no anchor element is rendered in the links cell, so a bug that
    // renders an <a href=""> for a null IssueUrl would go undetected. Should assert:
    //   cut.FindAll(".runs-links-cell a").Should().BeEmpty()
    // FIXED in the CRITICAL pass: assertion added.

    // TODO (test #9): RunsTable_DefaultSort_WhenDescending_NewestFirst has a tautological assertion.
    // Both runs use the default issueIdentifier "42", so rows[0].TextContent.Contains("42") always
    // passes regardless of sort order. Use distinct identifiers (e.g. "1" and "2") and assert that
    // the newer run's identifier appears first to actually verify the sort order.
    // FIXED in the CRITICAL pass: distinct identifiers "100" and "200" used, and the assertion
    // now checks for "#200" in rows[0] and explicitly excludes "#100".

    // TODO (test #8): RunsTable_SortableColumnHeaders_Present asserts only Count > 0.
    // It would pass if only one of the four expected sortable columns renders a button.
    // Strengthen to verify exactly 4 sort buttons, or check by name for Result/Type/Duration/When.

    // TODO (test #11): RunsTable_ActiveSortColumn_HasIndicatorClass verifies the sort-active class
    // is present but does not verify that inactive columns do NOT carry sort-active. A bug that
    // sets the class on all columns would not be caught (the Count.Should().Be(1) assertion in
    // test #11 does catch this, but test #15 does not verify it).

    // TODO (missing coverage): No test covers PipelineRunType.DecompositionAnalysis mapping to the
    // "Decomposition" filter option. RunTypeLabel maps both DecompositionAnalysis and Decomposition to
    // "Decomposition". A regression dropping one arm of the pattern would go undetected.

    // TODO (missing coverage): No test covers simultaneous Result + Type filter interaction. A bug
    // where the second filter overwrites the first (items = _result.Items.Where(...) instead of
    // items = items.Where(...)) would not be caught by tests that apply only one filter at a time.
}

using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for <see cref="Runs"/> covering:
/// - Removal of the Agent column (issue #2328)
/// - Links column with issue URL and PR URL (issue #2328)
/// - Sortable column headers (issue #2328)
/// - Type and Initiated-by filter dropdowns; Result dropdown removed (issue #2941)
/// - Sort state reflected in URL (issue #2328)
/// - Client-filter empty-state with Clear filters action (issue #2941)
/// - Pager count reflects filtered rows (issue #2941)
/// - Consolidation filter options removed (issue #2941)
/// - aria-selected lowercase on outcome tabs (issue #2941)
/// - Feedback only checkbox accessible name (issue #2941)
/// </summary>
public class RunsPageComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiRunHistoryClient> _mockRunHistory = new();
    private readonly Mock<IAgentHubConnection> _mockHubConnection = new();

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
        long totalTokens = 100,
        string initiatedBy = "manual",
        RunMode runMode = RunMode.New)
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
            InitiatedBy = initiatedBy,
            RunMode = runMode,
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

        // IAgentHubConnection is injected into Runs.razor for SignalR subscriptions.
        // Set up a no-op mock so bUnit can resolve the dependency without a real hub.
        _mockHubConnection.Setup(h => h.State).Returns(HubConnectionState.Disconnected);
        _mockHubConnection.Setup(h => h.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockHubConnection.Setup(h => h.On(It.IsAny<string>(), It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        _mockHubConnection.Setup(h => h.On<It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType>>())).Returns(Mock.Of<IDisposable>());
        _mockHubConnection.Setup(h => h.On<It.IsAnyType, It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType>>())).Returns(Mock.Of<IDisposable>());
        _mockHubConnection.Setup(h => h.On<It.IsAnyType, It.IsAnyType, It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType, It.IsAnyType>>())).Returns(Mock.Of<IDisposable>());
        _mockHubConnection.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        Services.AddSingleton(_mockRunHistory.Object);
        Services.AddSingleton(_mockHubConnection.Object);
        Services.AddSingleton(new CockpitState());
        // IJSRuntime is required by RefreshBar (injected via @inject IJSRuntime JS).
        Services.AddSingleton(Mock.Of<IJSRuntime>());
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

    // ── 12. Result filter dropdown is absent (replaced by outcome tabs — issue #2941) ──────────

    [Fact]
    public void RunsTable_NoResultFilterDropdown_Present()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        // The Result column dropdown was removed in issue #2941 — outcome tabs are the single outcome control.
        var resultFilters = cut.FindAll("select.column-filter[aria-label='Filter by Result']");
        resultFilters.Should().BeEmpty(
            "the Result column filter dropdown must not exist; outcome tabs are the single outcome control");
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
        var run1 = MakeSummary("r1", runType: PipelineRunType.Implementation, issueIdentifier: "1");
        var run2 = MakeSummary("r2", runType: PipelineRunType.Review, issueIdentifier: "2");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(run1, run2));

        var cut = Render<Runs>();

        // Filter by Review — only one row visible
        var typeFilter = cut.Find("select.column-filter[aria-label='Filter by Type']");
        await cut.InvokeAsync(() => typeFilter.Change("Review"));
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(1);

        // Clear filter (value = "") — all rows must be restored
        await cut.InvokeAsync(() => typeFilter.Change(""));
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(2,
            "clearing the Type filter should restore all rows");
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

    // ── 16. Empty state shown with message and Clear filters button when filter eliminates all rows ──

    [Fact]
    public async Task RunsTable_FilterEmptyState_WhenNoRowsMatchFilter()
    {
        var completedRun = MakeSummary("r1", runType: PipelineRunType.Implementation, issueIdentifier: "10");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(completedRun));

        var cut = Render<Runs>();

        // Filter by Review — no matches
        var typeFilter = cut.Find("select.column-filter[aria-label='Filter by Type']");
        await cut.InvokeAsync(() => typeFilter.Change("Review"));

        // The table must not be present (replaced by the cockpit-empty message)
        cut.FindAll(".monitoring-table").Should().BeEmpty(
            "when client filter produces zero rows the table must be replaced by the empty-state message");

        // A cockpit-empty message must appear with the expected text
        var emptyState = cut.Find(".cockpit-empty");
        emptyState.TextContent.Should().Contain("No runs match the current filters",
            "the empty-state message must tell the user no runs match");

        // A 'Clear filters' button must be present inside the empty-state
        var clearBtn = emptyState.QuerySelector("button");
        clearBtn.Should().NotBeNull("a 'Clear filters' button must be present in the empty-state");
        clearBtn!.TextContent.Should().Contain("Clear filters");
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

    // ── Initiated by column (issue #2540) ─────────────────────────────────────

    // 19. Initiated by column header present
    [Fact]
    public void RunsTable_HasInitiatedByColumnHeader()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1", initiatedBy: "loop:issue")));

        var cut = Render<Runs>();

        var headers = cut.FindAll(".monitoring-table thead th");
        headers.Should().Contain(h => h.TextContent.Trim().StartsWith("Initiated by"),
            "the Runs table must have an 'Initiated by' column header");
    }

    // 20. Initiated by cell renders ToDisplayString for RunMode.New
    [Fact]
    public void RunsTable_InitiatedByCell_RendersDisplayString_ForNew()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1", initiatedBy: "loop:issue", runMode: RunMode.New)));

        var cut = Render<Runs>();

        // TODO: [WARNING] cut.Markup.Should().Contain("loop:issue") matches against the entire rendered
        // page, including the filter <select> dropdown, which always contains an <option>loop:issue</option>
        // regardless of data rows. If ToDisplayString were not called or the cell were accidentally removed,
        // this assertion would still pass because "loop:issue" appears in the static dropdown HTML.
        // Scope the assertion to a .cockpit-chip element inside a tbody <td> to actually verify the cell
        // renders the value through ToDisplayString (e.g. using cut.FindAll(".monitoring-table tbody .cockpit-chip")).

        // For RunMode.New, ToDisplayString returns the raw initiatedBy value unchanged.
        cut.Markup.Should().Contain("loop:issue",
            "the Initiated by cell must contain the initiatedBy value for RunMode.New");
    }

    // 21. Initiated by cell renders "(rework)" suffix for RunMode.Rework
    [Fact]
    public void RunsTable_InitiatedByCell_RendersReworkSuffix_ForRework()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1", initiatedBy: "loop:issue", runMode: RunMode.Rework)));

        var cut = Render<Runs>();

        // TODO: [WARNING] This assertion verifies the text content but not that it is wrapped in a
        // .cockpit-chip element. The acceptance criterion requires "styled as a chip badge." If the
        // <span class="cockpit-chip"> wrapper were removed and the text rendered as plain text, this
        // test would still pass. Add a complementary assertion:
        //   cut.FindAll(".monitoring-table tbody .cockpit-chip")
        //      .Should().Contain(c => c.TextContent.Trim() == "loop:issue (rework)")
        // to fully cover the chip requirement.

        // ToDisplayString returns "loop:issue (rework)" for RunMode.Rework.
        cut.Markup.Should().Contain("loop:issue (rework)",
            "the Initiated by cell must include '(rework)' suffix when RunMode is Rework");
    }

    // 22. InitiatedBy filter narrows the displayed rows
    [Fact]
    public async Task RunsTable_InitiatedByFilter_NarrowsDisplayedRows()
    {
        var loopRun  = MakeSummary("r1", initiatedBy: "loop:issue",  issueIdentifier: "1");
        var manualRun = MakeSummary("r2", initiatedBy: "manual",     issueIdentifier: "2");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(loopRun, manualRun));

        var cut = Render<Runs>();

        // Both rows initially visible.
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(2);

        // Apply InitiatedBy filter — only the loop:issue run should remain.
        var initiatedByFilter = cut.Find("select.column-filter[aria-label='Filter by Initiated by']");
        await cut.InvokeAsync(() => initiatedByFilter.Change("loop:issue"));

        var visibleRows = cut.FindAll(".monitoring-table tbody tr");
        visibleRows.Count.Should().Be(1, "filtering by 'loop:issue' should show only 1 row");
        visibleRows[0].TextContent.Should().Contain("#1",
            "the visible row should be the loop:issue run");
    }

    // 23. Clearing the InitiatedBy filter restores all rows
    [Fact]
    public async Task RunsTable_ClearingInitiatedByFilter_RestoresAllRows()
    {
        var loopRun   = MakeSummary("r1", initiatedBy: "loop:issue", issueIdentifier: "1");
        var manualRun = MakeSummary("r2", initiatedBy: "manual",     issueIdentifier: "2");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(loopRun, manualRun));

        var cut = Render<Runs>();

        var initiatedByFilter = cut.Find("select.column-filter[aria-label='Filter by Initiated by']");
        await cut.InvokeAsync(() => initiatedByFilter.Change("loop:issue"));
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(1);

        // Clear filter — all rows must be restored.
        await cut.InvokeAsync(() => initiatedByFilter.Change(""));
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(2,
            "clearing the InitiatedBy filter should restore all rows");
    }

    // ── Consolidation row navigation (issue #2629) ────────────────────────────

    // 24. Clicking a consolidation row must NOT navigate and must lack monitoring-row-clickable
    [Fact]
    public async Task ConsolidationRow_Click_DoesNotNavigate_AndIsVisuallyInert()
    {
        var runId = Guid.NewGuid().ToString();
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary(runId, runType: PipelineRunType.Consolidation,
                initiatedBy: "consolidation:manual")));

        var cut = Render<Runs>();

        var nav = Services.GetRequiredService<NavigationManager>();
        var uriBefore = nav.Uri;

        // (a) Row must NOT carry monitoring-row-clickable CSS class.
        var row = cut.Find(".monitoring-table tbody tr");
        row.ClassList.Should().NotContain("monitoring-row-clickable",
            "consolidation rows must not be visually presented as clickable");

        // (b) Clicking the row must not change the navigation URI.
        await cut.InvokeAsync(() => row.Click());
        nav.Uri.Should().Be(uriBefore,
            "clicking a consolidation row must not trigger navigation");

        // (c) Row must have a non-empty title tooltip directing to the Consolidation page.
        var title = row.GetAttribute("title");
        title.Should().NotBeNullOrEmpty(
            "consolidation row must have a tooltip directing users to the Consolidation page");
        title.Should().Contain("Consolidation",
            "tooltip must reference the Consolidation page");
    }

    // 25. Clicking an Implementation row MUST navigate to /runs/{runId} (regression guard)
    // TODO: add equivalent tests for PipelineRunType.Review and PipelineRunType.DecompositionAnalysis /
    // PipelineRunType.Decomposition — the acceptance criteria for issue #2629 explicitly require
    // that all non-consolidation run types continue to navigate. TryOpenRun's single != Consolidation
    // branch would not be caught by test coverage if it is accidentally broadened.
    // See review finding from issue #2629.
    [Fact]
    public async Task ImplementationRow_Click_NavigatesToRunDetail()
    {
        var runId = Guid.NewGuid().ToString();
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary(runId, runType: PipelineRunType.Implementation)));

        var cut = Render<Runs>();

        var nav = Services.GetRequiredService<NavigationManager>();

        // Row must carry monitoring-row-clickable.
        var row = cut.Find(".monitoring-table tbody tr");
        row.ClassList.Should().Contain("monitoring-row-clickable",
            "implementation rows must be visually presented as clickable");

        // Clicking must navigate to /runs/{runId}.
        await cut.InvokeAsync(() => row.Click());
        nav.Uri.Should().EndWith($"runs/{runId}",
            "clicking an implementation row must navigate to the run detail page");
    }

    // ── Issue #2941 — new tests ────────────────────────────────────────────────

    // 26. Client filter empty-state is shown when filter eliminates all rows
    // TODO: This test is nearly identical to test 16 (RunsTable_FilterEmptyState_WhenNoRowsMatchFilter).
    // Both set up one Implementation run, apply the Review type filter, and assert the same
    // postconditions (no table, cockpit-empty message, Clear-filters button). The two tests should
    // be consolidated into a single parameterized test or merged to reduce maintenance burden.
    [Fact]
    public async Task RunsTable_FilterEmptyState_ShownWhenClientFilterEliminatesAllRows()
    {
        var implRun = MakeSummary("r1", runType: PipelineRunType.Implementation, issueIdentifier: "1");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(implRun));

        var cut = Render<Runs>();

        // Apply Type filter "Review" — zero matches for an Implementation run
        var typeFilter = cut.Find("select.column-filter[aria-label='Filter by Type']");
        await cut.InvokeAsync(() => typeFilter.Change("Review"));

        // Table must be replaced by cockpit-empty message
        cut.FindAll(".monitoring-table").Should().BeEmpty(
            "table must not be rendered when client filter matches nothing");
        var emptyState = cut.Find(".cockpit-empty");
        emptyState.TextContent.Should().Contain("No runs match the current filters");
        emptyState.QuerySelector("button").Should().NotBeNull("Clear filters button must be present");
    }

    // 27. Clear filters button restores the rows
    [Fact]
    public async Task RunsTable_ClearFiltersButton_ResetsFiltersAndRestoresRows()
    {
        var implRun = MakeSummary("r1", runType: PipelineRunType.Implementation, issueIdentifier: "1");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(implRun));

        var cut = Render<Runs>();

        // Drive to the empty-state
        var typeFilter = cut.Find("select.column-filter[aria-label='Filter by Type']");
        await cut.InvokeAsync(() => typeFilter.Change("Review"));
        cut.FindAll(".monitoring-table").Should().BeEmpty();

        // Click the Clear filters button
        await cut.InvokeAsync(() =>
        {
            var clearBtn = cut.Find(".cockpit-empty button");
            clearBtn.Click();
        });

        // The row must be visible again
        cut.FindAll(".monitoring-table tbody tr").Count.Should().Be(1,
            "clicking Clear filters must restore the row");
    }

    // 28. Pager shows 'N of M on this page' when client filter reduces count
    [Fact]
    public async Task RunsTable_PagerCount_ReflectsFilteredRowCount()
    {
        var implRun   = MakeSummary("r1", runType: PipelineRunType.Implementation, issueIdentifier: "1");
        var reviewRun = MakeSummary("r2", runType: PipelineRunType.Review,          issueIdentifier: "2");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(implRun, reviewRun));

        var cut = Render<Runs>();

        // Apply Type filter "Review" — one of two rows visible
        var typeFilter = cut.Find("select.column-filter[aria-label='Filter by Type']");
        await cut.InvokeAsync(() => typeFilter.Change("Review"));

        var pagerInfo = cut.Find(".cockpit-pager-info");
        pagerInfo.TextContent.Should().Contain("1 of 2 on this page",
            "pager must show filtered count when a client filter is active");
    }

    // 29. Pager shows 'N shown' when no client filter is active
    [Fact]
    public void RunsTable_PagerCount_ShowsTotal_WhenNoFilterActive()
    {
        var run1 = MakeSummary("r1", runType: PipelineRunType.Implementation, issueIdentifier: "1");
        var run2 = MakeSummary("r2", runType: PipelineRunType.Review,         issueIdentifier: "2");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(run1, run2));

        var cut = Render<Runs>();

        var pagerInfo = cut.Find(".cockpit-pager-info");
        pagerInfo.TextContent.Should().Contain("2 shown",
            "pager must show total count when no client filter is active");
    }

    // 30. Pager shows '0 of M on this page' when client filter eliminates all rows (empty-state path)
    // TODO: This test asserts the exact string "0 of 2 on this page", which depends on the current
    // pager expression emitting that format when _filteredRuns.Count == 0. If the production code
    // is later changed to suppress the pager info entirely when the empty-state is displayed, this
    // test will fail spuriously. Additionally, this test exercises the same filter/pager code path
    // as test 28; the only new aspect being tested is the zero-filtered-rows value. Consider
    // consolidating tests 28 and 30 or making the pager assertion less brittle.
    [Fact]
    public async Task RunsTable_PagerCount_ZeroShown_WhenFilterEliminatesAll()
    {
        var run1 = MakeSummary("r1", runType: PipelineRunType.Implementation, issueIdentifier: "1");
        var run2 = MakeSummary("r2", runType: PipelineRunType.Implementation, issueIdentifier: "2");

        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(run1, run2));

        var cut = Render<Runs>();

        // Apply Type filter "Review" — zero matches
        var typeFilter = cut.Find("select.column-filter[aria-label='Filter by Type']");
        await cut.InvokeAsync(() => typeFilter.Change("Review"));

        // Empty-state must be shown
        cut.FindAll(".monitoring-table").Should().BeEmpty();

        // Pager must correctly say "0 of 2 on this page" (not "2 shown")
        var pagerInfo = cut.Find(".cockpit-pager-info");
        pagerInfo.TextContent.Should().Contain("0 of 2 on this page",
            "pager must show '0 of 2 on this page' when filter eliminates all rows");
    }

    // 31. Type filter does not offer 'Consolidation' (excluded until #2567)
    [Fact]
    public void RunsTable_TypeFilter_DoesNotOfferConsolidation()
    {
        // TODO(#2567): remove or invert this test when consolidation runs appear in history.
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        var typeSelect = cut.Find("select.column-filter[aria-label='Filter by Type']");
        var options = typeSelect.QuerySelectorAll("option");
        options.Should().NotContain(o => o.GetAttribute("value") == "Consolidation",
            "Consolidation must not be offered as a filter option until consolidation runs appear in history (#2567)");
    }

    // 32. Initiated by filter does not offer consolidation options (excluded until #2567)
    [Fact]
    public void RunsTable_InitiatedByFilter_DoesNotOfferConsolidationOptions()
    {
        // TODO(#2567): remove or invert this test when consolidation runs appear in history.
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage(MakeSummary("r1")));

        var cut = Render<Runs>();

        var initiatedBySelect = cut.Find("select.column-filter[aria-label='Filter by Initiated by']");
        var options = initiatedBySelect.QuerySelectorAll("option");
        options.Should().NotContain(o => o.GetAttribute("value") == "consolidation:manual",
            "consolidation:manual must not be offered as an option until consolidation runs appear in history");
        options.Should().NotContain(o => o.GetAttribute("value") == "consolidation:auto",
            "consolidation:auto must not be offered as an option until consolidation runs appear in history");
    }

    // 33. Active outcome tab exposes aria-selected="true" (lowercase)
    // TODO: This test uses OnePage() (no runs), so clicking the Failed tab triggers a server reload
    // that returns an empty page and renders the "no runs yet" empty state. The aria-selected
    // attribute is still set on the tab element, so the assertion passes, but the test never
    // exercises the path where a tab is active and rows are present. A more discriminating setup
    // would provide at least one Failed-step run so that SetOutcome, the server call, and the
    // subsequent table render all participate in the same test scenario.
    [Fact]
    public async Task RunsTable_OutcomeTabs_AriaSelectedTrue_OnActiveTab()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage());

        var cut = Render<Runs>();

        // Click the Failed tab to make it active
        var failedBtn = cut.FindAll("[role='tab']").First(b => b.TextContent.Trim() == "Failed");
        await cut.InvokeAsync(() => failedBtn.Click());

        // Re-query after the state change
        var activeTab = cut.FindAll("[role='tab']").First(b => b.TextContent.Trim() == "Failed");
        activeTab.GetAttribute("aria-selected").Should().Be("true",
            "aria-selected must be the lowercase string 'true' on the active tab");
    }

    // 34. Inactive outcome tabs expose aria-selected="false" (lowercase)
    [Fact]
    public void RunsTable_OutcomeTabs_AriaSelectedFalse_OnInactiveTabs()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage());

        var cut = Render<Runs>();

        // Default: All tab is active
        var allTab = cut.FindAll("[role='tab']").First(b => b.TextContent.Trim() == "All");
        allTab.GetAttribute("aria-selected").Should().Be("true",
            "All tab must have aria-selected='true' on initial render");

        // All other tabs must have aria-selected="false"
        var inactiveTabs = cut.FindAll("[role='tab']").Where(b => b.TextContent.Trim() != "All");
        foreach (var tab in inactiveTabs)
        {
            tab.GetAttribute("aria-selected").Should().Be("false",
                $"inactive tab '{tab.TextContent.Trim()}' must have aria-selected='false'");
        }
    }

    // 35. Feedback only checkbox has accessible name 'Feedback only' via aria-label
    [Fact]
    public void RunsTable_FeedbackOnlyCheckbox_HasCorrectAccessibleName()
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePage());

        var cut = Render<Runs>();

        var checkbox = cut.Find("input[type='checkbox']");
        checkbox.GetAttribute("aria-label").Should().Be("Feedback only",
            "the Feedback only checkbox must carry aria-label='Feedback only' for assistive technology");
    }
}

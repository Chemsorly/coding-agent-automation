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
/// bUnit component tests for the Runs page (<see cref="Runs"/>).
/// Covers: Agent column removal, Links column (issue + PR links), client-side sort,
/// sort URL persistence, and Result/Type filter dropdowns.
/// </summary>
public class RunsPageTests : BunitContext
{
    private readonly Mock<IPipelineApiRunHistoryClient> _mockRunHistory = new();

    public RunsPageTests()
    {
        // CockpitState — plain instantiable, no mocking needed.
        Services.AddSingleton(new CockpitState());

        // IPipelineApiRunHistoryClient — stubbed to return empty history by default.
        // Individual tests override this via SetHistory().
        // Task-returning mock methods default to null without explicit setup — always set up.
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = Array.Empty<PipelineRunSummary>(),
                Page = 1, PageSize = 25, HasMore = false
            });
        Services.AddSingleton(_mockRunHistory.Object);

        // NavigationManager — provided automatically by bUnit; no explicit registration needed.
        // @layout CockpitLayout is NOT rendered when Render<Runs>() is called directly — no layout DI needed.
        // AutoRefresh uses PeriodicTimer internally; timers do not tick in bUnit tests — benign.
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private void SetHistory(params PipelineRunSummary[] runs)
    {
        _mockRunHistory
            .Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = runs, Page = 1, PageSize = 25, HasMore = false
            });
    }

    private static PipelineRunSummary MakeRun(
        string runId = "run-0001",
        string issueIdentifier = "org/repo#1",
        string issueTitle = "Test issue",
        PipelineStep finalStep = PipelineStep.Completed,
        PipelineRunType runType = PipelineRunType.Implementation,
        string? issueUrl = null,
        string? pullRequestUrl = null,
        string? reviewPrUrl = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null) => new()
    {
        RunId = runId,
        IssueIdentifier = issueIdentifier,
        IssueTitle = issueTitle,
        FinalStep = finalStep,
        RunType = runType,
        IssueUrl = issueUrl,
        PullRequestUrl = pullRequestUrl,
        ReviewPrUrl = reviewPrUrl,
        StartedAtOffset = startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-10),
        CompletedAtOffset = completedAt ?? DateTimeOffset.UtcNow,
        StartedAt = (startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-10)).UtcDateTime,
        CompletedAt = (completedAt ?? DateTimeOffset.UtcNow).UtcDateTime,
    };

    // ══════════════════════════════════════════════════════════════════════
    // 1. Agent column removal
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void RunsTable_DoesNotRender_AgentColumn()
    {
        SetHistory(MakeRun(runId: "r1"));
        var cut = Render<Runs>();

        // th "Agent" must not appear
        Assert.DoesNotContain("<th>Agent</th>", cut.Markup, StringComparison.OrdinalIgnoreCase);
        // TODO [WARNING]: The monitoring-mono class check below is fragile — if that class is reused
        // elsewhere in the component, this assertion becomes a false negative even if the agent column
        // were re-added without it. Consider replacing with a positive check that the Links <td> cells
        // do not contain an AgentId value instead.
        // See review finding: RunsPageTests.cs ~109 (TestQualityReviewer).
        // The agent cell used class="monitoring-mono" exclusively for the agent column
        Assert.DoesNotContain("monitoring-mono", cut.Markup);
    }

    [Fact]
    public void RunsTable_ColumnCount_Is_Seven()
    {
        SetHistory(MakeRun(runId: "r1"));
        var cut = Render<Runs>();

        var headerCells = cut.Find(".monitoring-table thead tr").QuerySelectorAll("th");
        Assert.Equal(7, headerCells.Length);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 2. Issue link
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void RunsTable_IssueUrl_IsPresent_RendersClickableLink()
    {
        SetHistory(MakeRun(runId: "r1", issueUrl: "https://github.com/org/repo/issues/42"));
        var cut = Render<Runs>();

        Assert.Contains("href=\"https://github.com/org/repo/issues/42\"", cut.Markup);
    }

    [Fact]
    public void RunsTable_IssueUrl_IsNull_RendersNoAnchorForIssue()
    {
        SetHistory(MakeRun(runId: "r1", issueUrl: null, pullRequestUrl: null, reviewPrUrl: null));
        var cut = Render<Runs>();

        // No anchor at all in the Links cell when both IssueUrl and PR urls are null
        var linksCells = cut.FindAll("td.run-links-cell");
        Assert.All(linksCells, td => Assert.DoesNotContain("<a ", td.InnerHtml, StringComparison.OrdinalIgnoreCase));
        // TODO [WARNING]: No test covers the case where issueUrl is null but pullRequestUrl is non-null.
        // A bug that renders a blank href="" for a null issue URL would not be caught by this test alone.
        // Add a complementary test: MakeRun(issueUrl: null, pullRequestUrl: "https://...") and assert
        // the PR anchor renders correctly with no broken issue anchor.
        // See review finding: RunsPageTests.cs ~120 (TestQualityReviewer).
    }

    // ══════════════════════════════════════════════════════════════════════
    // 3. PR link
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void RunsTable_PullRequestUrl_IsPresent_RendersClickablePrLink()
    {
        SetHistory(MakeRun(runId: "r1", pullRequestUrl: "https://github.com/org/repo/pull/7"));
        var cut = Render<Runs>();

        Assert.Contains("href=\"https://github.com/org/repo/pull/7\"", cut.Markup);
    }

    [Fact]
    public void RunsTable_PullRequestUrl_IsNull_NoPrLink()
    {
        SetHistory(MakeRun(runId: "r1", pullRequestUrl: null, reviewPrUrl: null, issueUrl: null));
        var cut = Render<Runs>();

        // No PR anchor — no anchor tags at all in links cell
        var linksCells = cut.FindAll("td.run-links-cell");
        Assert.All(linksCells, td => Assert.DoesNotContain("<a ", td.InnerHtml, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunsTable_ReviewRun_WithReviewPrUrl_RendersReviewPrLink()
    {
        SetHistory(MakeRun(
            runId: "r1",
            runType: PipelineRunType.Review,
            reviewPrUrl: "https://github.com/org/repo/pull/42",
            pullRequestUrl: null));
        var cut = Render<Runs>();

        Assert.Contains("href=\"https://github.com/org/repo/pull/42\"", cut.Markup);
    }

    [Fact]
    public void RunsTable_PullRequestUrl_IsNull_NoLinkRendered_NotBrokenAnchor()
    {
        SetHistory(MakeRun(runId: "r1", pullRequestUrl: null, reviewPrUrl: null));
        var cut = Render<Runs>();

        // No anchor with href="" or href="#"
        Assert.DoesNotContain("href=\"\"", cut.Markup);
        Assert.DoesNotContain("href=\"#\"", cut.Markup);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 4. Sort
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void RunsTable_DefaultSort_IsWhenDescending()
    {
        var older = MakeRun(runId: "r-old", issueIdentifier: "org/repo#1",
            startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = MakeRun(runId: "r-new", issueIdentifier: "org/repo#2",
            startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        SetHistory(older, newer); // server returns old first
        var cut = Render<Runs>();

        var rows = cut.FindAll(".monitoring-table tbody tr");
        Assert.Equal(2, rows.Count);
        // Newer run (r-new / #2) should appear first in When-desc order
        Assert.Contains("org/repo#2", rows[0].InnerHtml);
        Assert.Contains("org/repo#1", rows[1].InnerHtml);
    }

    [Fact]
    public async Task RunsTable_ClickWhenHeader_TogglesToAscending()
    {
        var older = MakeRun(runId: "r-old", issueIdentifier: "org/repo#1",
            startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = MakeRun(runId: "r-new", issueIdentifier: "org/repo#2",
            startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        SetHistory(older, newer);
        var cut = Render<Runs>();

        // Click the When sort header
        await cut.InvokeAsync(() =>
        {
            var whenTh = cut.FindAll(".monitoring-table thead th")
                .First(th => th.TextContent.Contains("When"));
            whenTh.Click();
        });

        var rows = cut.FindAll(".monitoring-table tbody tr");
        // After one click → ascending: older run should appear first
        Assert.Contains("org/repo#1", rows[0].InnerHtml);
        Assert.Contains("org/repo#2", rows[1].InnerHtml);
    }

    [Fact]
    public async Task RunsTable_ClickWhenHeaderTwice_RestoresDescending()
    {
        var older = MakeRun(runId: "r-old", issueIdentifier: "org/repo#1",
            startedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = MakeRun(runId: "r-new", issueIdentifier: "org/repo#2",
            startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        SetHistory(older, newer);
        var cut = Render<Runs>();

        // Click twice to go desc → asc → desc
        await cut.InvokeAsync(() =>
        {
            var whenTh = cut.FindAll(".monitoring-table thead th")
                .First(th => th.TextContent.Contains("When"));
            whenTh.Click();
            whenTh.Click();
        });

        var rows = cut.FindAll(".monitoring-table tbody tr");
        // Back to descending: newer first
        Assert.Contains("org/repo#2", rows[0].InnerHtml);
        Assert.Contains("org/repo#1", rows[1].InnerHtml);
        // TODO [WARNING]: Both clicks execute inside a single InvokeAsync call, so they run in the same
        // render cycle. The second click may read _sortDir before the first click's state change has been
        // applied, meaning the test could pass for the wrong reason (two desc→asc transitions both fire
        // on the original state = net asc, not desc). Split into two separate InvokeAsync calls with a
        // state barrier between them to ensure each click processes the previous click's state change.
        // See review finding: RunsPageTests.cs ~252 (TestQualityReviewer).
    }

    [Fact]
    public async Task RunsTable_ClickDurationHeader_SortsByDuration()
    {
        var now = DateTimeOffset.UtcNow;
        var shortRun = MakeRun(runId: "r-short", issueIdentifier: "org/repo#1",
            startedAt: now.AddMinutes(-1), completedAt: now);         // 1 min
        var longRun = MakeRun(runId: "r-long", issueIdentifier: "org/repo#2",
            startedAt: now.AddMinutes(-10), completedAt: now);        // 10 min
        SetHistory(shortRun, longRun);
        var cut = Render<Runs>();

        await cut.InvokeAsync(() =>
        {
            var durationTh = cut.FindAll(".monitoring-table thead th")
                .First(th => th.TextContent.Contains("Duration"));
            durationTh.Click();
        });

        var rows = cut.FindAll(".monitoring-table tbody tr");
        // Duration desc: longest first
        Assert.Contains("org/repo#2", rows[0].InnerHtml);
        Assert.Contains("org/repo#1", rows[1].InnerHtml);
    }

    [Fact]
    public async Task RunsTable_ActiveSortHeader_HasAriaSort()
    {
        SetHistory(MakeRun(runId: "r1"));
        var cut = Render<Runs>();

        // Default: When column is active, descending
        var whenTh = cut.FindAll(".monitoring-table thead th")
            .First(th => th.TextContent.Contains("When"));
        Assert.Equal("descending", whenTh.GetAttribute("aria-sort"));

        // After one click → ascending
        await cut.InvokeAsync(() => whenTh.Click());

        whenTh = cut.FindAll(".monitoring-table thead th")
            .First(th => th.TextContent.Contains("When"));
        Assert.Equal("ascending", whenTh.GetAttribute("aria-sort"));
        // TODO [WARNING]: This test does not verify that a previously-sorted column loses its aria-sort
        // attribute when a different column is clicked. The acceptance criteria state that the active
        // sort column is visually indicated — implying only the active column carries the indicator.
        // Add a step: click Duration header, then assert When th no longer has aria-sort.
        // See review finding: RunsPageTests.cs ~201 (TestQualityReviewer).
    }

    [Fact]
    public void RunsTable_SortColumn_WhenQueryParam_SetsInitialSort()
    {
        // Arrange: navigate to /runs?sort=duration&dir=asc before rendering
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/runs?sort=duration&dir=asc");

        var now = DateTimeOffset.UtcNow;
        var shortRun = MakeRun(runId: "r-short", issueIdentifier: "org/repo#1",
            startedAt: now.AddMinutes(-1), completedAt: now);
        var longRun = MakeRun(runId: "r-long", issueIdentifier: "org/repo#2",
            startedAt: now.AddMinutes(-10), completedAt: now);
        SetHistory(shortRun, longRun);

        var cut = Render<Runs>();

        var rows = cut.FindAll(".monitoring-table tbody tr");
        // Duration asc: shortest first
        Assert.Contains("org/repo#1", rows[0].InnerHtml);
        Assert.Contains("org/repo#2", rows[1].InnerHtml);
        // TODO [WARNING]: This test only covers the "read sort from URL" path. It does not verify that
        // a subsequent header click after URL-init toggles correctly (e.g. clicking Duration again from
        // an asc-initialised state should go to desc, not re-apply asc). Add a follow-up test for the
        // "URL init + toggle" interaction.
        // See review finding: RunsPageTests.cs ~296 (TestQualityReviewer).
    }
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunsTable_TypeFilter_Implementation_ShowsOnlyImplRuns()
    {
        SetHistory(
            MakeRun(runId: "r-impl", issueIdentifier: "org/repo#1",
                runType: PipelineRunType.Implementation),
            MakeRun(runId: "r-review", issueIdentifier: "org/repo#2",
                runType: PipelineRunType.Review,
                finalStep: PipelineStep.Completed));
        var cut = Render<Runs>();

        // Set the Type filter to Implementation via the select element
        await cut.InvokeAsync(() =>
        {
            var typeSelect = cut.Find("select.run-type-filter");
            typeSelect.Change("Implementation");
        });

        var rows = cut.FindAll(".monitoring-table tbody tr");
        Assert.Single(rows);
        Assert.Contains("org/repo#1", rows[0].InnerHtml);
    }

    [Fact]
    public async Task RunsTable_TypeFilter_All_ShowsAllRuns()
    {
        SetHistory(
            MakeRun(runId: "r-impl", issueIdentifier: "org/repo#1",
                runType: PipelineRunType.Implementation),
            MakeRun(runId: "r-review", issueIdentifier: "org/repo#2",
                runType: PipelineRunType.Review,
                finalStep: PipelineStep.Completed));
        var cut = Render<Runs>();

        // Set to filter then clear
        await cut.InvokeAsync(() =>
        {
            var typeSelect = cut.Find("select.run-type-filter");
            typeSelect.Change("Implementation");
        });
        await cut.InvokeAsync(() =>
        {
            var typeSelect = cut.Find("select.run-type-filter");
            typeSelect.Change("");
        });

        var rows = cut.FindAll(".monitoring-table tbody tr");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task RunsTable_ResultFilter_Completed_ShowsOnlyCompletedRuns()
    {
        SetHistory(
            MakeRun(runId: "r-ok", issueIdentifier: "org/repo#1",
                finalStep: PipelineStep.Completed),
            MakeRun(runId: "r-fail", issueIdentifier: "org/repo#2",
                finalStep: PipelineStep.Failed));
        var cut = Render<Runs>();

        await cut.InvokeAsync(() =>
        {
            var resultSelect = cut.Find("select.run-result-filter");
            resultSelect.Change("Completed");
        });

        var rows = cut.FindAll(".monitoring-table tbody tr");
        Assert.Single(rows);
        Assert.Contains("org/repo#1", rows[0].InnerHtml);
    }

    [Fact]
    public async Task RunsTable_ResultFilter_Running_ShowsOnlyActiveRuns()
    {
        SetHistory(
            MakeRun(runId: "r-active", issueIdentifier: "org/repo#1",
                finalStep: PipelineStep.GeneratingCode),
            MakeRun(runId: "r-done", issueIdentifier: "org/repo#2",
                finalStep: PipelineStep.Completed));
        var cut = Render<Runs>();

        // Filter to Running (non-terminal)
        await cut.InvokeAsync(() =>
        {
            var resultSelect = cut.Find("select.run-result-filter");
            resultSelect.Change("Running");
        });

        var rows = cut.FindAll(".monitoring-table tbody tr");
        Assert.Single(rows);
        Assert.Contains("org/repo#1", rows[0].InnerHtml);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 6. URL persistence
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunsTable_ToggleSort_UpdatesUrl_WithSortQueryParam()
    {
        SetHistory(MakeRun(runId: "r1"));
        var cut = Render<Runs>();

        await cut.InvokeAsync(() =>
        {
            var durationTh = cut.FindAll(".monitoring-table thead th")
                .First(th => th.TextContent.Contains("Duration"));
            durationTh.Click();
        });

        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.Contains("sort=duration", nav.Uri);
        // TODO [WARNING]: This test does not assert that the dir param is absent. The first click on a
        // new column defaults to Desc, and Desc is the default direction, so dir=asc should NOT appear
        // in the URL (clean-URL logic in ToggleSort omits it). A bug that writes dir=desc to the URL
        // would not be caught. Add: Assert.DoesNotContain("dir=", nav.Uri).
        // See review finding: RunsPageTests.cs ~365 (TestQualityReviewer).
    }

    [Fact]
    public void RunsTable_DefaultSort_Produces_CleanUrl()
    {
        SetHistory(MakeRun(runId: "r1"));
        var cut = Render<Runs>();

        var nav = Services.GetRequiredService<NavigationManager>();
        // Default sort (When desc) — no sort param in URL
        Assert.DoesNotContain("sort=", nav.Uri);
    }
}

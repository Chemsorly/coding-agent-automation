using Bunit;
using Bunit.TestDoubles;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;
using CodingAgent.Web.UnitTests.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for the Attention page's MCP servers section.
/// Verifies that rows are rendered per problem, clicking navigates to runs/{runId},
/// and the card is absent when there are no MCP problems.
/// </summary>
public class AttentionMcpSectionComponentTests : BunitContext
{
    private readonly Mock<IUiRunClient> _mockRunHistory = new();

    public AttentionMcpSectionComponentTests()
    {
        Services.AddTestAccess();

        // Required by RefreshBar.
        Services.AddSingleton(Mock.Of<IJSRuntime>());

        // Triage client — no triages.
        Services.AddSingleton(Mock.Of<IUiTriageClient>(c =>
            c.ListAsync(It.IsAny<TriageListQuery>(), It.IsAny<CancellationToken>()) ==
            Task.FromResult(new TriageListPage())));

        // Browse client — no blocked issues.
        Services.AddSingleton(Mock.Of<IUiBrowseClient>(b =>
            b.GetBacklogAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()) ==
            Task.FromResult(new BacklogResult(Array.Empty<BacklogIssue>(), false))));

        Services.AddSingleton<BlockedIssuesService>();
        Services.AddSingleton<CockpitState>();

        Services.AddSingleton(_mockRunHistory.Object);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static PagedResult<PipelineRunSummary> HistoryWith(params PipelineRunSummary[] items) =>
        new() { Items = items, Page = 1, PageSize = 100, HasMore = false };

    private static PipelineRunSummary RunWithUnavailable(
        string issueId,
        DateTimeOffset startedAt,
        string server,
        string status = McpServerStatuses.NeedsAuth) =>
        new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = issueId,
            IssueTitle = $"Issue {issueId}",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = startedAt,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["codegen"] = new PhaseUsage(0, null)
                {
                    UnavailableMcpServers = new Dictionary<string, string> { [server] = status },
                }
            },
        };

    private static readonly DateTimeOffset T1 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When no run has MCP problems, the MCP servers card must not be rendered.
    /// </summary>
    [Fact]
    public void McpSection_NoProblem_CardAbsent()
    {
        _mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryWith(
                new PipelineRunSummary
                {
                    RunId = Guid.NewGuid().ToString(),
                    IssueIdentifier = "owner/repo#1",
                    IssueTitle = "Some issue",
                    RunType = PipelineRunType.Implementation,
                    FinalStep = PipelineStep.Completed,
                    StartedAtOffset = T1,
                }));

        var cut = Render<Attention>();

        Assert.Throws<Bunit.ElementNotFoundException>(
            () => cut.Find("[data-testid='attention-section-mcp']"));
    }

    /// <summary>
    /// When a run has one MCP server unavailable, the card is rendered with one row.
    /// </summary>
    [Fact]
    public void McpSection_OneProblem_OneRowRendered()
    {
        var run = RunWithUnavailable("owner/repo#1", T1, "sonarqube");
        _mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryWith(run));

        var cut = Render<Attention>();

        var card = cut.Find("[data-testid='attention-section-mcp']");
        Assert.NotNull(card);
        var row = cut.Find("[data-testid='attention-mcp-row-sonarqube']");
        Assert.NotNull(row);
        // Verify the subline wires up the reason, run count, and issue identifier.
        Assert.Contains("unavailable (needs-auth)", row.InnerHtml);
        Assert.Contains("1 run(s) in a row", row.InnerHtml);
        Assert.Contains("owner/repo#1", row.InnerHtml);
    }

    /// <summary>
    /// When two servers have problems, two rows are rendered.
    /// </summary>
    [Fact]
    public void McpSection_TwoProblems_TwoRowsRendered()
    {
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Issue 1",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = T1,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["codegen"] = new PhaseUsage(0, null)
                {
                    UnavailableMcpServers = new Dictionary<string, string>
                    {
                        ["sonarqube"] = McpServerStatuses.NeedsAuth,
                        ["grafana"] = McpServerStatuses.Failed,
                    },
                }
            },
        };
        _mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryWith(run));

        var cut = Render<Attention>();

        var rows = cut.FindAll("[data-testid^='attention-mcp-row-']");
        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// Clicking an MCP problem row navigates to runs/{runId}.
    /// </summary>
    [Fact]
    public void McpSection_ClickRow_NavigatesToRun()
    {
        var run = RunWithUnavailable("owner/repo#1", T1, "sonarqube");
        _mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryWith(run));

        var cut = Render<Attention>();

        var row = cut.Find("[data-testid='attention-mcp-row-sonarqube']");
        row.Click();

        // BunitNavigationManager.Uri is absolute; just check the suffix.
        var nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith($"runs/{run.RunId}", nav.Uri);
    }
}

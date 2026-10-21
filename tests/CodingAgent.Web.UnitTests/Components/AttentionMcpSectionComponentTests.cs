using AwesomeAssertions;
using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;
using CodingAgent.Web.UnitTests.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for the MCP servers section of the Attention page.
/// Verifies rendering, per-row navigation, and absence when no problems exist.
/// </summary>
public class AttentionMcpSectionComponentTests : BunitContext
{
    private readonly Mock<IUiRunClient> _runClient = new();
    private readonly Mock<IUiTriageClient> _triageClient = new();
    private readonly Mock<IUiBrowseClient> _browseClient = new();

    public AttentionMcpSectionComponentTests()
    {
        // Auth: [Authorize(Policy = AccessPolicies.AnyAccess)] requires CurrentAccess, IAccessGuard, etc.
        Services.AddTestAccess();

        // RefreshBar injects IJSRuntime.
        Services.AddSingleton(Mock.Of<IJSRuntime>());

        Services.AddSingleton(_runClient.Object);
        Services.AddSingleton(_triageClient.Object);
        Services.AddSingleton(_browseClient.Object);
        Services.AddSingleton(new BlockedIssuesService(_browseClient.Object, NullLogger<BlockedIssuesService>.Instance));
        Services.AddSingleton(new CockpitState());

        // Default: empty triages so they don't interfere with MCP section assertions.
        _triageClient
            .Setup(c => c.ListAsync(It.IsAny<TriageListQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TriageListPage());

        // Default: empty backlog (blocked issues).
        _browseClient
            .Setup(c => c.GetBacklogAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BacklogResult([], false));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static PagedResult<PipelineRunSummary> RunsPage(params PipelineRunSummary[] runs) => new()
    {
        Items = runs.ToList(),
        Page = 1,
        PageSize = 100,
        HasMore = false,
    };

    private static PipelineRunSummary UnavailableRun(string runId, string server, string status) => new()
    {
        RunId = runId,
        IssueIdentifier = "owner/repo#1",
        IssueTitle = "Test issue",
        FinalStep = PipelineStep.Completed,
        StartedAtOffset = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
        PhaseBreakdown = new Dictionary<string, PhaseUsage>
        {
            ["codegen"] = new PhaseUsage(0, null)
            {
                UnavailableMcpServers = new Dictionary<string, string> { [server] = status }
            }
        }
    };

    // ── Tests ──────────────────────────────────────────────────────────────

    /// <summary>
    /// When there are MCP problems, the section card renders with one row per problem server.
    /// </summary>
    [Fact]
    public void McpSection_WithProblems_RendersRowPerProblem()
    {
        _runClient
            .Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage(
                UnavailableRun(Guid.NewGuid().ToString(), "sonarqube", "needs-auth"),
                UnavailableRun(Guid.NewGuid().ToString(), "grafana", "failed")));

        var cut = Render<Attention>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='attention-section-mcp']");
            cut.FindAll("[data-testid^='attention-mcp-row-']").Count.Should().Be(2);
            cut.Find("[data-testid='attention-mcp-row-sonarqube']");
            cut.Find("[data-testid='attention-mcp-row-grafana']");
        });
    }

    /// <summary>
    /// Clicking an MCP problem row navigates to runs/{runId}.
    /// </summary>
    [Fact]
    public void McpSection_ClickRow_NavigatesToRun()
    {
        var runId = Guid.NewGuid().ToString();
        _runClient
            .Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage(UnavailableRun(runId, "sonarqube", "needs-auth")));

        var cut = Render<Attention>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='attention-mcp-row-sonarqube']"));
        cut.Find("[data-testid='attention-mcp-row-sonarqube']").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"runs/{runId}");
    }

    /// <summary>
    /// When there are no MCP problems the card element is absent.
    /// </summary>
    [Fact]
    public void McpSection_WithoutProblems_CardAbsent()
    {
        _runClient
            .Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage());

        var cut = Render<Attention>();

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='attention-section-mcp']").Should().BeEmpty());
    }
}

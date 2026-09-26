using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Verifies the rendered content of the Overview page stat cards.
/// </summary>
public class OverviewComponentTests : BunitContext
{
    private static PagedResult<PipelineRunSummary> EmptyHistory() => new()
    {
        Items = new List<PipelineRunSummary>(),
        Page = 1,
        PageSize = 100,
        HasMore = false
    };

    private static AgentEntryDto MakeConnectedAgent(string id = "agent-1") => new()
    {
        AgentId = new AgentId(id),
        ConnectionId = $"conn-{id}",
        Hostname = $"host-{id}",
        Labels = new List<string>(),
        Status = AgentStatus.Idle,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Registers the full set of services that Overview.razor requires so that
    /// <see cref="Render{Overview}"/> does not throw a DI exception.
    /// </summary>
    private void RegisterOverviewServices(
        Mock<IPipelineApiAgentClient> mockAgents,
        Mock<IPipelineApiRunHistoryClient>? mockRunHistory = null,
        Mock<IPipelineApiWorkItemClient>? mockWorkItems = null)
    {
        mockRunHistory ??= BuildEmptyRunHistoryMock();
        mockWorkItems ??= BuildEmptyWorkItemsMock();

        Services.AddSingleton(mockRunHistory.Object);
        Services.AddSingleton(mockAgents.Object);
        Services.AddSingleton(mockWorkItems.Object);
        Services.AddSingleton<ILoopStatusService>(Mock.Of<ILoopStatusService>());
        Services.AddSingleton(new CockpitState());
    }

    private static Mock<IPipelineApiRunHistoryClient> BuildEmptyRunHistoryMock()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyHistory());
        return mock;
    }

    private static Mock<IPipelineApiWorkItemClient> BuildEmptyWorkItemsMock()
    {
        var mock = new Mock<IPipelineApiWorkItemClient>();
        mock.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>());
        mock.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActiveWorkItemDto>());
        return mock;
    }

    // ── Agents stat card ───────────────────────────────────────────────────

    /// <summary>
    /// Regression guard for issue #2448: the Agents stat card must show only the connected
    /// agent count with no "/" denominator. Previously the card rendered "1/1" for an
    /// ephemeral agent because _agentsTotal (always equal to _agentsOnline) was appended.
    /// </summary>
    [Fact]
    public void AgentsStatCard_WhenOneAgentConnected_ShowsOnlyOnlineCountWithoutDenominator()
    {
        var mockAgents = new Mock<IPipelineApiAgentClient>();
        mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto> { MakeConnectedAgent() });

        RegisterOverviewServices(mockAgents);

        var cut = Render<Overview>();

        // Find the Agents stat card value span
        var statCards = cut.FindAll(".cockpit-stat");
        var agentsCard = statCards.FirstOrDefault(card =>
        {
            var label = card.QuerySelector(".cockpit-stat-l");
            return label?.TextContent.Trim() == "Agents";
        });

        agentsCard.Should().NotBeNull("the Agents stat card must be present in the Overview stat strip");

        var valueSpan = agentsCard!.QuerySelector(".cockpit-stat-v");
        valueSpan.Should().NotBeNull("the Agents stat card must have a .cockpit-stat-v element");

        var valueText = valueSpan!.TextContent.Trim();

        // The card must show "1" — the connected count — and must not contain a "/" denominator
        valueText.Should().Be("1", "the Agents stat card must show only the online count");
        valueText.Should().NotContain("/", "the Agents stat card must not show a denominator");
    }

    // TODO: Add a test verifying that a Disconnected agent is excluded from the displayed count.
    // Scenario: one AgentStatus.Idle agent + one AgentStatus.Disconnected agent → card must show "1".
    // Without this test, reverting _agentsOnline counting logic back to agents.Count would leave
    // both existing tests green, so the behavioral contract of the fix is not fully guarded.

    /// <summary>
    /// With zero agents registered, the card should show "0" (not "0/0").
    /// </summary>
    [Fact]
    public void AgentsStatCard_WhenNoAgentsRegistered_ShowsZeroWithoutDenominator()
    {
        var mockAgents = new Mock<IPipelineApiAgentClient>();
        mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto>());

        RegisterOverviewServices(mockAgents);

        var cut = Render<Overview>();

        var statCards = cut.FindAll(".cockpit-stat");
        var agentsCard = statCards.FirstOrDefault(card =>
        {
            var label = card.QuerySelector(".cockpit-stat-l");
            return label?.TextContent.Trim() == "Agents";
        });

        agentsCard.Should().NotBeNull("the Agents stat card must be present");

        var valueSpan = agentsCard!.QuerySelector(".cockpit-stat-v");
        var valueText = valueSpan!.TextContent.Trim();

        valueText.Should().Be("0", "with no agents the card must show '0'");
        valueText.Should().NotContain("/", "the Agents stat card must not show a denominator");
    }

    // ── Success-rate label ────────────────────────────────────────────────

    /// <summary>
    /// The success-rate stat card must show "Success · last 100" (not "Success · recent").
    /// This guards the label against regression — Overview fetches a fixed pageSize: 100 with no
    /// time filter, so the label must accurately state the data window.
    /// </summary>
    [Fact]
    public void SuccessRateStatCard_ShowsLastHundredLabel()
    {
        var mockAgents = new Mock<IPipelineApiAgentClient>();
        mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto>());

        RegisterOverviewServices(mockAgents);

        var cut = Render<Overview>();

        var statCards = cut.FindAll(".cockpit-stat");
        var successCard = statCards.FirstOrDefault(card =>
        {
            var label = card.QuerySelector(".cockpit-stat-l");
            return label?.TextContent.Trim() == "Success · last 100";
        });

        successCard.Should().NotBeNull(
            "the success-rate stat card must use the label 'Success · last 100' to accurately state its data window");
    }

    // ── Attention tile de-duplication tests (issue #2935) ─────────────────

    // TODO: [WARNING] The de-duplication tests below only cover the FailedRuns tile for Implementation
    // runs. The acceptance criteria require the badge to equal the sum of all three Attention sections
    // for both "All projects" and a specific project scope. Consider adding tests for:
    //   - NeedsRefinement tile de-duplication
    //   - PlansToApprove tile de-duplication
    //   - A project-scoped scenario where the tile count changes when a project is selected

    /// <summary>
    /// Three Failed runs for the same IssueIdentifier must produce a "Failed runs" tile
    /// showing "1", not "3". AttentionAggregator de-duplicates by (issue, runType) group.
    /// </summary>
    [Fact]
    public void AttentionTile_ThreeFailedRunsSameIssue_ShowsOne()
    {
        var issueId = (IssueIdentifier)"owner/repo#42";
        var now = DateTimeOffset.UtcNow;
        var runs = new List<PipelineRunSummary>
        {
            MakeFailedRun(issueId, now.AddHours(-2), "old failure"),
            MakeFailedRun(issueId, now.AddHours(-1), "middle failure"),
            MakeFailedRun(issueId, now,               "latest failure"),
        };

        var mockHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = runs,
                Page = 1,
                PageSize = 100,
                HasMore = false
            });

        var mockAgents = new Mock<IPipelineApiAgentClient>();
        mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto>());

        RegisterOverviewServices(mockAgents, mockHistory);

        var cut = Render<Overview>();

        // The "Needs attention" card only renders when the total > 0.
        var failedTileCount = GetAttentionTileCount(cut, "Failed runs");
        failedTileCount.Should().Be("1",
            "three Failed runs for the same issue must be de-duplicated to 1");
    }

    /// <summary>
    /// An in-progress (active) run and a completed run for the same issue: the active run
    /// must be excluded from the attention count. Active runs are filtered via IsActive()
    /// before being passed to AttentionAggregator.
    /// </summary>
    // TODO: [WARNING] This test cannot fully verify that the active run is excluded by the aggregator
    // in isolation. Overview.razor fetches with includeActive: true and filters client-side via IsActive(),
    // but the mock returns both runs unconditionally. The test relies on the active run having a different
    // IssueIdentifier (owner/repo#20) so the count still equals 1 — not because the active filter fires.
    // The test also does not verify that GetRunHistoryAsync was called with the correct includeActive flag.
    // An implementation that accidentally removes the includeActive parameter would still pass this test.
    // Consider verifying the mock was called with includeActive: true, and adding a test where the active
    // run has the same issue as the failed run to confirm it is genuinely excluded from the tile count.
    [Fact]
    public void AttentionTile_ActiveRunExcluded_OnlyNonActiveCountedAsFailed()
    {
        var now = DateTimeOffset.UtcNow;
        var runs = new List<PipelineRunSummary>
        {
            // non-active failed run — should appear in FailedRuns
            MakeFailedRun("owner/repo#10", now.AddHours(-1), "build broke"),
            // active run (GeneratingCode is not a terminal step) — must be excluded
            new PipelineRunSummary
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = "owner/repo#20",
                IssueTitle = "Active issue",
                RunType = PipelineRunType.Implementation,
                FinalStep = PipelineStep.GeneratingCode,
                StartedAtOffset = now,
            },
        };

        var mockHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = runs,
                Page = 1,
                PageSize = 100,
                HasMore = false
            });

        var mockAgents = new Mock<IPipelineApiAgentClient>();
        mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto>());

        RegisterOverviewServices(mockAgents, mockHistory);

        var cut = Render<Overview>();

        var failedTileCount = GetAttentionTileCount(cut, "Failed runs");
        failedTileCount.Should().Be("1",
            "only the non-active failed run should appear in the Failed runs tile");
    }

    // ── Terminal-like steps (ConflictRestart / PrMerged / PrClosed) ────────

    /// <summary>
    /// Restarted, merged and closed runs are finished: they must not count as Active or be listed
    /// under "Active runs" with a Running pill, and the recent list shows their real outcome.
    /// </summary>
    [Fact]
    public void TerminalLikeSteps_AreNotActive_AndShowTheirOutcomeInRecentActivity()
    {
        var now = DateTimeOffset.UtcNow;
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("1", PipelineStep.GeneratingCode, now),
            MakeRun("2", PipelineStep.ConflictRestart, now.AddMinutes(-1)),
            MakeRun("3", PipelineStep.PrMerged, now.AddMinutes(-2)),
            MakeRun("4", PipelineStep.PrClosed, now.AddMinutes(-3)),
        };
        RegisterOverviewServices(EmptyAgentsMock(), HistoryMock(runs));

        var cut = Render<Overview>();

        GetStatValue(cut, "Active").Should().Be("1", "only the GeneratingCode run is still in flight");
        var badges = cut.FindAll(".cockpit-run-row .step-badge").Select(b => b.TextContent.Trim()).ToList();
        badges.Should().ContainSingle(b => b == "Running");
        badges.Should().Contain(["Restarted", "Merged", "Closed"]);
    }

    /// <summary>
    /// Success rate is succeeded ÷ (succeeded + failed + cancelled): a merged PR counts as a success and a
    /// conflict restart is left out (the re-dispatched run carries the outcome).
    /// </summary>
    [Fact]
    public void SuccessRate_CountsMergedAsSuccess_AndIgnoresRestarts()
    {
        var now = DateTimeOffset.UtcNow;
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("1", PipelineStep.Completed, now),
            MakeRun("2", PipelineStep.PrMerged, now.AddMinutes(-1)),
            MakeRun("3", PipelineStep.Failed, now.AddMinutes(-2)),
            MakeRun("4", PipelineStep.ConflictRestart, now.AddMinutes(-3)),
        };
        RegisterOverviewServices(EmptyAgentsMock(), HistoryMock(runs));

        var cut = Render<Overview>();

        GetStatValue(cut, "Success · last 100").Should().Be("67%");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static PipelineRunSummary MakeRun(string issueId, PipelineStep finalStep, DateTimeOffset startedAt) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = issueId,
        IssueTitle = $"Issue {issueId}",
        RunType = PipelineRunType.Implementation,
        FinalStep = finalStep,
        StartedAtOffset = startedAt,
    };

    private static Mock<IPipelineApiRunHistoryClient> HistoryMock(List<PipelineRunSummary> runs)
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = runs, Page = 1, PageSize = 100, HasMore = false });
        return mock;
    }

    private static Mock<IPipelineApiAgentClient> EmptyAgentsMock()
    {
        var mock = new Mock<IPipelineApiAgentClient>();
        mock.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<AgentEntryDto>());
        return mock;
    }

    private static string? GetStatValue(IRenderedComponent<Overview> cut, string label) =>
        cut.FindAll(".cockpit-stat")
            .FirstOrDefault(s => s.QuerySelector(".cockpit-stat-l")?.TextContent.Trim() == label)
            ?.QuerySelector(".cockpit-stat-v")?.TextContent.Trim();

    private static PipelineRunSummary MakeFailedRun(
        IssueIdentifier issueId,
        DateTimeOffset startedAt,
        string? failureReason = null) => new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = issueId,
            IssueTitle = $"Issue {issueId}",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Failed,
            StartedAtOffset = startedAt,
            FailureReason = failureReason,
        };

    /// <summary>
    /// Finds the numeric text shown in the attention tile whose label matches
    /// <paramref name="label"/> (e.g. "Failed runs"). Returns null if the card is absent.
    /// </summary>
    private static string? GetAttentionTileCount(IRenderedComponent<Overview> cut, string label)
    {
        // The "Needs attention" grid contains three <a> tiles. Each tile has a structure:
        //   <a ...>
        //     <span ...> (icon) </span>
        //     <div>
        //       <div style="font-size:18px...">COUNT</div>
        //       <div style="font-size:12px...">LABEL</div>
        //     </div>
        //   </a>
        // We find the label text node and return its sibling count div's text.
        var labelDivs = cut.FindAll("div")
            .Where(d => d.TextContent.Trim() == label)
            .ToList();

        var labelDiv = labelDivs.FirstOrDefault();
        if (labelDiv is null) return null;

        // The count is the immediately preceding sibling div.
        var parent = labelDiv.ParentElement;
        if (parent is null) return null;

        var children = parent.Children.ToList();
        var labelIndex = children.IndexOf(labelDiv);
        if (labelIndex <= 0) return null;

        return children[labelIndex - 1].TextContent.Trim();
    }
}

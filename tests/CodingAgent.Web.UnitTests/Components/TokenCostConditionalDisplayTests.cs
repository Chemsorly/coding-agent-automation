using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests for token/cost conditional display in Insights, Overview, and Runs pages.
/// Verifies acceptance criterion: "With only Kiro runs in scope, no token/cost tile,
/// column, field or card is shown; with OpenCode runs they appear."
/// </summary>
public class TokenCostConditionalDisplayTests : BunitContext
{
    // ═══ Helpers ═══

    private static PagedResult<PipelineRunSummary> PagedHistory(params PipelineRunSummary[] items) => new()
    {
        Items = items.ToList(),
        Page = 1,
        PageSize = 100,
        HasMore = false
    };

    private static PipelineRunSummary MakeRun(string id, long tokens = 0, decimal? cost = null) => new()
    {
        RunId = id,
        IssueIdentifier = "1",
        IssueTitle = "Test",
        FinalStep = PipelineStep.Completed,
        StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-10),
        CompletedAtOffset = DateTimeOffset.UtcNow,
        TotalTokens = tokens,
        TotalCost = cost,
        InitiatedBy = "manual",
    };

    // ─── Insights ────────────────────────────────────────────────────────────

    private void RegisterInsightsServices(Mock<IPipelineApiRunHistoryClient> mockHistory)
    {
        Services.AddSingleton(mockHistory.Object);
        Services.AddSingleton(new CockpitState());
        Services.AddSingleton(Mock.Of<IJSRuntime>());
    }

    [Fact]
    public void Insights_HidesTokenStatAndCostCard_WhenAllRunsHaveZeroTokens()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", tokens: 0), MakeRun("r2", tokens: 0)));
        RegisterInsightsServices(mock);

        var cut = Render<Insights>();

        // "Avg tokens / run" stat tile must be absent
        var statLabels = cut.FindAll(".cockpit-stat .cockpit-stat-l").Select(e => e.TextContent).ToList();
        statLabels.Should().NotContain(l => l.StartsWith("Avg tokens / run"),
            "the 'Avg tokens / run' stat must be hidden when no run has tokens");

        // Cost card (h2 "Cost") must be absent
        var h2s = cut.FindAll("h2").Select(e => e.TextContent.Trim()).ToList();
        h2s.Should().NotContain("Cost",
            "the Cost card must be hidden when no run has tokens or cost");
    }

    [Fact]
    public void Insights_ShowsTokenStatAndCostCard_WhenSomeRunsHaveTokens()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", tokens: 5000), MakeRun("r2", tokens: 0)));
        RegisterInsightsServices(mock);

        var cut = Render<Insights>();

        var statLabels = cut.FindAll(".cockpit-stat .cockpit-stat-l").Select(e => e.TextContent).ToList();
        statLabels.Should().Contain(l => l.StartsWith("Avg tokens / run"),
            "the 'Avg tokens / run' stat must be visible when some runs have tokens");

        var h2s = cut.FindAll("h2").Select(e => e.TextContent.Trim()).ToList();
        h2s.Should().Contain("Cost",
            "the Cost card must be visible when some runs have tokens");
    }

    // ─── Overview ────────────────────────────────────────────────────────────

    private void RegisterOverviewServices(
        Mock<IPipelineApiRunHistoryClient> mockRunHistory,
        Mock<IPipelineApiAgentClient>? mockAgents = null,
        Mock<IPipelineApiWorkItemClient>? mockWorkItems = null)
    {
        mockAgents ??= BuildEmptyAgentsMock();
        mockWorkItems ??= BuildEmptyWorkItemsMock();

        Services.AddSingleton(mockRunHistory.Object);
        Services.AddSingleton(mockAgents.Object);
        Services.AddSingleton(mockWorkItems.Object);
        Services.AddSingleton<ILoopStatusService>(Mock.Of<ILoopStatusService>());
        Services.AddSingleton(new CockpitState());
    }

    private static Mock<IPipelineApiAgentClient> BuildEmptyAgentsMock()
    {
        var mock = new Mock<IPipelineApiAgentClient>();
        mock.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto>());
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

    [Fact]
    public void Overview_HidesTokensRecentTile_WhenNoRunHasTokens()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", tokens: 0), MakeRun("r2", tokens: 0)));
        RegisterOverviewServices(mock);

        var cut = Render<Overview>();

        var statLabels = cut.FindAll(".cockpit-stat .cockpit-stat-l").Select(e => e.TextContent.Trim()).ToList();
        statLabels.Should().NotContain("Tokens · recent",
            "the 'Tokens · recent' stat tile must be absent when no run has tokens");
    }

    [Fact]
    public void Overview_ShowsTokensRecentTile_WhenSomeRunsHaveTokens()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(
                MakeRun("r1", tokens: 12345),
                MakeRun("r2", tokens: 0)));
        RegisterOverviewServices(mock);

        var cut = Render<Overview>();

        var statLabels = cut.FindAll(".cockpit-stat .cockpit-stat-l").Select(e => e.TextContent.Trim()).ToList();
        statLabels.Should().Contain("Tokens · recent",
            "the 'Tokens · recent' stat tile must be visible when some runs have tokens");
    }

    // ─── Runs ────────────────────────────────────────────────────────────────

    private void RegisterRunsServices(Mock<IPipelineApiRunHistoryClient> mockHistory)
    {
        var mockHub = new Mock<IAgentHubConnection>();
        mockHub.Setup(h => h.State).Returns(HubConnectionState.Disconnected);
        mockHub.Setup(h => h.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockHub.Setup(h => h.On<It.IsAnyType, It.IsAnyType, It.IsAnyType>(
            It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType, It.IsAnyType>>()))
            .Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType, It.IsAnyType>(
            It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType>>()))
            .Returns(Mock.Of<IDisposable>());

        Services.AddSingleton(mockHistory.Object);
        Services.AddSingleton(mockHub.Object);
        Services.AddSingleton(new CockpitState());
        Services.AddSingleton(Mock.Of<IJSRuntime>());
    }

    [Fact]
    public void Runs_HidesTokensColumn_WhenNoRunHasTokens()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", tokens: 0), MakeRun("r2", tokens: 0)));
        RegisterRunsServices(mock);

        var cut = Render<Runs>();

        var headers = cut.FindAll(".monitoring-table thead th").Select(e => e.TextContent.Trim()).ToList();
        headers.Should().NotContain("Tokens",
            "the Tokens column header must be absent when no run has tokens");
    }

    [Fact]
    public void Runs_ShowsTokensColumn_WhenSomeRunsHaveTokens()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", tokens: 7777), MakeRun("r2", tokens: 0)));
        RegisterRunsServices(mock);

        var cut = Render<Runs>();

        var headers = cut.FindAll(".monitoring-table thead th").Select(e => e.TextContent.Trim()).ToList();
        headers.Should().Contain("Tokens",
            "the Tokens column header must be visible when some runs have tokens");
    }
}

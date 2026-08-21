using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Tests for AgentHub.Consolidation.cs paths not covered by AgentHubBehaviorTests.
/// Covers: job-id mismatch → REJECTED, agent null (no status transition),
/// no HarnessSuggestions (skip save), no CreatedIssues (skip badge increment).
/// ReportFetchModelsResult is excluded because ModelFetchService is a sealed type — null! is passed.
/// </summary>
public sealed class AgentHubConsolidationTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<IConsolidationService> _mockConsolidation = new();
    private readonly ConsolidationBadgeService _badgeService = new();
    private readonly Mock<IChangeNotifier> _mockChangeNotifier = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            null!,  // ModelFetchService — sealed, cannot mock
            _mockConsolidation.Object,
            _badgeService,
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<IGateCommentFormatter>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = mockContext.Object;

        return hub;
    }

    private static AgentEntry CreateAgent(string agentId = "agent-1", string connectionId = "conn-1") => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    // ── ReportConsolidationComplete — job id mismatch → REJECTED ────────

    [Fact]
    public async Task ReportConsolidationComplete_JobIdMismatch_ReturnsRejected()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-active";  // Agent is working on a different job
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-different", Success = true };

        var returnValue = await hub.ReportConsolidationComplete(result);

        returnValue.Should().StartWith("REJECTED:");
    }

    [Fact]
    public async Task ReportConsolidationComplete_JobIdMismatch_DoesNotUpdateRunStatus()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-active";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-different", Success = true };

        await hub.ReportConsolidationComplete(result);

        _mockConsolidation.Verify(s =>
            s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReportConsolidationComplete_JobIdMismatch_DoesNotTransitionAgentToIdle()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-active";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-different", Success = true };

        await hub.ReportConsolidationComplete(result);

        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), AgentStatus.Idle), Times.Never);
        agent.ActiveJobId.Should().Be("crun-active", "active job must not be cleared on mismatch");
    }

    // ── ReportConsolidationComplete — agent not found (null agent) ───────

    [Fact]
    public async Task ReportConsolidationComplete_AgentNull_StillUpdatesRunStatus()
    {
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true, Summary = "Done" };

        await hub.ReportConsolidationComplete(result);

        // Run status must still be updated even when agent is not found.
        // No token data available (null result fields) → totalTokens must be 0.
        _mockConsolidation.Verify(s => s.UpdateRunAsync(
            (RunId)"crun-1", ConsolidationRunStatus.Succeeded, "Done",
            It.IsAny<CancellationToken>(), 0L),
            Times.Once);
    }

    [Fact]
    public async Task ReportConsolidationComplete_AgentNull_DoesNotTransitionStatus()
    {
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = false };

        await hub.ReportConsolidationComplete(result);

        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
        _mockFacade.Verify(f => f.Signal(), Times.Never);
    }

    [Fact]
    public async Task ReportConsolidationComplete_AgentNull_ReturnsDebugInfo()
    {
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };

        var returnValue = await hub.ReportConsolidationComplete(result);

        // Return value contains debug info and includes agentFound=False
        returnValue.Should().Contain("agentFound=False");
    }

    // ── ReportConsolidationComplete — no HarnessSuggestions ─────────────

    [Fact]
    public async Task ReportConsolidationComplete_NoHarnessSuggestions_SkipsSaveAndBadge()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult
        {
            JobId = "crun-1",
            Success = true,
            Summary = "OK",
            HarnessSuggestions = null   // no suggestions
        };

        await hub.ReportConsolidationComplete(result);

        _mockConsolidation.Verify(s =>
            s.SaveHarnessSuggestionsAsync(It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _badgeService.BadgeCount.Should().Be(0, "no suggestions → no badge increment");
    }

    // ── ReportConsolidationComplete — no CreatedIssues ───────────────────

    [Fact]
    public async Task ReportConsolidationComplete_NullCreatedIssues_SkipsBadgeIncrement()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult
        {
            JobId = "crun-1",
            Success = true,
            CreatedIssues = null
        };

        await hub.ReportConsolidationComplete(result);

        _badgeService.BadgeCount.Should().Be(0, "null CreatedIssues → no badge increment");
    }

    [Fact]
    public async Task ReportConsolidationComplete_EmptyCreatedIssues_SkipsBadgeIncrement()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult
        {
            JobId = "crun-1",
            Success = true,
            CreatedIssues = new List<CreatedIssueInfo>()  // empty list (Count == 0)
        };

        await hub.ReportConsolidationComplete(result);

        _badgeService.BadgeCount.Should().Be(0, "empty CreatedIssues → no badge increment");
    }

    // ── ReportConsolidationComplete — matching job id proceeds normally ───

    [Fact]
    public async Task ReportConsolidationComplete_MatchingJobId_TransitionsAgentToIdleAndSignals()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-1";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };

        await hub.ReportConsolidationComplete(result);

        _mockFacade.Verify(f => f.TransitionStatus("agent-1", AgentStatus.Idle), Times.Once);
        _mockFacade.Verify(f => f.Signal(), Times.Once);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task ReportConsolidationComplete_NullActiveJobId_ProceedsNormally()
    {
        // Agent with null ActiveJobId — the mismatch check only fires when both are non-null
        var agent = CreateAgent();
        agent.ActiveJobId = null;
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true, Summary = "Done" };

        var returnValue = await hub.ReportConsolidationComplete(result);

        // Must not start with REJECTED
        returnValue.Should().NotStartWith("REJECTED");
        _mockConsolidation.Verify(s => s.UpdateRunAsync(
            It.IsAny<RunId>(), ConsolidationRunStatus.Succeeded, "Done",
            It.IsAny<CancellationToken>(), 0L),
            Times.Once);
    }

    // ── ReportConsolidationComplete — token usage sum ────────────────────

    [Fact]
    public async Task ReportConsolidationComplete_WithTokenUsage_SumsAndPassesToUpdateRunAsync()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-tok";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult
        {
            JobId = "crun-tok",
            Success = true,
            Summary = "OK",
            ReviewTokenUsage = new TokenUsage { InputTokens = 100, OutputTokens = 50, ReasoningTokens = 10 },
            RefinementTokenUsage = new TokenUsage { InputTokens = 200, OutputTokens = 80, ReasoningTokens = 0 },
            DiffSummaryTokenUsage = new TokenUsage { InputTokens = 30, OutputTokens = 20, ReasoningTokens = 5 }
        };

        await hub.ReportConsolidationComplete(result);

        // Total = (100+50+10) + (200+80+0) + (30+20+5) = 160 + 280 + 55 = 495
        _mockConsolidation.Verify(s => s.UpdateRunAsync(
            (RunId)"crun-tok", ConsolidationRunStatus.Succeeded, "OK",
            CancellationToken.None, 495L),
            Times.Once);
    }

    [Fact]
    public async Task ReportConsolidationComplete_NullTokenUsage_PassesZeroTotal()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = "crun-notok";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult
        {
            JobId = "crun-notok",
            Success = true,
            Summary = "OK",
            ReviewTokenUsage = null,
            RefinementTokenUsage = null,
            DiffSummaryTokenUsage = null
        };

        await hub.ReportConsolidationComplete(result);

        _mockConsolidation.Verify(s => s.UpdateRunAsync(
            (RunId)"crun-notok", ConsolidationRunStatus.Succeeded, "OK",
            CancellationToken.None, 0L),
            Times.Once);
    }
}

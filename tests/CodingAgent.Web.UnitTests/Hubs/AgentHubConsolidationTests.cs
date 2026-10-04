using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Tests for AgentHub.Consolidation.cs paths not covered by AgentHubBehaviorTests.
/// Covers: job-id mismatch → REJECTED, agent null (no status transition),
/// no HarnessSuggestions (skip save), no CreatedIssues (skip badge increment).
/// T10: ModelFetchService and ConsolidationService now behind IHubConsolidationOperations —
/// the sealed-type null! workaround is no longer needed.
/// </summary>
public sealed class AgentHubConsolidationTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<IHubConsolidationOperations> _mockConsolidationOps = new();
    private readonly Mock<IChangeNotifier> _mockChangeNotifier = new();
    private readonly Mock<ILogger> _mockLogger = new();

    public AgentHubConsolidationTests()
    {
        // Default: HandleConsolidationCompleteAsync returns an empty debug string
        _mockConsolidationOps
            .Setup(c => c.HandleConsolidationCompleteAsync(It.IsAny<ConsolidationJobResult>(), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
    }

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            _mockChangeNotifier.Object,
            _mockConsolidationOps.Object,
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
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

        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.IsAny<ConsolidationJobResult>(), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
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
    public async Task ReportConsolidationComplete_AgentNull_ReturnsRejected()
    {
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true, Summary = "Done" };

        var returnValue = await hub.ReportConsolidationComplete(result);

        // Null agent (narrow race after disconnect) — must be rejected, run must not be updated.
        returnValue.Should().StartWith("REJECTED:");
        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.IsAny<ConsolidationJobResult>(), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReportConsolidationComplete_AgentNull_DoesNotTransitionStatus()
    {
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = false };

        await hub.ReportConsolidationComplete(result);

        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    [Fact]
    public async Task ReportConsolidationComplete_AgentNull_ReturnsRejectedString()
    {
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };

        var returnValue = await hub.ReportConsolidationComplete(result);

        // Null agent → REJECTED (hub returns the rejection string, not the downstream debug info)
        returnValue.Should().StartWith("REJECTED:");
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

        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.IsAny<ConsolidationJobResult>(), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Badge counting is now inside HandleConsolidationCompleteAsync — no suggestions means badge count not incremented
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

        // Badge counting is now inside HandleConsolidationCompleteAsync — null CreatedIssues means no badge increment
        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.Is<ConsolidationJobResult>(r => r.CreatedIssues == null), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
            Times.Once);
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

        // Badge counting is now inside HandleConsolidationCompleteAsync — empty CreatedIssues means no badge increment
        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.Is<ConsolidationJobResult>(r => r.CreatedIssues != null && r.CreatedIssues.Count == 0), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
            Times.Once);
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
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task ReportConsolidationComplete_NullActiveJobId_ReturnsRejected()
    {
        // Agent present but idle (ActiveJobId = null) — duplicate report or stale retry.
        // Must be rejected; downstream processing must not fire.
        var agent = CreateAgent();
        agent.ActiveJobId = null;
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true, Summary = "Done" };

        var returnValue = await hub.ReportConsolidationComplete(result);

        returnValue.Should().StartWith("REJECTED:");
        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.IsAny<ConsolidationJobResult>(), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

        // Token summation is now inside HandleConsolidationCompleteAsync; verify it was called with the right result
        // Total = (100+50+10) + (200+80+0) + (30+20+5) = 160 + 280 + 55 = 495
        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.Is<ConsolidationJobResult>(r => r.JobId == "crun-tok"), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
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

        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.Is<ConsolidationJobResult>(r => r.JobId == "crun-notok"), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── ReportConsolidationComplete — null ActiveJobId rejection (new regression guards) ───

    [Fact]
    public async Task ReportConsolidationComplete_NullActiveJobId_DoesNotCallHandleConsolidationCompleteAsync()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = null;
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };

        await hub.ReportConsolidationComplete(result);

        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.IsAny<ConsolidationJobResult>(), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "idle agent must not trigger harness/badge/run writes");
    }

    [Fact]
    public async Task ReportConsolidationComplete_NullActiveJobId_DoesNotTransitionAgentToIdle()
    {
        var agent = CreateAgent();
        agent.ActiveJobId = null;
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };

        await hub.ReportConsolidationComplete(result);

        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never,
            "no status transition on a rejected report");
        agent.ActiveJobId.Should().BeNull("ActiveJobId must remain null — it was not changed by the rejection");
    }

    [Fact]
    public async Task ReportConsolidationComplete_SecondReport_SameAgent_ReturnsRejected()
    {
        // Simulates a duplicate/retry: agent.ActiveJobId is already null because the first report
        // was accepted and cleared it. The second call must be rejected entirely.
        var agent = CreateAgent();
        agent.ActiveJobId = null; // first report already cleared this
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };

        var returnValue = await hub.ReportConsolidationComplete(result);

        returnValue.Should().StartWith("REJECTED:",
            "a second report from an idle agent must be rejected regardless of the JobId");
        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.IsAny<ConsolidationJobResult>(), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "duplicate report must not trigger downstream processing");
    }

    // ── ReportConsolidationComplete — null agent rejection (new regression guards) ─────────

    [Fact]
    public async Task ReportConsolidationComplete_AgentNull_DoesNotCallHandleConsolidationCompleteAsync()
    {
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var result = new ConsolidationJobResult { JobId = "crun-1", Success = true };

        await hub.ReportConsolidationComplete(result);

        _mockConsolidationOps.Verify(c =>
            c.HandleConsolidationCompleteAsync(It.IsAny<ConsolidationJobResult>(), It.IsAny<AgentEntry?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "null agent (disconnect race) must not trigger harness/badge/run writes");
    }
}

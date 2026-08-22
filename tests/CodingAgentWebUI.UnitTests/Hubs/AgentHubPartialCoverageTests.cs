using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Targeted coverage tests for AgentHub partial classes (Chat session-ownership paths)
/// not covered by AgentHubBehaviorTests.
/// </summary>
public sealed class AgentHubPartialCoverageTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<IConsolidationService> _mockConsolidation = new();
    private readonly ConsolidationBadgeService _badgeService = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private static AgentEntry CreateAgent(string agentId = "agent-1", string connectionId = "conn-1") => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    private AgentHub CreateHub(string connectionId = "conn-test")
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            null!,  // ModelFetchService — not needed for these tests
            _mockConsolidation.Object,
            _badgeService,
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            new AgentTokenRefreshService(_mockFacade.Object, _mockTokenVending.Object, _mockLogger.Object),
            Mock.Of<IGateCommentFormatter>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = mockContext.Object;

        return hub;
    }

    // ── AgentHub.Chat — ReportChatResponse session ownership ────────────────

    [Fact]
    public async Task ReportChatResponse_WrongSession_ThrowsHubException()
    {
        var hub = CreateHub("conn-1");

        var agent = CreateAgent("agent-1", "conn-1");
        agent.ActiveChatSessionId = "session-correct";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var message = new ChatResponseMessage
        {
            SessionId = "session-wrong",  // mismatch
            Lines = []
        };

        var act = async () => await hub.ReportChatResponse(message);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*session-wrong*not assigned*");
    }

    [Fact]
    public async Task ReportChatResponse_NullArg_Throws()
    {
        var hub = CreateHub();

        var act = async () => await hub.ReportChatResponse(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReportChatResponse_NoAgent_ThrowsHubException()
    {
        var hub = CreateHub("conn-orphan");
        _mockFacade.Setup(f => f.GetByConnectionId("conn-orphan")).Returns((AgentEntry?)null);

        var message = new ChatResponseMessage { SessionId = "sess-1", Lines = [] };

        // agent is null → agent?.ActiveChatSessionId is null ≠ "sess-1"
        var act = async () => await hub.ReportChatResponse(message);

        await act.Should().ThrowAsync<HubException>();
    }

    // ── AgentHub.Chat — ReportChatCompleted session ownership ───────────────

    [Fact]
    public async Task ReportChatCompleted_WrongSession_ThrowsHubException()
    {
        var hub = CreateHub("conn-1");

        var agent = CreateAgent("agent-1", "conn-1");
        agent.ActiveChatSessionId = "session-correct";
        _mockFacade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var message = new ChatCompletedMessage
        {
            SessionId = "session-wrong",
            ExitCode = 0
        };

        var act = async () => await hub.ReportChatCompleted(message);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*session-wrong*not assigned*");
    }

    [Fact]
    public async Task ReportChatCompleted_NullArg_Throws()
    {
        var hub = CreateHub();

        var act = async () => await hub.ReportChatCompleted(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReportChatCompleted_NoAgent_ThrowsHubException()
    {
        var hub = CreateHub("conn-orphan");
        _mockFacade.Setup(f => f.GetByConnectionId("conn-orphan")).Returns((AgentEntry?)null);

        var message = new ChatCompletedMessage { SessionId = "sess-1", ExitCode = 0 };

        var act = async () => await hub.ReportChatCompleted(message);

        await act.Should().ThrowAsync<HubException>();
    }
}

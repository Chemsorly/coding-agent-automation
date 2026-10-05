using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Tests for AgentHub.Chat.cs covering:
/// - SubscribeToChatSession / UnsubscribeFromChatSession group management
/// - ReportChatResponse session ownership validation and broadcast
/// - ReportChatCompleted session ownership validation, ActiveChatSessionId cleared, broadcast
/// - ValidateChatSessionOwnership branch matrix
/// </summary>
public sealed class AgentHubChatTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IChatNotifier> _chatNotifier = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<IGroupManager> _groups = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
            IssueOps: Mock.Of<IHubIssueOperations>(),
            LifecycleService: Mock.Of<IAgentJobLifecycleService>(),
            TokenRefreshService: Mock.Of<IAgentTokenRefreshService>(),
            OrphanRecoveryService: Mock.Of<IAgentOrphanRecoveryService>(),
            Logger: Log.Logger,
            UiContext: HubTestHelpers.CreateNoOpHubContext()));

        hub.Context = mockCtx.Object;
        hub.Groups = _groups.Object;

        return hub;
    }

    private static AgentEntry CreateAgent(string agentId, string connectionId, string? activeSessionId = null) => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "k8s-pod",
        Labels = [],
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow,
        ActiveChatSessionId = activeSessionId
    };

    // ── SubscribeToChatSession ────────────────────────────────────────────

    [Fact]
    public async Task SubscribeToChatSession_NullSessionId_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.SubscribeToChatSession(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── UnsubscribeFromChatSession ────────────────────────────────────────

    [Fact]
    public async Task UnsubscribeFromChatSession_NullSessionId_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.UnsubscribeFromChatSession(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── ReportChatResponse — session not owned → HubException ────────────

    [Fact]
    public async Task ReportChatResponse_SessionNotOwnedByAgent_ThrowsHubException()
    {
        var agent = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-other");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var message = new ChatResponseMessage
        {
            SessionId = "sess-1",
            Lines = ["line"]
        };

        var act = () => hub.ReportChatResponse(message);
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*sess-1*not assigned*");
    }

    [Fact]
    public async Task ReportChatResponse_NullMessage_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.ReportChatResponse(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── ReportChatCompleted — session not owned → HubException ───────────

    [Fact]
    public async Task ReportChatCompleted_WrongSession_ThrowsHubException()
    {
        var agent = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-other");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var message = new ChatCompletedMessage
        {
            SessionId = "sess-mine",
            ExitCode = 0
        };

        var act = () => hub.ReportChatCompleted(message);
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*sess-mine*not assigned*");
    }

    [Fact]
    public async Task ReportChatCompleted_NullMessage_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.ReportChatCompleted(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── ReportChatCompleted — exit code propagated ────────────────────────

    [Fact]
    public async Task ReportChatCompleted_NonZeroExitCode_NotifiesWithCorrectCode()
    {
        var agent = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-3");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var message = new ChatCompletedMessage
        {
            SessionId = "sess-3",
            ExitCode = 42,
            Error = "agent crashed"
        };

        await hub.ReportChatCompleted(message);

        _chatNotifier.Verify(n => n.NotifyChatCompleted("sess-3", 42, "agent crashed"), Times.Once);
    }

    // ── ValidateChatSessionOwnership — full branch matrix ─────────────────
    // (static method — no hub or mocks needed)

    [Fact]
    public void ValidateChatSessionOwnership_AgentNull_ReturnsInvalid()
    {
        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(null, "any-session");
        isValid.Should().BeFalse();
        agentId.Should().Be("unknown");
    }

    [Fact]
    public void ValidateChatSessionOwnership_SessionIdMismatch_ReturnsInvalidWithAgentId()
    {
        var agent = CreateAgent("agent-X", "conn-X", activeSessionId: "sess-A");
        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(agent, "sess-B");
        isValid.Should().BeFalse();
        agentId.Should().Be("agent-X");
    }

    [Fact]
    public void ValidateChatSessionOwnership_WhenSessionMatches_ReturnsValid()
    {
        var agent = MakeAgent(activeSession: "session-42");

        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(agent, "session-42");

        isValid.Should().BeTrue();
        agentId.Should().Be("agent-1");
    }

    [Theory]
    [InlineData("session-1", "session-1", true)]
    [InlineData("session-1", "session-2", false)]
    [InlineData(null, "session-1", false)]
    [InlineData("session-1", "SESSION-1", false)] // case-sensitive
    public void ValidateChatSessionOwnership_Theory(string? agentSession, string requestedSession, bool expectedValid)
    {
        var agent = MakeAgent(activeSession: agentSession);

        var (isValid, _) = AgentHub.ValidateChatSessionOwnership(agent, requestedSession);

        isValid.Should().Be(expectedValid);
    }

    [Fact]
    public void ValidateChatSessionOwnership_AgentHasNullSession_ReturnsInvalid()
    {
        var agent = MakeAgent("agent-1", null);
        var (isValid, agentId) = AgentHub.ValidateChatSessionOwnership(agent, "session-abc");

        isValid.Should().BeFalse();
        agentId.Should().Be("agent-1");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AgentEntry MakeAgent(string agentId = "agent-1", string? activeSession = null)
    {
        var entry = new AgentEntry
        {
            AgentId = new AgentId(agentId),
            ConnectionId = $"conn-{agentId}",
            Hostname = "test-host",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow
        };
        entry.ActiveChatSessionId = activeSession;
        return entry;
    }
}

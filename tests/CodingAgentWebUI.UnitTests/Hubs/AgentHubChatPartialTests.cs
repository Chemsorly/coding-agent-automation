using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Tests for AgentHub.Chat.cs covering:
/// - SubscribeToChatSession / UnsubscribeFromChatSession group management
/// - ReportChatResponse session ownership validation and broadcast
/// - ReportChatCompleted session ownership validation, ActiveChatSessionId cleared, broadcast
/// </summary>
public sealed class AgentHubChatPartialTests
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
            ModelFetchService: null!, // sealed type — not needed for chat tests
            ConsolidationService: Mock.Of<IConsolidationService>(),
            BadgeService: new ConsolidationBadgeService(),
            IssueOps: Mock.Of<IHubIssueOperations>(),
            LifecycleService: Mock.Of<IAgentJobLifecycleService>(),
            TokenRefreshService: Mock.Of<IAgentTokenRefreshService>(),
            GateCommentFormatter: Mock.Of<IGateCommentFormatter>(),
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
    public async Task SubscribeToChatSession_CallsGroupAddToGroup()
    {
        _groups.Setup(g => g.AddToGroupAsync("conn-1", "chat-session-abc", default))
               .Returns(Task.CompletedTask);

        var hub = CreateHub();
        await hub.SubscribeToChatSession("abc");

        _groups.Verify(g => g.AddToGroupAsync("conn-1", "chat-session-abc", default), Times.Once);
    }

    [Fact]
    public async Task SubscribeToChatSession_NullSessionId_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.SubscribeToChatSession(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── UnsubscribeFromChatSession ────────────────────────────────────────

    [Fact]
    public async Task UnsubscribeFromChatSession_CallsGroupRemoveFromGroup()
    {
        _groups.Setup(g => g.RemoveFromGroupAsync("conn-1", "chat-session-xyz", default))
               .Returns(Task.CompletedTask);

        var hub = CreateHub();
        await hub.UnsubscribeFromChatSession("xyz");

        _groups.Verify(g => g.RemoveFromGroupAsync("conn-1", "chat-session-xyz", default), Times.Once);
    }

    [Fact]
    public async Task UnsubscribeFromChatSession_NullSessionId_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.UnsubscribeFromChatSession(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── ReportChatResponse — valid ownership ──────────────────────────────

    [Fact]
    public async Task ReportChatResponse_ValidSession_BroadcastsAndNotifies()
    {
        var agent = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-1");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var message = new ChatResponseMessage
        {
            SessionId = "sess-1",
            Lines = ["hello world"]
        };

        var act = () => hub.ReportChatResponse(message);
        await act.Should().NotThrowAsync("valid session ownership must succeed");

        _chatNotifier.Verify(n => n.NotifyChatResponse("sess-1", It.IsAny<IReadOnlyList<string>>()), Times.Once);
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
    public async Task ReportChatResponse_AgentNotFound_ThrowsHubException()
    {
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var message = new ChatResponseMessage
        {
            SessionId = "sess-1",
            Lines = ["line"]
        };

        var act = () => hub.ReportChatResponse(message);
        await act.Should().ThrowAsync<HubException>(
            "unknown connection has no session ownership");
    }

    [Fact]
    public async Task ReportChatResponse_NullMessage_Throws()
    {
        var hub = CreateHub();
        var act = () => hub.ReportChatResponse(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── ReportChatCompleted — valid ownership ─────────────────────────────

    [Fact]
    public async Task ReportChatCompleted_ValidSession_ClearsActiveSessionIdAndNotifies()
    {
        var agent = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-2");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub();
        var message = new ChatCompletedMessage
        {
            SessionId = "sess-2",
            ExitCode = 0,
            Error = null
        };

        await hub.ReportChatCompleted(message);

        agent.ActiveChatSessionId.Should().BeNull(
            "ReportChatCompleted must clear ActiveChatSessionId so the next prompt gets a fresh session");
        _chatNotifier.Verify(n => n.NotifyChatCompleted("sess-2", 0, null), Times.Once);
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
    public async Task ReportChatCompleted_AgentNotFound_ThrowsHubException()
    {
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns((AgentEntry?)null);

        var hub = CreateHub();
        var message = new ChatCompletedMessage { SessionId = "sess-1", ExitCode = 1 };

        var act = () => hub.ReportChatCompleted(message);
        await act.Should().ThrowAsync<HubException>();
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
    public void ValidateChatSessionOwnership_NoActiveSession_ReturnsInvalid()
    {
        var agent = CreateAgent("agent-X", "conn-X", activeSessionId: null);
        var (isValid, _) = AgentHub.ValidateChatSessionOwnership(agent, "sess-1");
        isValid.Should().BeFalse("null ActiveChatSessionId can never match a real session");
    }
}

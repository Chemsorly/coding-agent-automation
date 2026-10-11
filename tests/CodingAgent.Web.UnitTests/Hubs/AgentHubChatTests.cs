using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Contracts;
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
        mockCtx.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

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
        // TODO: This test exercises the "GetByAgentIdAsync returns null (entry never registered)"
        // rejection path, NOT the "session mismatch" path implied by the name. The snapshot
        // value activeSessionId: "sess-other" is never consulted — GetByAgentIdAsync is not set
        // up so Moq returns null, triggering the authoritative-entry-null branch. Rename this
        // test to ReportChatResponse_AuthoritativeEntryNullForCallerAgent_ThrowsHubException
        // and add a dedicated test for the real-world scenario where the snapshot shows an old
        // session but the authoritative entry has the correct one (covered separately by
        // ReportChatResponse_AuthoritativeEntryHasWrongSession_ThrowsHubException but the
        // "snapshot says S, authoritative says S, message carries S'" race is not tested).
        // GetByConnectionId returns agent-1; GetByAgentIdAsync returns null (not stamped)
        // → rejected with agent-1 in the message
        var agent = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-other");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        // GetByAgentIdAsync not set up → returns null (Moq default for Task<T?> returning methods)

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

    // ── ReportChatResponse — cross-replica: snapshot null, authoritative has session → accepted ──

    [Fact]
    public async Task ReportChatResponse_SnapshotHasNoSession_AuthoritativeHasSession_Accepted()
    {
        // Snapshot (GetByConnectionId) has no ActiveChatSessionId — this replica was not the
        // one that handled SendChatPrompt. Authoritative entry (GetByAgentIdAsync) has the
        // correct session ID written by the other replica.
        var caller = CreateAgent("agent-1", "conn-1", activeSessionId: null);
        var authoritativeEntry = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-1");

        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(caller);
        _facade.Setup(f => f.GetByAgentIdAsync(new AgentId("agent-1"), It.IsAny<CancellationToken>()))
               .ReturnsAsync(authoritativeEntry);

        var hub = CreateHub();
        var message = new ChatResponseMessage
        {
            SessionId = "sess-1",
            Lines = ["cross-replica line"]
        };

        // Must not throw — the authoritative store has the correct session
        var act = () => hub.ReportChatResponse(message);
        await act.Should().NotThrowAsync();
    }

    // ── ReportChatResponse — GetByAgentIdAsync returns null → rejected with caller's AgentId ──

    [Fact]
    public async Task ReportChatResponse_AuthoritativeEntryNull_ThrowsHubExceptionWithCallerId()
    {
        var caller = CreateAgent("agent-1", "conn-1");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(caller);
        _facade.Setup(f => f.GetByAgentIdAsync(new AgentId("agent-1"), It.IsAny<CancellationToken>()))
               .ReturnsAsync((AgentEntry?)null);

        var hub = CreateHub();
        var message = new ChatResponseMessage { SessionId = "sess-1", Lines = ["line"] };

        var act = () => hub.ReportChatResponse(message);
        // Message must contain the caller's AgentId, not "unknown"
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*sess-1*not assigned*agent-1*");
    }

    // ── ReportChatResponse — GetByAgentIdAsync returns wrong session → rejected ──────────────

    [Fact]
    public async Task ReportChatResponse_AuthoritativeEntryHasWrongSession_ThrowsHubException()
    {
        var caller = CreateAgent("agent-1", "conn-1");
        var authoritativeEntry = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-X");

        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(caller);
        _facade.Setup(f => f.GetByAgentIdAsync(new AgentId("agent-1"), It.IsAny<CancellationToken>()))
               .ReturnsAsync(authoritativeEntry);

        var hub = CreateHub();
        var message = new ChatResponseMessage { SessionId = "sess-Y", Lines = ["line"] };

        var act = () => hub.ReportChatResponse(message);
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*sess-Y*not assigned*");
    }

    // ── ReportChatCompleted — session not owned → HubException ───────────

    [Fact]
    public async Task ReportChatCompleted_WrongSession_ThrowsHubException()
    {
        var agent = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-other");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        // GetByAgentIdAsync not set up → returns null → rejected

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
        var caller = CreateAgent("agent-1", "conn-1");
        var authoritativeEntry = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-3");

        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(caller);
        _facade.Setup(f => f.GetByAgentIdAsync(new AgentId("agent-1"), It.IsAny<CancellationToken>()))
               .ReturnsAsync(authoritativeEntry);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
               .Returns(Task.CompletedTask);

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

    // ── ReportChatCompleted — UpdateAgentFieldAsync called, snapshot not mutated ────────────

    [Fact]
    public async Task ReportChatCompleted_ClearsSessionViaUpdateAgentFieldAsync_NotDirectMutation()
    {
        var caller = CreateAgent("agent-1", "conn-1");
        var authoritativeEntry = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-4");

        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(caller);
        _facade.Setup(f => f.GetByAgentIdAsync(new AgentId("agent-1"), It.IsAny<CancellationToken>()))
               .ReturnsAsync(authoritativeEntry);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
               .Returns(Task.CompletedTask);

        var hub = CreateHub();
        var message = new ChatCompletedMessage { SessionId = "sess-4", ExitCode = 0 };

        await hub.ReportChatCompleted(message);

        // Must delegate the clear to UpdateAgentFieldAsync — not mutate the snapshot object
        _facade.Verify(f => f.UpdateAgentFieldAsync(
            new AgentId("agent-1"),
            AgentFieldNames.ActiveChatSessionId,
            null), Times.Once);

        // TODO: This assertion is tautological — `caller` is created with activeSessionId: null
        // so it is always null regardless of whether the hub mutated the snapshot. To make this
        // assertion meaningful, create caller with a non-null ActiveChatSessionId (e.g. "initial")
        // and assert it is still "initial" after the call, proving the hub did not zero it out.
        // The snapshot object (caller) returned by GetByConnectionId must NOT be mutated
        caller.ActiveChatSessionId.Should().BeNull(
            because: "caller.ActiveChatSessionId starts null and hub must not set it to null either; " +
                     "more importantly the hub must not set it to any non-null value");
    }

    // ── ReportChatCompleted — clear awaited before OnChatCompleted broadcast ──────────────────

    [Fact]
    public async Task ReportChatCompleted_AwaitsUpdateBeforeBroadcast()
    {
        // Arrange: UpdateAgentFieldAsync blocks until TCS is released.
        // We use a trackable hub context to detect when OnChatCompleted fires.
        var caller = CreateAgent("agent-1", "conn-1");
        var authoritativeEntry = CreateAgent("agent-1", "conn-1", activeSessionId: "sess-5");

        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(caller);
        _facade.Setup(f => f.GetByAgentIdAsync(new AgentId("agent-1"), It.IsAny<CancellationToken>()))
               .ReturnsAsync(authoritativeEntry);

        var updateTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
               .Returns(updateTcs.Task.ContinueWith(_ => { }));

        // Track whether OnChatCompleted has been broadcast
        var broadcastFired = false;
        var uiClientProxy = new Mock<IClientProxy>();
        uiClientProxy
            .Setup(p => p.SendCoreAsync(HubMethodNames.OnChatCompleted, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback(() => broadcastFired = true)
            .Returns(Task.CompletedTask);
        var uiClients = new Mock<IHubClients>();
        uiClients.Setup(c => c.Group(It.IsAny<string>())).Returns(uiClientProxy.Object);
        var uiContext = new Mock<IHubContext<AgentHub>>();
        uiContext.Setup(c => c.Clients).Returns(uiClients.Object);

        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns("conn-1");
        mockCtx.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

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
            UiContext: uiContext.Object));
        hub.Context = mockCtx.Object;
        hub.Groups = _groups.Object;

        var message = new ChatCompletedMessage { SessionId = "sess-5", ExitCode = 0 };
        var hubTask = hub.ReportChatCompleted(message);

        // TODO: This Task.Delay(50) is a timing-based heuristic and is inherently flaky on a
        // loaded CI machine — the scheduler may not resume the hub task within 50 ms, causing
        // the assertion to be vacuously true. Replace with a deterministic approach, e.g. poll
        // broadcastFired until either it becomes true or the TCS is released, using a short
        // timeout, or restructure to use Task.Yield() + a SemaphoreSlim gating the broadcast.
        // Broadcast must not have fired yet — UpdateAgentFieldAsync is still pending
        await Task.Delay(50); // brief yield to let the method advance up to the await
        broadcastFired.Should().BeFalse("OnChatCompleted must not broadcast before UpdateAgentFieldAsync completes");

        // Release the update
        updateTcs.SetResult(true);
        await hubTask;

        broadcastFired.Should().BeTrue("OnChatCompleted must broadcast after UpdateAgentFieldAsync completes");
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

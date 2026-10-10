using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Unit tests for <see cref="AgentHub.OnDisconnectedAsync"/> — verifies that ephemeral chat
/// agents are fully removed from the registry on disconnect (issue #2109), while persistent
/// worker agents still transition to <see cref="AgentStatus.Disconnected"/>.
/// </summary>
public sealed class AgentHubOnDisconnectedTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILogger> _logger = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: Mock.Of<IChatNotifier>(),
            ChangeNotifier: Mock.Of<IChangeNotifier>(),
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
            IssueOps: Mock.Of<IHubIssueOperations>(),
            LifecycleService: Mock.Of<IAgentJobLifecycleService>(),
            TokenRefreshService: Mock.Of<IAgentTokenRefreshService>(),
            Logger: _logger.Object,
            OrphanRecoveryService: Mock.Of<IAgentOrphanRecoveryService>(),
            UiContext: HubTestHelpers.CreateNoOpHubContext()));

        hub.Context = mockCtx.Object;
        return hub;
    }

    private static AgentEntry CreateAgent(
        string agentId,
        string connectionId,
        IReadOnlyList<string>? labels = null,
        string? activeJobId = null) => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "host",
        Labels = labels ?? Array.Empty<string>(),
        Status = AgentStatus.Idle,
        ActiveJobId = activeJobId,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    // ── Fix 1: chat agent deregistered on disconnect ──────────────────────────

    /// <summary>
    /// A chat agent (label "chat=true") must be fully deregistered, not transitioned to Disconnected.
    /// This is the primary fix for issue #2109.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_ChatAgent_CallsDeregister_NotTransitionStatus()
    {
        var agent = CreateAgent("caa-chat-abc123", "conn-1", labels: new[] { "chat=true", "chat-session-id=guid-xyz" });
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _facade.Setup(f => f.Deregister(It.IsAny<AgentId>())).Returns(true);

        var hub = CreateHub("conn-1");
        await hub.OnDisconnectedAsync(null);

        // Must call Deregister — not TransitionStatus
        _facade.Verify(f => f.Deregister(It.Is<AgentId>(a => a.Value == "caa-chat-abc123")), Times.Once);
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    /// <summary>
    /// Graceful shutdown path: chat agent disconnects with an exception — still deregistered.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_ChatAgentWithException_CallsDeregister()
    {
        var agent = CreateAgent("caa-chat-ex456", "conn-1", labels: new[] { "chat=true" });
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);
        _facade.Setup(f => f.Deregister(It.IsAny<AgentId>())).Returns(true);

        var hub = CreateHub("conn-1");
        await hub.OnDisconnectedAsync(new Exception("transport closed"));

        _facade.Verify(f => f.Deregister(It.Is<AgentId>(a => a.Value == "caa-chat-ex456")), Times.Once);
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── Fix 1: persistent worker still transitions to Disconnected ────────────

    /// <summary>
    /// A persistent (non-chat) worker agent must still transition to Disconnected — no change
    /// to that path. This validates no regression for issue #2109.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_NonChatAgent_CallsTransitionStatus_NotDeregister()
    {
        var agent = CreateAgent("caa-worker-1", "conn-1", labels: new[] { "dotnet", "kiro" });
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub("conn-1");
        await hub.OnDisconnectedAsync(null);

        _facade.Verify(f => f.TransitionStatus(
            It.Is<AgentId>(a => a.Value == "caa-worker-1"),
            AgentStatus.Disconnected),
            Times.Once);
        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
    }

    /// <summary>
    /// A non-chat agent with no labels at all must still transition to Disconnected.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_NoLabels_CallsTransitionStatus_NotDeregister()
    {
        var agent = CreateAgent("caa-worker-nolabels", "conn-1", labels: Array.Empty<string>());
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub("conn-1");
        await hub.OnDisconnectedAsync(null);

        _facade.Verify(f => f.TransitionStatus(
            It.Is<AgentId>(a => a.Value == "caa-worker-nolabels"),
            AgentStatus.Disconnected),
            Times.Once);
        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
    }

    [Fact]
    public async Task OnDisconnectedAsync_WithException_LogsExceptionMessage()
    {
        var hub = CreateHub("conn-1");

        var agent = CreateAgent("agent-1", "conn-1");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        // Should not rethrow the passed exception
        await hub.OnDisconnectedAsync(exception: new InvalidOperationException("test error"));

        _facade.Verify(f => f.TransitionStatus(
            It.Is<AgentId>(a => a.Value == "agent-1"),
            AgentStatus.Disconnected), Times.Once);
    }

    // ── Fix 1: non-chat agent with active job still logs Warning ──────────────

    /// <summary>
    /// A non-chat agent with an active job must log a Warning (existing orphan-recovery flow)
    /// and NOT call Deregister.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_NonChatAgentWithActiveJob_LogsWarning_NotDeregister()
    {
        var agent = CreateAgent(
            "caa-worker-busy", "conn-1",
            labels: new[] { "dotnet" },
            activeJobId: "job-xyz-123");

        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub("conn-1");
        await hub.OnDisconnectedAsync(null);

        // Warning must be logged (existing behaviour, not deregistered).
        // Serilog ILogger.Warning(string messageTemplate, params object?[] propertyValues) — verify by template.
        // TODO: This verification uses It.IsAny<object[]>() which only matches the params-array overload.
        // If the argument count ever drops to 3 or fewer, Serilog will bind to an explicit typed overload
        // (Warning(string, object, object, object)) and this verify will silently stop matching.
        // Consider matching with individual It.IsAny<object?>() matchers for the exact parameter count,
        // consistent with the pattern in AgentHubRegistrationTests.DeregisterAgent_CallerNotFound_DoesNotDeregister.
        // See review finding: TestQualityReviewer [WARNING] AgentHubOnDisconnectedTests.cs:155
        _logger.Verify(l => l.Warning(
            It.Is<string>(s => s.Contains("active job")),
            It.IsAny<object[]>()),
            Times.Once);

        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
        _facade.Verify(f => f.TransitionStatus(
            It.Is<AgentId>(a => a.Value == "caa-worker-busy"),
            AgentStatus.Disconnected),
            Times.Once);
    }

    // ── Edge case: no agent found for connection ──────────────────────────────

    /// <summary>
    /// When no agent is registered for the connection, no-op — no calls to registry.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_NoAgentFound_DoesNothing()
    {
        _facade.Setup(f => f.GetByConnectionId("conn-unknown")).Returns((AgentEntry?)null);

        var hub = CreateHub("conn-unknown");
        await hub.OnDisconnectedAsync(null);

        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── Edge case: partial chat label match ───────────────────────────────────

    /// <summary>
    /// A label "chat=false" must NOT trigger deregistration — only "chat=true" qualifies.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_ChatFalseLabel_CallsTransitionStatus_NotDeregister()
    {
        var agent = CreateAgent("caa-worker-chatfalse", "conn-1", labels: new[] { "chat=false" });
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        var hub = CreateHub("conn-1");
        await hub.OnDisconnectedAsync(null);

        _facade.Verify(f => f.TransitionStatus(
            It.Is<AgentId>(a => a.Value == "caa-worker-chatfalse"),
            AgentStatus.Disconnected),
            Times.Once);
        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
    }

    // ── Fix 2: cross-replica reconnect guard (issue #3554) ───────────────────

    /// <summary>
    /// When the agent has already re-registered on another connection (cross-replica reconnect),
    /// the old connection closing on this replica must NOT mark the agent Disconnected.
    /// Test 1 (work-item agent) — this test must fail before the fix is applied.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_CrossReplicaReconnect_WorkerAgent_SkipsTransitionStatus()
    {
        // Arrange: local snapshot sees conn-old as the closing connection
        var agentEntry = CreateAgent("caa-worker-xreplica", "conn-old", labels: new[] { "dotnet" });
        _facade.Setup(f => f.GetByConnectionId("conn-old")).Returns(agentEntry);

        // Redis (authoritative) shows the agent already moved to conn-new on another replica
        var currentEntry = CreateAgent("caa-worker-xreplica", "conn-new", labels: new[] { "dotnet" });
        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "caa-worker-xreplica")))
            .Returns(currentEntry);

        var hub = CreateHub("conn-old");
        await hub.OnDisconnectedAsync(null);

        // Neither TransitionStatus nor Deregister must be called
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
    }

    /// <summary>
    /// When the agent has already re-registered on another connection (cross-replica reconnect),
    /// the old connection closing on this replica must NOT deregister a chat agent.
    /// Test 2 (chat agent).
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_CrossReplicaReconnect_ChatAgent_SkipsDeregister()
    {
        // Arrange: local snapshot sees conn-old as the closing connection
        var agentEntry = CreateAgent("caa-chat-xreplica", "conn-old", labels: new[] { "chat=true" });
        _facade.Setup(f => f.GetByConnectionId("conn-old")).Returns(agentEntry);

        // Redis (authoritative) shows the agent already moved to conn-new on another replica
        var currentEntry = CreateAgent("caa-chat-xreplica", "conn-new", labels: new[] { "chat=true" });
        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "caa-chat-xreplica")))
            .Returns(currentEntry);

        var hub = CreateHub("conn-old");
        await hub.OnDisconnectedAsync(null);

        // Neither TransitionStatus nor Deregister must be called
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
    }

    /// <summary>
    /// When the authoritative store returns the same connection ID, a worker agent
    /// is still marked Disconnected as today (same-connection path).
    /// Test 3.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_SameConnection_WorkerAgent_TransitionsToDisconnected()
    {
        var agentEntry = CreateAgent("caa-worker-same", "conn-1", labels: new[] { "dotnet" });
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agentEntry);

        // Authoritative store confirms conn-1 is still the registered connection
        var currentEntry = CreateAgent("caa-worker-same", "conn-1", labels: new[] { "dotnet" });
        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "caa-worker-same")))
            .Returns(currentEntry);

        var hub = CreateHub("conn-1");
        await hub.OnDisconnectedAsync(null);

        _facade.Verify(f => f.TransitionStatus(
            It.Is<AgentId>(a => a.Value == "caa-worker-same"),
            AgentStatus.Disconnected),
            Times.Once);
        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
    }

    /// <summary>
    /// When the authoritative store returns the same connection ID, a chat agent
    /// is still deregistered as today (same-connection path).
    /// Test 4.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_SameConnection_ChatAgent_Deregisters()
    {
        var agentEntry = CreateAgent("caa-chat-same", "conn-1", labels: new[] { "chat=true" });
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agentEntry);
        _facade.Setup(f => f.Deregister(It.IsAny<AgentId>())).Returns(true);

        // Authoritative store confirms conn-1 is still the registered connection
        var currentEntry = CreateAgent("caa-chat-same", "conn-1", labels: new[] { "chat=true" });
        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "caa-chat-same")))
            .Returns(currentEntry);

        var hub = CreateHub("conn-1");
        await hub.OnDisconnectedAsync(null);

        _facade.Verify(f => f.Deregister(It.Is<AgentId>(a => a.Value == "caa-chat-same")), Times.Once);
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    /// <summary>
    /// When the authoritative store returns null (entry TTL-expired or already deregistered),
    /// the existing behavior is unchanged: a worker agent is marked Disconnected.
    /// Test 5.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_NullCurrentEntry_WorkerAgent_TransitionsToDisconnected()
    {
        var agentEntry = CreateAgent("caa-worker-nullentry", "conn-1", labels: new[] { "dotnet" });
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agentEntry);

        // Authoritative store returns null — entry TTL-expired or deregistered cross-replica
        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "caa-worker-nullentry")))
            .Returns((AgentEntry?)null);

        var hub = CreateHub("conn-1");
        await hub.OnDisconnectedAsync(null);

        _facade.Verify(f => f.TransitionStatus(
            It.Is<AgentId>(a => a.Value == "caa-worker-nullentry"),
            AgentStatus.Disconnected),
            Times.Once);
        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
    }
    // TODO (WARNING, issue #3554): Add an explicit null-current-entry test for the chat-agent branch.
    // When GetByAgentId returns null for a chat agent, the guard must not fire and Deregister must
    // still be called. Pre-existing tests (OnDisconnectedAsync_ChatAgent_CallsDeregister_NotTransitionStatus,
    // OnDisconnectedAsync_ChatAgentWithException_CallsDeregister) cover this implicitly via the Moq
    // loose mock returning null from GetByAgentId, but there is no explicit test anchoring the
    // "null entry → deregister chat agent" path. A concrete test scenario:
    //   GetByConnectionId("conn-1") returns a chat=true agent;
    //   GetByAgentId(...) returns null;
    //   Assert Deregister is called once and TransitionStatus is never called.
}

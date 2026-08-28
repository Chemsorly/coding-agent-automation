using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

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
            GateCommentFormatter: Mock.Of<IGateCommentFormatter>(),
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
        // consistent with the pattern in AgentHubDeregisterReadyTests.cs:79.
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
}

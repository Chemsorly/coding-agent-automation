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
/// Tests for <c>AgentHub.Registration.cs</c> — <c>DeregisterAgent</c> and <c>AgentReady</c>
/// ownership-check paths not covered by <see cref="AgentHubRegistrationBranchTests"/>.
/// </summary>
public sealed class AgentHubDeregisterReadyTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILogger> _logger = new();

    private AgentHub CreateHub(string connectionId)
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
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private static AgentEntry CreateAgent(string agentId, string connectionId) => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "host",
        Labels = [],
        Status = AgentStatus.Idle,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    // ── DeregisterAgent — null agentId.Value throws ───────────────────────

    [Fact]
    public void DeregisterAgent_NullAgentIdValue_Throws()
    {
        var hub = CreateHub("conn-1");
        // ArgumentNullException.ThrowIfNull(agentId.Value) fires synchronously before any Task is created.
        // Use explicit try/catch to avoid xUnit2014 false-positive on Action wrappers over Task-returning methods.
        Exception? caught = null;
        try { hub.DeregisterAgent(default(AgentId)); } catch (Exception ex) { caught = ex; }
        Assert.IsType<ArgumentNullException>(caught);
    }

    // ── DeregisterAgent — caller does not own agent → no-op ──────────────

    [Fact]
    public async Task DeregisterAgent_CallerNotFound_DoesNotDeregister()
    {
        var hub = CreateHub("conn-caller");
        _facade.Setup(f => f.GetByConnectionId("conn-caller")).Returns((AgentEntry?)null);

        await hub.DeregisterAgent(new AgentId("agent-1"));

        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
        // Warning logged with structured args — verify template only via typed overload
        _logger.Verify(l => l.Warning(
            It.Is<string>(s => s.Contains("rejected")),
            It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task DeregisterAgent_CallerOwnsOtherAgent_DoesNotDeregister()
    {
        var hub = CreateHub("conn-caller");
        var agent = CreateAgent("agent-other", "conn-caller");
        _facade.Setup(f => f.GetByConnectionId("conn-caller")).Returns(agent);

        // Caller owns "agent-other" but tries to deregister "agent-1"
        await hub.DeregisterAgent(new AgentId("agent-1"));

        _facade.Verify(f => f.Deregister(It.IsAny<AgentId>()), Times.Never);
    }

    // ── DeregisterAgent — happy path: caller owns agent → deregisters ─────

    [Fact]
    public async Task DeregisterAgent_CallerOwnsAgent_Deregisters()
    {
        var hub = CreateHub("conn-1");
        var agent = CreateAgent("agent-1", "conn-1");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        await hub.DeregisterAgent(new AgentId("agent-1"));

        _facade.Verify(f => f.Deregister(It.Is<AgentId>(a => a.Value == "agent-1")), Times.Once);
    }

    // ── AgentReady — null agentId.Value throws ────────────────────────────

    [Fact]
    public void AgentReady_NullAgentIdValue_Throws()
    {
        var hub = CreateHub("conn-1");
        Exception? caught = null;
        try { hub.AgentReady(default(AgentId)); } catch (Exception ex) { caught = ex; }
        Assert.IsType<ArgumentNullException>(caught);
    }

    // ── AgentReady — caller does not own → no-op ─────────────────────────

    [Fact]
    public async Task AgentReady_CallerNotFound_NoOp()
    {
        var hub = CreateHub("conn-caller");
        _facade.Setup(f => f.GetByConnectionId("conn-caller")).Returns((AgentEntry?)null);

        // Must not throw; logs a warning
        await hub.AgentReady(new AgentId("agent-1"));
        // Verified via typed overload: Warning<string,string>(template, conn, agentId)
        _logger.Verify(l => l.Warning(
            It.Is<string>(s => s.Contains("rejected")),
            It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task AgentReady_CallerOwnsOtherAgent_NoOp()
    {
        var hub = CreateHub("conn-caller");
        var agent = CreateAgent("agent-other", "conn-caller");
        _facade.Setup(f => f.GetByConnectionId("conn-caller")).Returns(agent);

        await hub.AgentReady(new AgentId("agent-1"));

        _logger.Verify(l => l.Warning(
            It.Is<string>(s => s.Contains("rejected")),
            It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    // ── AgentReady — happy path ───────────────────────────────────────────

    [Fact]
    public async Task AgentReady_CallerOwnsAgent_LogsReady()
    {
        var hub = CreateHub("conn-1");
        var agent = CreateAgent("agent-1", "conn-1");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        await hub.AgentReady(new AgentId("agent-1"));

        _logger.Verify(l => l.Information(
            It.Is<string>(s => s.Contains("signaled ready")),
            It.IsAny<string>()),
            Times.Once);
    }
}

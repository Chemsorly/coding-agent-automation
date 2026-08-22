using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;
using System.Security.Claims;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Branch coverage tests for AgentHub.Registration.cs:
/// - RegisterAgent query-param mismatch → HubException
/// - RegisterAgent authenticated-identity mismatch → HubException
/// - RegisterAgent with force-disconnect of old connection (exception swallowed)
/// - AgentHub.cs OnConnectedAsync paths (with agentId, missing non-operator, operator)
/// - AgentHub.cs OnDisconnectedAsync (agent found vs null)
/// </summary>
public sealed class AgentHubRegistrationBranchTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IChatNotifier> _chatNotifier = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<IConsolidationService> _consolidationService = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IAgentJobLifecycleService> _lifecycleService = new();
    private readonly Mock<IAgentTokenRefreshService> _tokenRefreshService = new();
    private readonly Mock<IGateCommentFormatter> _gateCommentFormatter = new();
    private readonly Mock<IAgentOrphanRecoveryService> _orphanRecoveryService = new();
    private readonly ConsolidationBadgeService _badgeService = new();
    private readonly ModelFetchService _modelFetchService;

    public AgentHubRegistrationBranchTests()
    {
        var registry = new AgentRegistryService(Log.Logger);
        var agentComm = new Mock<IAgentCommunication>();
        _modelFetchService = new ModelFetchService(registry, agentComm.Object, Log.Logger);
    }

    private AgentHub CreateHub(HubCallerContext context)
    {
        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ModelFetchService: _modelFetchService,
            ConsolidationService: _consolidationService.Object,
            BadgeService: _badgeService,
            IssueOps: _issueOps.Object,
            LifecycleService: _lifecycleService.Object,
            TokenRefreshService: _tokenRefreshService.Object,
            GateCommentFormatter: _gateCommentFormatter.Object,
            Logger: Log.Logger,
            OrphanRecoveryService: _orphanRecoveryService.Object,
            UiContext: HubTestHelpers.CreateNoOpHubContext()));

        hub.Context = context;
        hub.Groups = new Mock<IGroupManager>().Object;
        return hub;
    }

    private static HubCallerContext BuildContext(
        string connectionId,
        string? agentIdQueryParam,
        ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext();
        if (agentIdQueryParam is not null)
            httpContext.Request.QueryString = new QueryString($"?agentId={agentIdQueryParam}");

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns(connectionId);
        mockCtx.Setup(c => c.Features).Returns(features);
        mockCtx.Setup(c => c.User).Returns(user);
        return mockCtx.Object;
    }

    private static AgentEntry CreateEntry(string agentId, string connectionId, AgentStatus status = AgentStatus.Idle) => new()
    {
        AgentId = agentId,
        ConnectionId = connectionId,
        Hostname = "host",
        Labels = [],
        Status = status,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    // ── RegisterAgent — query param mismatch ─────────────────────────────

    [Fact]
    public async Task RegisterAgent_QueryParamMismatch_ThrowsHubException()
    {
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-X");
        var hub = CreateHub(ctx);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-Y",  // Mismatch: query param is "agent-X"
            Hostname = "host",
            Labels = []
        };

        var act = () => hub.RegisterAgent(message);
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*mismatch*");
    }

    // ── RegisterAgent — authenticated identity mismatch ───────────────────

    [Fact]
    public async Task RegisterAgent_AuthenticatedIdentityMismatch_ThrowsHubException()
    {
        // Query param matches message, but authenticated identity differs
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "other-agent")
        }));
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1", user: claims);
        var hub = CreateHub(ctx);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = []
        };

        var act = () => hub.RegisterAgent(message);
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*mismatch*");
    }

    // ── RegisterAgent — no existing entry, no auth claim ─────────────────

    [Fact]
    public async Task RegisterAgent_NewAgent_NoAuthClaim_RegistersSuccessfully()
    {
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1", user: null);
        var hub = CreateHub(ctx);

        var entry = CreateEntry("agent-1", "conn-1");
        _facade.Setup(f => f.GetByAgentId(It.IsAny<AgentId>())).Returns((AgentEntry?)null);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(entry);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = []
        };

        await hub.RegisterAgent(message);

        _facade.Verify(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1"), Times.Once);
        _orphanRecoveryService.Verify(
            s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()),
            Times.Once);
    }

    // ── RegisterAgent — "agent" identity claim is skipped ────────────────

    [Fact]
    public async Task RegisterAgent_AuthClaimIsGenericAgent_SkipsIdentityCheck()
    {
        // When NameIdentifier == "agent", the defense-in-depth check is bypassed
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "agent")
        }));
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1", user: claims);
        var hub = CreateHub(ctx);

        var entry = CreateEntry("agent-1", "conn-1");
        _facade.Setup(f => f.GetByAgentId(It.IsAny<AgentId>())).Returns((AgentEntry?)null);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(entry);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = []
        };

        // Must NOT throw — "agent" claim is allowed through
        await hub.RegisterAgent(message);

        _facade.Verify(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1"), Times.Once);
    }

    // ── RegisterAgent — force-disconnect old connection (exception swallowed) ──

    [Fact]
    public async Task RegisterAgent_ExistingConnectedAgent_ForceDisconnectFails_StillRegisters()
    {
        var ctx = BuildContext("conn-new", agentIdQueryParam: "agent-1", user: null);

        // Mock old connection's ForceDisconnect call
        var mockOldClientProxy = new Mock<IAgentHubClient>();
        mockOldClientProxy
            .Setup(p => p.ForceDisconnect())
            .ThrowsAsync(new InvalidOperationException("Connection closed"));

        var mockClients = new Mock<IHubCallerClients<IAgentHubClient>>();
        mockClients.Setup(c => c.Client("conn-old")).Returns(mockOldClientProxy.Object);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ModelFetchService: _modelFetchService,
            ConsolidationService: _consolidationService.Object,
            BadgeService: _badgeService,
            IssueOps: _issueOps.Object,
            LifecycleService: _lifecycleService.Object,
            TokenRefreshService: _tokenRefreshService.Object,
            GateCommentFormatter: _gateCommentFormatter.Object,
            Logger: Log.Logger,
            OrphanRecoveryService: _orphanRecoveryService.Object,
            UiContext: HubTestHelpers.CreateNoOpHubContext()));
        hub.Context = ctx;
        hub.Clients = mockClients.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        var existingEntry = CreateEntry("agent-1", "conn-old", AgentStatus.Idle);
        var newEntry = CreateEntry("agent-1", "conn-new");

        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "agent-1"))).Returns(existingEntry);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-new")).Returns(newEntry);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = []
        };

        // Must NOT throw despite ForceDisconnect throwing
        await hub.RegisterAgent(message);

        // Register must still be called
        _facade.Verify(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-new"), Times.Once);
    }

    // ── RegisterAgent — same connection already registered (no force disconnect) ──

    [Fact]
    public async Task RegisterAgent_SameConnectionId_SkipsForceDisconnect()
    {
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1", user: null);
        var hub = CreateHub(ctx);

        // Same connection ID → no force disconnect
        var existingEntry = CreateEntry("agent-1", "conn-1", AgentStatus.Idle);
        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "agent-1"))).Returns(existingEntry);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(existingEntry);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = []
        };

        await hub.RegisterAgent(message);

        _facade.Verify(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1"), Times.Once);
    }

    // ── OnConnectedAsync — agentId present ───────────────────────────────

    [Fact]
    public async Task OnConnectedAsync_AgentIdPresent_LogsAndCallsBase()
    {
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1");
        var hub = CreateHub(ctx);

        // Should not throw; calls base.OnConnectedAsync
        await hub.OnConnectedAsync();
    }

    // ── OnConnectedAsync — no agentId, not operator → aborts ─────────────

    [Fact]
    public async Task OnConnectedAsync_NoAgentId_NotOperator_AbortsConnection()
    {
        var httpContext = new DefaultHttpContext();
        // No agentId query param, no auth_kind claim
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        var aborted = false;
        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns("conn-anon");
        mockCtx.Setup(c => c.Features).Returns(features);
        mockCtx.Setup(c => c.User).Returns(new ClaimsPrincipal());
        mockCtx.Setup(c => c.Abort()).Callback(() => aborted = true);

        var hub = CreateHub(mockCtx.Object);
        await hub.OnConnectedAsync();

        aborted.Should().BeTrue("connection without agentId and no operator claim must be aborted");
    }

    // ── OnConnectedAsync — no agentId, operator claim → allowed ──────────

    [Fact]
    public async Task OnConnectedAsync_NoAgentId_OperatorClaim_AllowsConnection()
    {
        var httpContext = new DefaultHttpContext();
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        var aborted = false;
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("auth_kind", "operator")
        }));

        var mockCtx = new Mock<HubCallerContext>();
        mockCtx.Setup(c => c.ConnectionId).Returns("conn-op");
        mockCtx.Setup(c => c.Features).Returns(features);
        mockCtx.Setup(c => c.User).Returns(claims);
        mockCtx.Setup(c => c.Abort()).Callback(() => aborted = true);

        var hub = CreateHub(mockCtx.Object);
        await hub.OnConnectedAsync();

        aborted.Should().BeFalse("operator connections must not be aborted");
    }

    // ── OnDisconnectedAsync — agent found → transitions to Disconnected ───

    [Fact]
    public async Task OnDisconnectedAsync_AgentFound_TransitionsToDisconnected()
    {
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1");
        var hub = CreateHub(ctx);

        var agent = CreateEntry("agent-1", "conn-1");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        await hub.OnDisconnectedAsync(exception: null);

        _facade.Verify(f => f.TransitionStatus(
            It.Is<AgentId>(a => a.Value == "agent-1"),
            AgentStatus.Disconnected), Times.Once);
    }

    // ── OnDisconnectedAsync — agent not found → no-op ────────────────────

    [Fact]
    public async Task OnDisconnectedAsync_AgentNotFound_DoesNotThrow()
    {
        var ctx = BuildContext("conn-unknown", agentIdQueryParam: null);
        var hub = CreateHub(ctx);

        _facade.Setup(f => f.GetByConnectionId("conn-unknown")).Returns((AgentEntry?)null);

        var act = () => hub.OnDisconnectedAsync(exception: null);
        await act.Should().NotThrowAsync("missing agent entry must be a no-op");

        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── OnDisconnectedAsync — with exception ──────────────────────────────

    [Fact]
    public async Task OnDisconnectedAsync_WithException_LogsExceptionMessage()
    {
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1");
        var hub = CreateHub(ctx);

        var agent = CreateEntry("agent-1", "conn-1");
        _facade.Setup(f => f.GetByConnectionId("conn-1")).Returns(agent);

        // Should not rethrow the passed exception
        await hub.OnDisconnectedAsync(exception: new InvalidOperationException("test error"));

        _facade.Verify(f => f.TransitionStatus(
            It.Is<AgentId>(a => a.Value == "agent-1"),
            AgentStatus.Disconnected), Times.Once);
    }

    // ── Test helpers ──────────────────────────────────────────────────────

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public TestHttpContextFeature(HttpContext httpContext) => HttpContext = httpContext;
        public HttpContext? HttpContext { get; set; }
    }
}

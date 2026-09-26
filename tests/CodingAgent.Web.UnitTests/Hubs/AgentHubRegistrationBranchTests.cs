using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;
using System.Security.Claims;

namespace CodingAgent.Web.UnitTests.Hubs;

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
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IAgentJobLifecycleService> _lifecycleService = new();
    private readonly Mock<IAgentTokenRefreshService> _tokenRefreshService = new();
    private readonly Mock<IGateCommentFormatter> _gateCommentFormatter = new();
    private readonly Mock<IAgentOrphanRecoveryService> _orphanRecoveryService = new();


    private AgentHub CreateHub(HubCallerContext context)
    {
        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
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
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
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
        var act = () => hub.OnConnectedAsync();
        await act.Should().NotThrowAsync("agent connection with valid agentId must be accepted");
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

    // ── RegisterAgent — K8s-mode in-progress label swap on actual pickup ──────

    private static ActiveJobState MakeActiveJob(string runId) => new()
    {
        RunId = runId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1",
        AgentProviderConfigId = "agent-cfg-1",
        InitiatedBy = "loop",
        CurrentStep = PipelineStep.ExploringCodebase,
        StartedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task RegisterAgent_WithActiveJob_FirstPickup_SwapsLabelToInProgress()
    {
        // The desired behavior: while queued (Pending) the issue stays agent:next. Only when an
        // agent actually picks up the dispatched run — which in K8s mode is signalled by the agent
        // registering with an ActiveJob — does the issue move to agent:in-progress.
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1", user: null);
        var hub = CreateHub(ctx);

        var runId = Guid.NewGuid().ToString();
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-cfg-1",
            RepoProviderConfigId = "repo-cfg-1"
            // AgentId empty → this is the first pickup
        };

        _facade.Setup(f => f.GetByAgentId(It.IsAny<AgentId>())).Returns((AgentEntry?)null);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(CreateEntry("agent-1", "conn-1"));
        _facade.Setup(f => f.GetRun(runId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = [],
            ActiveJob = MakeActiveJob(runId)
        };

        await hub.RegisterAgent(message);

        _issueOps.Verify(o => o.SwapLabelAsync(run, AgentLabels.InProgress), Times.Once,
            "when the agent actually picks up a dispatched run, the issue must move agent:next → agent:in-progress");
    }

    [Fact]
    public async Task RegisterAgent_WithoutActiveJob_DoesNotSwapLabel()
    {
        // An agent registering without an active job (idle worker joining the pool) must not
        // touch any issue label.
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1", user: null);
        var hub = CreateHub(ctx);

        _facade.Setup(f => f.GetByAgentId(It.IsAny<AgentId>())).Returns((AgentEntry?)null);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(CreateEntry("agent-1", "conn-1"));
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = []
            // no ActiveJob
        };

        await hub.RegisterAgent(message);

        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never,
            "an agent registering without an active job must not swap any issue label");
    }

    [Fact]
    public async Task RegisterAgent_ActiveJobRunAlreadyHasAgentId_DoesNotReSwapLabel()
    {
        // Same-agent reconnect: the registering agent is the same agent already assigned to the run.
        // run.AgentId == message.AgentId → the guard (run.AgentId != message.AgentId.Value) is false
        // → the entire block is skipped (no ReplaceRun, no label swap). This is the idempotent case.
        // The different-agent (pod replacement) case is covered by RegisterAgent_PodReplacement_DifferentAgentId_UpdatesRunAgentId.
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1", user: null);
        var hub = CreateHub(ctx);

        var runId = Guid.NewGuid().ToString();
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-cfg-1",
            RepoProviderConfigId = "repo-cfg-1",
            AgentId = "agent-1" // already assigned → not a first pickup
        };

        _facade.Setup(f => f.GetByAgentId(It.IsAny<AgentId>())).Returns((AgentEntry?)null);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(CreateEntry("agent-1", "conn-1"));
        _facade.Setup(f => f.GetRun(runId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = [],
            ActiveJob = MakeActiveJob(runId)
        };

        await hub.RegisterAgent(message);

        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never,
            "a reconnect where the run already has an assigned agent must not re-swap the label");
    }

    // ── RegisterAgent — pod replacement (different AgentId reconnects) ────────

    [Fact]
    public async Task RegisterAgent_PodReplacement_DifferentAgentId_UpdatesRunAgentId()
    {
        // Pod replacement: a new agent pod registers with the same RunId but a different AgentId.
        // run.AgentId must be updated to the new agent's identity.
        // The label must NOT be swapped — it was already moved to agent:in-progress on first pickup.
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-new", user: null);
        var hub = CreateHub(ctx);

        var runId = Guid.NewGuid().ToString();
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-cfg-1",
            RepoProviderConfigId = "repo-cfg-1",
            AgentId = "agent-old" // prior pod's identity — different from registering agent
        };

        _facade.Setup(f => f.GetByAgentId(It.IsAny<AgentId>())).Returns((AgentEntry?)null);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(CreateEntry("agent-new", "conn-1"));
        _facade.Setup(f => f.GetRun(runId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(It.IsAny<PipelineRun>()));
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-new",
            Hostname = "host",
            Labels = [],
            ActiveJob = MakeActiveJob(runId)
        };

        await hub.RegisterAgent(message);

        run.AgentId.Should().Be("agent-new", "pod replacement must update run.AgentId to the new agent");
        // TODO: [WARNING] _facade.Verify(f => f.ReplaceRun(run), Times.Once) does not add confidence
        // beyond the AgentId assertion above — `run` is a mutable reference already mutated to "agent-new"
        // before Verify executes, so it doesn't distinguish "ReplaceRun called with updated run" from
        // "ReplaceRun called with original run". The AgentId assertion is the meaningful check here.
        _facade.Verify(f => f.ReplaceRun(run), Times.Once,
            "run must be persisted after AgentId update");
        // TODO: [WARNING] The acceptance criterion "A log entry is emitted when AgentId is updated due to
        // pod replacement" is not verified here. CreateHub wires Logger: Log.Logger (Serilog static logger,
        // not a mock), so _logger.Information(... "pod replacement") cannot be asserted via mock expectations
        // from this test class. To cover the log AC for the RegisterAgent path, the hub would need to be
        // constructed with a Mock<ILogger> (as AgentHubBehaviorTests already does).
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never,
            "label swap must NOT fire on pod replacement — only first pickup swaps the label");
    }

    [Fact]
    public async Task RegisterAgent_SameAgentReconnect_RunAgentId_IsNoOp()
    {
        // Same-agent reconnect: the entire AgentId block must be skipped (no ReplaceRun, no label swap).
        // run.AgentId must remain unchanged.
        // TODO: [WARNING] This test is structurally identical to RegisterAgent_ActiveJobRunAlreadyHasAgentId_DoesNotReSwapLabel
        // above (same setup: run.AgentId = "agent-1", register with "agent-1", verify ReplaceRun/SwapLabel never called).
        // The added `run.AgentId.Should().Be("agent-1")` assertion is trivially satisfied because no mutation
        // occurs when the block is skipped — it confirms the reference was not changed, not that the guard
        // logic was evaluated correctly. The two tests have no meaningful differentiation; consider whether
        // one of them can be removed or replaced with a guard-logic-focused assertion.
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1", user: null);
        var hub = CreateHub(ctx);

        var runId = Guid.NewGuid().ToString();
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-cfg-1",
            RepoProviderConfigId = "repo-cfg-1",
            AgentId = "agent-1" // same as registering agent
        };

        _facade.Setup(f => f.GetByAgentId(It.IsAny<AgentId>())).Returns((AgentEntry?)null);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(CreateEntry("agent-1", "conn-1"));
        _facade.Setup(f => f.GetRun(runId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = [],
            ActiveJob = MakeActiveJob(runId)
        };

        await hub.RegisterAgent(message);

        run.AgentId.Should().Be("agent-1", "same-agent reconnect must leave run.AgentId unchanged");
        _facade.Verify(f => f.ReplaceRun(It.IsAny<PipelineRun>()), Times.Never,
            "same-agent reconnect must not trigger a ReplaceRun write");
        _issueOps.Verify(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()), Times.Never,
            "same-agent reconnect must not re-swap the label");
    }

    [Fact]
    public async Task RegisterAgent_WithActiveJob_LabelSwapThrows_DoesNotBreakRegistration()
    {
        // The in-progress swap is best-effort: a provider failure must NOT break registration or
        // force-disconnect the agent (the agent already has the job and must keep working).
        var ctx = BuildContext("conn-1", agentIdQueryParam: "agent-1", user: null);
        var hub = CreateHub(ctx);

        var runId = Guid.NewGuid().ToString();
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-cfg-1",
            RepoProviderConfigId = "repo-cfg-1"
        };

        _facade.Setup(f => f.GetByAgentId(It.IsAny<AgentId>())).Returns((AgentEntry?)null);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1")).Returns(CreateEntry("agent-1", "conn-1"));
        _facade.Setup(f => f.GetRun(runId)).Returns(run);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("issue provider unreachable"));
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = [],
            ActiveJob = MakeActiveJob(runId)
        };

        var ex = await Record.ExceptionAsync(() => hub.RegisterAgent(message));

        ex.Should().BeNull("a label-swap failure must be swallowed — registration must still complete");
        _facade.Verify(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-1"), Times.Once);
        _orphanRecoveryService.Verify(
            s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()),
            Times.Once,
            "registration must run to completion (orphan recovery) despite the label-swap failure");
    }

    // ── RegisterAgent — ForceDisconnect guard (mid-run reconnect vs pod replacement) ──

    [Fact]
    public async Task RegisterAgent_MidRunReconnect_NoActiveJob_DoesNotForceDisconnect()
    {
        // When the same agent reconnects without an ActiveJob while an active job is in flight
        // (existingEntry.ActiveJobId is non-null), ForceDisconnect must NOT be sent. The existing
        // connection is still being used by the running pipeline's OrchestratorProxy; killing it
        // severs all subsequent hub calls (RequestGetIssue, etc.) silently.
        var ctx = BuildContext("conn-new", agentIdQueryParam: "agent-1", user: null);

        var mockOldClientProxy = new Mock<IAgentHubClient>();
        mockOldClientProxy.Setup(p => p.ForceDisconnect()).Returns(Task.CompletedTask);

        var mockClients = new Mock<IHubCallerClients<IAgentHubClient>>();
        mockClients.Setup(c => c.Client("conn-old")).Returns(mockOldClientProxy.Object);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
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

        // Existing entry has an active job in flight
        var existingEntry = CreateEntry("agent-1", "conn-old", AgentStatus.Busy) with { Hostname = "pod-running" };
        existingEntry.ActiveJobId = "job-123";
        var newEntry = CreateEntry("agent-1", "conn-new");

        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "agent-1"))).Returns(existingEntry);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-new", true)).Returns(newEntry);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            // Same hostname as existingEntry ("pod-running") is intentional and required: the hostname
            // equality check is the discriminating signal that this is a kiro-cli sub-process restart
            // on the same pod, not a pod replacement. If the hostname check were removed from the
            // production guard the test would still pass on the two-condition form — using an explicit
            // distinct hostname (not CreateEntry's hard-coded "host") ensures the intent is clear and
            // the value can be updated if the hostname check is ever changed.
            Hostname = "pod-running",
            Labels = []
            // no ActiveJob — mid-run kiro-cli sub-process restart
        };

        await hub.RegisterAgent(message);

        // ForceDisconnect must NOT be called — the pipeline connection must be preserved
        mockOldClientProxy.Verify(p => p.ForceDisconnect(), Times.Never,
            "mid-run reconnect without ActiveJob must not ForceDisconnect the existing pipeline connection");
        // Registration must still complete with preserveExistingConnectionId=true so conn-old stays
        // in _connectionIndex for AgentAuthorizationFilter lookups during the in-flight job.
        _facade.Verify(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-new", true), Times.Once,
            "registration must complete with preserveExistingConnectionId=true to preserve the pipeline connection");
    }

    [Fact]
    public async Task RegisterAgent_PodReplacement_WithActiveJob_StillForceDisconnects()
    {
        // When an agent reconnects WITH an ActiveJob (pod replacement scenario), ForceDisconnect
        // must still fire on the old connection — the new pod is taking over the run.
        var ctx = BuildContext("conn-new", agentIdQueryParam: "agent-1", user: null);

        var mockOldClientProxy = new Mock<IAgentHubClient>();
        mockOldClientProxy.Setup(p => p.ForceDisconnect()).Returns(Task.CompletedTask);

        var mockClients = new Mock<IHubCallerClients<IAgentHubClient>>();
        mockClients.Setup(c => c.Client("conn-old")).Returns(mockOldClientProxy.Object);

        var runId = Guid.NewGuid().ToString();
        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
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

        var existingEntry = CreateEntry("agent-1", "conn-old", AgentStatus.Busy);
        existingEntry.ActiveJobId = runId;
        var newEntry = CreateEntry("agent-1", "conn-new");

        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "agent-1"))).Returns(existingEntry);
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-new")).Returns(newEntry);
        // TODO: [WARNING] This GetRun setup is never invoked by the RegisterAgent ForceDisconnect
        // path under test — RegisterAgent does not call GetRun during the disconnect guard. Its
        // presence is misleading and suggests the test author believed GetRun is part of the
        // ForceDisconnect flow. Remove this setup to avoid false impressions about the control flow.
        // (It is invoked later in the run if message.ActiveJob.RunId is non-null, but verifying
        // that path is not the intent of this test.)
        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            Labels = [],
            ActiveJob = MakeActiveJob(runId)  // non-null → pod replacement
        };

        await hub.RegisterAgent(message);

        // ForceDisconnect MUST be called — pod replacement must evict the old connection
        mockOldClientProxy.Verify(p => p.ForceDisconnect(), Times.Once,
            "pod replacement (ActiveJob non-null) must still send ForceDisconnect to the old connection");
    }

    [Fact]
    public async Task RegisterAgent_ExistingEntryHasNoActiveJob_StillForceDisconnects()
    {
        // When existingEntry.ActiveJobId is null (agent is idle), the guard does not apply
        // regardless of message.ActiveJob — ForceDisconnect fires as before.
        // This covers the idle-to-idle reconnect case (e.g. double registration from same worker).
        var ctx = BuildContext("conn-new", agentIdQueryParam: "agent-1", user: null);

        var mockOldClientProxy = new Mock<IAgentHubClient>();
        mockOldClientProxy.Setup(p => p.ForceDisconnect()).Returns(Task.CompletedTask);

        var mockClients = new Mock<IHubCallerClients<IAgentHubClient>>();
        mockClients.Setup(c => c.Client("conn-old")).Returns(mockOldClientProxy.Object);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
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

        // Existing entry has NO active job
        var existingEntry = CreateEntry("agent-1", "conn-old", AgentStatus.Idle);
        // existingEntry.ActiveJobId is null by default
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
            // no ActiveJob — but existingEntry.ActiveJobId is also null, so guard doesn't apply
        };

        await hub.RegisterAgent(message);

        // ForceDisconnect MUST be called — guard only skips when existingEntry.ActiveJobId is non-null
        mockOldClientProxy.Verify(p => p.ForceDisconnect(), Times.Once,
            "when existingEntry has no active job, ForceDisconnect must fire regardless of message.ActiveJob");
    }

    [Fact]
    public async Task RegisterAgent_PodReplacement_DifferentHostname_NoActiveJob_ForceDisconnects()
    {
        // AC #1: a genuine pod replacement registers with a DIFFERENT hostname and no ActiveJob
        // (the new pod has not yet been assigned a work item). The guard must NOT preserve the old
        // connection — a different hostname unambiguously identifies a different pod.
        var ctx = BuildContext("conn-new", agentIdQueryParam: "agent-1", user: null);

        var mockOldClientProxy = new Mock<IAgentHubClient>();
        mockOldClientProxy.Setup(p => p.ForceDisconnect()).Returns(Task.CompletedTask);

        var mockClients = new Mock<IHubCallerClients<IAgentHubClient>>();
        mockClients.Setup(c => c.Client("conn-old")).Returns(mockOldClientProxy.Object);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
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

        // Existing entry belongs to the OLD pod and has an active job in flight
        var existingEntry = new AgentEntry
        {
            AgentId = "agent-1",
            ConnectionId = "conn-old",
            Hostname = "pod-old",   // <-- old pod hostname
            Labels = [],
            Status = AgentStatus.Busy,
            RegisteredAt = DateTimeOffset.UtcNow,
            ActiveJobId = "job-456"
        };
        var newEntry = CreateEntry("agent-1", "conn-new");

        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "agent-1"))).Returns(existingEntry);
        // preserveExistingConnectionId must be false (default) — different hostname → NOT kiro-cli reconnect
        // TODO: [WARNING] This setup matches only when preserveExistingConnectionId=false. If production
        // code passes true instead, the mock returns null (no matching setup) causing a NullReferenceException
        // before the ForceDisconnect Verify assertion is reached. The test then fails with an exception
        // rather than a clear assertion failure, making it harder to diagnose which behavioral invariant
        // was violated. The ForceDisconnect assertion is the primary guard; consider also setting up
        // Register with It.IsAny<bool>() so a wrong flag produces a clean Verify failure rather than
        // an opaque exception.
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-new", false)).Returns(newEntry);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "pod-new",   // <-- different hostname: new pod, not a kiro-cli sub-process restart
            Labels = []
            // no ActiveJob — new pod has not yet been assigned a work item
        };

        await hub.RegisterAgent(message);

        // ForceDisconnect MUST fire — different hostname means a different pod regardless of ActiveJob
        mockOldClientProxy.Verify(p => p.ForceDisconnect(), Times.Once,
            "a pod replacement with a different hostname must ForceDisconnect the old connection even when ActiveJob is null");
        // Registration must proceed with preserveExistingConnectionId=false so the stale connection is evicted
        _facade.Verify(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-new", false), Times.Once,
            "registration must use preserveExistingConnectionId=false for a different-hostname pod replacement");
    }

    [Fact]
    public async Task RegisterAgent_MidRunReconnect_SameHostname_NoActiveJob_DoesNotForceDisconnect()
    {
        // AC #2 (primary regression guard): same pod reconnects without ActiveJob while the registry
        // entry still holds an active job. This is the kiro-cli sub-process mid-run restart pattern —
        // the hostname explicitly confirms "same pod". ForceDisconnect must NOT be sent.
        // Uses distinct hostname values (not CreateEntry's hardcoded "host") to make the intent explicit.
        var ctx = BuildContext("conn-new", agentIdQueryParam: "agent-1", user: null);

        var mockOldClientProxy = new Mock<IAgentHubClient>();
        mockOldClientProxy.Setup(p => p.ForceDisconnect()).Returns(Task.CompletedTask);

        var mockClients = new Mock<IHubCallerClients<IAgentHubClient>>();
        mockClients.Setup(c => c.Client("conn-old")).Returns(mockOldClientProxy.Object);

        var hub = new AgentHub(new AgentHubDependencies(
            Facade: _facade.Object,
            ChatNotifier: _chatNotifier.Object,
            ChangeNotifier: _changeNotifier.Object,
            ConsolidationOps: Mock.Of<IHubConsolidationOperations>(),
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

        // Existing entry: same pod (same hostname), job in flight, kiro-cli sub-process dropped and is reconnecting
        var existingEntry = new AgentEntry
        {
            AgentId = "agent-1",
            ConnectionId = "conn-old",
            Hostname = "pod-xyz",   // <-- same pod hostname
            Labels = [],
            Status = AgentStatus.Busy,
            RegisteredAt = DateTimeOffset.UtcNow,
            ActiveJobId = "job-789"
        };
        var newEntry = CreateEntry("agent-1", "conn-new");

        _facade.Setup(f => f.GetByAgentId(It.Is<AgentId>(a => a.Value == "agent-1"))).Returns(existingEntry);
        // preserveExistingConnectionId must be true — same hostname confirms kiro-cli sub-process reconnect
        _facade.Setup(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-new", true)).Returns(newEntry);
        _orphanRecoveryService
            .Setup(s => s.RecoverOrphanedStateAsync(It.IsAny<AgentRegistrationMessage>(), It.IsAny<AgentId>()))
            .Returns(Task.CompletedTask);

        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "pod-xyz",   // <-- same hostname as existingEntry: same pod = kiro-cli sub-process restart
            Labels = []
            // no ActiveJob — mid-run sub-process restart, job is still running on the old connection
        };

        await hub.RegisterAgent(message);

        // ForceDisconnect must NOT be sent — same pod, same job in flight, old connection still needed
        mockOldClientProxy.Verify(p => p.ForceDisconnect(), Times.Never,
            "a kiro-cli sub-process reconnect from the same pod must not ForceDisconnect the existing pipeline connection");
        // Registration must complete with preserveExistingConnectionId=true to keep conn-old in _connectionIndex
        _facade.Verify(f => f.Register(It.IsAny<AgentRegistrationMessage>(), "conn-new", true), Times.Once,
            "registration must use preserveExistingConnectionId=true to preserve the in-flight pipeline connection");
    }

    // ── Test helpers ──────────────────────────────────────────────────────

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public TestHttpContextFeature(HttpContext httpContext) => HttpContext = httpContext;
        public HttpContext? HttpContext { get; set; }
    }
}

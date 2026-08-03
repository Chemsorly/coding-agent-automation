using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry;

/// <summary>
/// Unit tests for <see cref="HeartbeatMonitorService"/> chat-agent exemption.
/// Validates Req 7: agents carrying <c>"chat=true"</c> in their labels are skipped
/// entirely in <see cref="HeartbeatMonitorService.SweepAsync"/> — they are never swept
/// to Disconnected on stale heartbeat and never swept to Idle on long-busy.
///
/// These tests are written BEFORE the guard is added to SweepAsync (TDD red state).
/// Tests 1–3 and 5 will FAIL until task 7.2 inserts the guard.
/// </summary>
public class HeartbeatMonitorChatAgentTests : IDisposable
{
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<ILabelService> _mockLabelService;
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager;
    private readonly Mock<ILogger> _mockLogger;
    private readonly HeartbeatMonitorService _monitor;

    public HeartbeatMonitorChatAgentTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockLabelService = new Mock<ILabelService>();
        _mockLifecycleManager = new Mock<IRunLifecycleManager>();

        // Default config: short heartbeat timeout (10s) so stale agents sweep quickly in tests.
        // AgentBusyProgressTimeout is short (1 min) so busy-without-progress sweeps fire.
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                HeartbeatTimeoutSeconds = 10,
                AgentDisconnectGracePeriod = TimeSpan.Zero,
                AgentBusyProgressTimeout = TimeSpan.FromMinutes(1)
            });

        // Default FailRunAsync simulates ClearAgentState side effects.
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()))
            .Returns((RunId runId, string reason, CancellationToken _, FailureReason? __) =>
            {
                var agents = _registry.GetAllAgents();
                foreach (var agent in agents)
                {
                    if (agent.ActiveJobId == runId.Value)
                    {
                        lock (agent.SyncRoot)
                        {
                            agent.ActiveJobId = null;
                            agent.OrphanRestoredAt = null;
                        }
                        _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
                        break;
                    }
                }

                return Task.FromResult<PipelineRun?>(new PipelineRun
                {
                    RunId = runId.Value,
                    IssueIdentifier = "test/repo#0",
                    IssueTitle = "Test",
                    IssueProviderConfigId = "ip-default",
                    RepoProviderConfigId = "rp-default",
                    FailureReason = reason,
                    CurrentStep = PipelineStep.Failed
                });
            });

        _monitor = new HeartbeatMonitorService(
            new HeartbeatMonitorDependencies(
                _registry,
                _runService,
                _mockHistoryService.Object,
                _mockConfigStore.Object,
                _mockLogger.Object,
                LifecycleManager: _mockLifecycleManager.Object));
    }

    // ── Test 1: "chat=true" + stale heartbeat → NOT swept to Disconnected ────────

    /// <summary>
    /// A chat agent whose heartbeat is stale (>HeartbeatTimeoutSeconds) must NOT be
    /// swept to Disconnected. Chat pods have long idle periods by design and do not
    /// send periodic heartbeats the same way pipeline agents do.
    ///
    /// RED until the guard <c>if (agent.Labels?.Any(...) == true) continue;</c> is added.
    /// </summary>
    [Fact]
    public async Task SweepAsync_ChatAgent_StaleHeartbeat_NotSweptToDisconnected()
    {
        // Arrange: chat agent with stale heartbeat (> HeartbeatTimeoutSeconds = 10s)
        var entry = RegisterChatAgent("chat-agent-1", "conn-chat-1", ["chat=true", "kiro=true"]);
        entry.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-60); // 60s stale, timeout is 10s

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: chat agent MUST remain Idle (not swept to Disconnected)
        var agent = _registry.GetByAgentId("chat-agent-1");
        agent.Should().NotBeNull("chat agent must still be in registry");
        agent!.Status.Should().Be(AgentStatus.Idle,
            "chat agents with stale heartbeats must NOT be swept to Disconnected — they are exempt from heartbeat sweeping");
    }

    // ── Test 2: "chat=true" + long busy → NOT swept to Idle ──────────────────────

    /// <summary>
    /// A chat agent stuck in Busy far beyond the progress timeout must NOT be swept
    /// back to Idle. Chat sessions can run for hours without job-slot "progress" events.
    ///
    /// RED until the guard is added.
    /// </summary>
    [Fact]
    public async Task SweepAsync_ChatAgent_LongBusy_NotSweptToIdle()
    {
        // Arrange: chat agent Busy with no run (as in a real chat session — no PipelineRun row)
        var entry = RegisterChatAgent("chat-agent-2", "conn-chat-2", ["chat=true", "kiro=true"]);
        entry.ActiveJobId = "chat-job-1";
        _registry.TransitionStatus("chat-agent-2", AgentStatus.Busy);

        // BusySince is old enough to exceed BusySince grace period (30s) and progress timeout (1min)
        lock (entry.SyncRoot)
        {
            entry.BusySince = DateTimeOffset.UtcNow.AddMinutes(-120); // 2 hours
        }

        // Fresh heartbeat so stale-heartbeat sweep (Phase 1) doesn't fire first
        entry.LastHeartbeatAt = DateTimeOffset.UtcNow;

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: chat agent MUST stay Busy (not reset to Idle by BusySince grace period logic)
        var agent = _registry.GetByAgentId("chat-agent-2");
        agent.Should().NotBeNull("chat agent must still be in registry");
        agent!.Status.Should().Be(AgentStatus.Busy,
            "chat agents with long-running busy periods must NOT be swept to Idle — sessions can last hours");
    }

    // ── Test 3: "Chat=true" (uppercase) → also exempted ─────────────────────────

    /// <summary>
    /// The guard uses OrdinalIgnoreCase, so "Chat=true" (mixed case) must also exempt
    /// the agent from sweeping.
    ///
    /// RED until the guard is added (with StringComparison.OrdinalIgnoreCase).
    /// </summary>
    [Fact]
    public async Task SweepAsync_ChatAgent_UppercaseChatLabel_AlsoExempted()
    {
        // Arrange: label is "Chat=true" (uppercase C) — should still be exempted
        var entry = RegisterChatAgent("chat-agent-3", "conn-chat-3", ["Chat=true", "kiro=true"]);
        entry.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-60); // stale heartbeat

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: uppercase "Chat=true" must also be treated as the chat exemption label
        var agent = _registry.GetByAgentId("chat-agent-3");
        agent.Should().NotBeNull("chat agent with uppercase label must still be in registry");
        agent!.Status.Should().Be(AgentStatus.Idle,
            "\"Chat=true\" (uppercase) must be treated identically to \"chat=true\" — guard is case-insensitive");
    }

    // ── Test 4: Labels = [] (empty) + stale → swept normally (guard evaluates false) ─

    /// <summary>
    /// An agent whose <c>Labels</c> collection is empty (no "chat=true" entry) and has a
    /// stale heartbeat must be swept normally to Disconnected.
    ///
    /// Note: <c>AgentEntry.Labels</c> is <c>required IReadOnlyList&lt;string&gt;</c> (non-nullable),
    /// so null is not reachable through normal registration. The guard expression
    /// <c>agent.Labels?.Any(...) == true</c> uses <c>?.</c> defensively; for empty labels
    /// <c>.Any()</c> returns false and the guard does not fire.
    ///
    /// GREEN even before 7.2 (empty labels list evaluates false by default).
    /// Documents the guard-evaluates-false contract for non-chat agents.
    /// </summary>
    [Fact]
    public async Task SweepAsync_EmptyLabels_StaleHeartbeat_SweptNormally()
    {
        // Arrange: agent with empty Labels list and stale heartbeat
        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-empty-labels",
            Hostname = "host-empty",
            Labels = [],    // empty — no "chat=true" entry
            ActiveJob = null
        };
        var entry = _registry.Register(message, connectionId: "conn-empty");
        entry.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-60); // stale

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: empty Labels → .Any(l => ...) returns false → guard does not fire → swept normally
        var agent = _registry.GetByAgentId("agent-empty-labels");
        agent.Should().NotBeNull("agent should still exist (just Disconnected, not deregistered)");
        agent!.Status.Should().Be(AgentStatus.Disconnected,
            "agent with empty Labels must not be exempted — guard evaluates false when no label matches");
    }

    // ── Test 5: no "chat=true" → swept normally (regression guard) ───────────────

    /// <summary>
    /// An agent WITHOUT the "chat=true" label must continue to be swept normally.
    /// This is the regression guard: the guard must not accidentally exempt non-chat agents.
    ///
    /// GREEN even before 7.2 (guard absence = all agents swept normally).
    /// After 7.2, this test ensures the guard only fires for chat-labeled agents.
    /// </summary>
    [Fact]
    public async Task SweepAsync_NormalAgent_NoChatLabel_StaleHeartbeat_SweptNormally()
    {
        // Arrange: regular pipeline agent with no "chat=true" label
        var entry = RegisterChatAgent("normal-agent-1", "conn-normal-1", ["kiro=true", "dotnet=true"]);
        entry.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-60); // stale

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: no "chat=true" → swept to Disconnected as normal
        var agent = _registry.GetByAgentId("normal-agent-1");
        agent.Should().NotBeNull("agent should still exist (just Disconnected, not deregistered yet)");
        agent!.Status.Should().Be(AgentStatus.Disconnected,
            "regular agents without \"chat=true\" must still be swept normally — regression guard");
    }

    // ── Test 6: "chat=true" + Status = Disconnected → document intended outcome ──

    /// <summary>
    /// Documents the intended behavior when a chat agent is already Disconnected.
    /// The guard fires inside <c>if (agent.Status != AgentStatus.Disconnected)</c>, so
    /// a chat agent that is already Disconnected will NOT be protected by the guard —
    /// it proceeds to Phase 2 (SweepDisconnectedAgents) normally.
    ///
    /// This is intentional: a Disconnected chat agent has already lost its SignalR
    /// connection and must be cleaned up. The exemption is for Idle/Busy states only.
    ///
    /// GREEN (the guard is inside the non-Disconnected branch).
    /// This test documents and locks in that intended outcome.
    /// </summary>
    [Fact]
    public async Task SweepAsync_ChatAgent_AlreadyDisconnected_SweptNormally()
    {
        // Arrange: chat agent that was explicitly set to Disconnected
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                HeartbeatTimeoutSeconds = 10,
                AgentDisconnectGracePeriod = TimeSpan.Zero, // immediate
                AgentBusyProgressTimeout = TimeSpan.FromMinutes(1)
            });

        var entry = RegisterChatAgent("chat-disconnected", "conn-dc", ["chat=true", "kiro=true"]);
        _registry.TransitionStatus("chat-disconnected", AgentStatus.Disconnected);
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10); // well past zero grace period

        // Act
        await _monitor.SweepAsync(CancellationToken.None);

        // Assert: a Disconnected chat agent is deregistered as normal.
        // The guard only applies inside the `if (agent.Status != Disconnected)` branch.
        // Once a chat pod has lost its connection, it should be cleaned up.
        _registry.GetByAgentId("chat-disconnected").Should()
            .BeNull("a chat agent already in Disconnected state must be deregistered past the grace period — the guard only protects Idle/Busy chat agents");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private AgentEntry RegisterChatAgent(string agentId, string connectionId, IReadOnlyList<string> labels)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = labels,
            ActiveJob = null
        }, connectionId);
    }

    public void Dispose()
    {
        _monitor.Dispose();
    }
}

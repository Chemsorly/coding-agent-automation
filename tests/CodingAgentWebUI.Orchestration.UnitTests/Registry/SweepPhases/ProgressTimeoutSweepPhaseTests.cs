using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Registry.SweepPhases;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry.SweepPhases;

/// <summary>
/// Unit tests for <see cref="ProgressTimeoutSweepPhase"/> in isolation.
/// </summary>
public class ProgressTimeoutSweepPhaseTests : IDisposable
{
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IRunLifecycleManager> _mockLifecycleManager;
    private readonly Mock<IConsolidationService> _mockConsolidationService;
    private readonly Mock<ILogger> _mockLogger;
    private readonly ProgressTimeoutSweepPhase _phase;

    public ProgressTimeoutSweepPhaseTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _mockLifecycleManager = new Mock<IRunLifecycleManager>();
        _mockConsolidationService = new Mock<IConsolidationService>();

        // Default: FailRunAsync succeeds and simulates ClearAgentState side effects.
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()))
            .Returns((RunId runId, string reason, CancellationToken _, FailureReason? __) =>
            {
                foreach (var a in _registry.GetAllAgents())
                {
                    if (a.ActiveJobId == runId.Value)
                    {
                        lock (a.SyncRoot)
                        {
                            a.ActiveJobId = null;
                            a.OrphanRestoredAt = null;
                        }
                        _registry.TransitionStatus(a.AgentId, AgentStatus.Idle);
                        break;
                    }
                }
                return Task.FromResult<PipelineRun?>(new PipelineRun
                {
                    RunId = runId.Value,
                    IssueIdentifier = "test/repo#0",
                    IssueTitle = "Test",
                    IssueProviderConfigId = "ip-1",
                    RepoProviderConfigId = "rp-1",
                    FailureReason = reason,
                });
            });

        // Default: consolidation service says no active runs
        _mockConsolidationService.Setup(c => c.IsRunActive(It.IsAny<RunId>())).Returns(false);

        _phase = new ProgressTimeoutSweepPhase(
            _registry,
            _runService,
            _mockLifecycleManager.Object,
            _mockConsolidationService.Object,
            _mockLogger.Object);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private AgentEntry RegisterBusyAgent(string agentId, string jobId = "job-1")
    {
        var entry = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId, Hostname = $"host-{agentId}", Labels = [], ActiveJob = null
        }, "conn-1");
        entry.ActiveJobId = jobId;
        _registry.TransitionStatus(agentId, AgentStatus.Busy);
        return entry;
    }

    private static PipelineConfiguration MakeConfig(TimeSpan progressTimeout)
        => new() { AgentBusyProgressTimeout = progressTimeout };

    // ── Tests: status guard ───────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_AgentNotBusy_ReturnsFalse_NoAction()
    {
        var entry = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "host-1", Labels = [], ActiveJob = null
        }, "conn-1");
        // Status = Idle

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        result.Should().BeFalse();
        _mockLifecycleManager.Verify(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Never);
    }

    [Fact]
    public async Task Execute_BusyAgentWithNullActiveJobId_ReturnsFalse_NoAction()
    {
        // Guard: phase must return false immediately when ActiveJobId is null.
        var entry = RegisterBusyAgent("agent-1");
        entry.ActiveJobId = null; // override after registration

        var result = await _phase.ExecuteAsync(entry, DateTimeOffset.UtcNow, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        result.Should().BeFalse("phase must guard on ActiveJobId != null");
        _mockLifecycleManager.Verify(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Never);
    }

    // ── Tests: run found — progress timeout ───────────────────────────────────────

    [Fact]
    public async Task Execute_RunFound_LastStepChangeAtWithinTimeout_NoAction()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "job-1");
        _runService.AddRun(new PipelineRun
        {
            RunId = "job-1", IssueIdentifier = "org/repo#1", IssueTitle = "Test",
            IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            LastStepChangeAt = now.AddMinutes(-10) // within 30min timeout
        });

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        _mockLifecycleManager.Verify(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Never);
    }

    [Fact]
    public async Task Execute_RunFound_LastStepChangeAtPastTimeout_FailsRun()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "job-1");
        _runService.AddRun(new PipelineRun
        {
            RunId = "job-1", IssueIdentifier = "org/repo#1", IssueTitle = "Test",
            IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            LastStepChangeAt = now.AddMinutes(-45) // exceeds 30min timeout
        });

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(
                "job-1",
                It.Is<string>(s => s.Contains("progress timeout")),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()),
            Times.Once);
        // TODO: [WARNING] Add: var result = await _phase.ExecuteAsync(...); result.Should().BeFalse().
        // ProgressTimeoutSweepPhase intentionally always returns false so subsequent phases still run.
        // If this return value were accidentally changed to true, it would silently stop downstream phases
        // (same "consumed" semantics as StaleHeartbeatSweepPhase). No test currently asserts the return
        // value on the action-taken paths (run-found-past-timeout and BusySince-past-grace).
    }

    [Fact]
    public async Task Execute_RunFound_DefaultLastStepChangeAt_StartedAtOffsetWithinTimeout_NoAction()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "job-1");
        _runService.AddRun(new PipelineRun
        {
            RunId = "job-1", IssueIdentifier = "org/repo#1", IssueTitle = "Test",
            IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            // LastStepChangeAt = default, StartedAtOffset within timeout
            StartedAtOffset = now.AddMinutes(-10)
        });

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        _mockLifecycleManager.Verify(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Never);
    }

    [Fact]
    public async Task Execute_RunFound_DefaultLastStepChangeAt_StartedAtOffsetPastTimeout_FailsRun()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "job-1");
        _runService.AddRun(new PipelineRun
        {
            RunId = "job-1", IssueIdentifier = "org/repo#1", IssueTitle = "Test",
            IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            StartedAtOffset = now.AddMinutes(-45) // exceeds 30min, LastStepChangeAt = default
        });

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        _mockLifecycleManager.Verify(
            l => l.FailRunAsync(
                "job-1",
                It.Is<string>(s => s.Contains("progress timeout")),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>()),
            Times.Once);
        // TODO: [WARNING] Add return-value assertion: result.Should().BeFalse(). See note on
        // Execute_RunFound_LastStepChangeAtPastTimeout_FailsRun above — same gap applies here.
    }

    [Fact]
    public async Task Execute_RunFound_BothTimestampsDefault_WarningLoggedNoFail()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "job-1");
        _runService.AddRun(new PipelineRun
        {
            RunId = "job-1", IssueIdentifier = "org/repo#1", IssueTitle = "Test",
            IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            // Both LastStepChangeAt and StartedAtOffset = default
        });

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        _mockLifecycleManager.Verify(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()), Times.Never,
            "run with no valid timestamp must not be failed — skip stall detection");
        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("no valid timestamp")),
                It.IsAny<string>()),
            Times.Once);
    }

    // ── Tests: run not found — consolidation ─────────────────────────────────────

    [Fact]
    public async Task Execute_RunNotFound_IsConsolidationRun_WithinTimeout_NoAction()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "consol-1");
        // No run in runService — consolidation service says it's active
        _mockConsolidationService.Setup(c => c.IsRunActive((RunId)"consol-1")).Returns(true);
        _mockConsolidationService.Setup(c => c.GetActiveRunStartedAt((RunId)"consol-1"))
            .Returns(now.AddMinutes(-10)); // within 30min timeout

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        _mockConsolidationService.Verify(c => c.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Execute_RunNotFound_IsConsolidationRun_PastTimeout_UpdatesRunAndResetsAgent()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "consol-1");
        _mockConsolidationService.Setup(c => c.IsRunActive((RunId)"consol-1")).Returns(true);
        _mockConsolidationService.Setup(c => c.GetActiveRunStartedAt((RunId)"consol-1"))
            .Returns(now.AddMinutes(-45)); // exceeds 30min timeout
        _mockConsolidationService
            .Setup(c => c.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        _mockConsolidationService.Verify(
            c => c.UpdateRunAsync("consol-1", ConsolidationRunStatus.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()),
            Times.Once);
        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
        // TODO: [WARNING] Add assertion: agent.OrphanRestoredAt.Should().BeNull(). The production code
        // clears both ActiveJobId and OrphanRestoredAt inside the lock on the consolidation-timeout path
        // (SweepStuckConsolidationRunsAsync). A regression that stopped clearing OrphanRestoredAt would
        // not be caught by the current assertions — the next sweep would misroute the agent through
        // OrphanRestoredJobSweepPhase instead of ProgressTimeoutSweepPhase.
    }

    // ── Tests: run not found — BusySince grace period ─────────────────────────────

    [Fact]
    public async Task Execute_RunNotFound_BusySinceWithin30sGrace_NoReset()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "job-1");
        entry.BusySince = now.AddSeconds(-20); // within 30s grace

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Busy, "agent within BusySince grace must not be reset");
    }

    [Fact]
    public async Task Execute_RunNotFound_BusySincePast30sGrace_ResetsToIdle()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "job-1");
        entry.BusySince = now.AddSeconds(-60); // past 30s grace

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task Execute_RunNotFound_NullBusySince_ResetsToIdle()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "job-1");
        entry.BusySince = null; // no grace protection

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    // ── Tests: race-lost path ─────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_RaceLost_PhaseExplicitlyClearsAgentState()
    {
        // When FailRunAsync returns null (race lost), the phase itself must clear agent state.
        _mockLifecycleManager
            .Setup(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "job-1");
        _runService.AddRun(new PipelineRun
        {
            RunId = "job-1", IssueIdentifier = "org/repo#1", IssueTitle = "Test",
            IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            LastStepChangeAt = now.AddMinutes(-45)
        });

        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1")!;
        agent.Status.Should().Be(AgentStatus.Idle, "phase must call TransitionStatus(Idle) on race-lost path");
        agent.ActiveJobId.Should().BeNull();
    }

    // ── Tests: TOCTOU — local capture ─────────────────────────────────────────────

    [Fact]
    public async Task Execute_ActiveJobIdNullifiedConcurrently_ConsolidationPath_UsesLocalCaptureNotLiveProperty()
    {
        // Regression test for TOCTOU race on the consolidation path.
        // agent.ActiveJobId must be captured into a local variable before the first use.
        // In the unfixed code, SweepStuckConsolidationRunsAsync re-reads agent.ActiveJobId!
        // on every call (IsRunActive, GetActiveRunStartedAt, log, UpdateRunAsync). A concurrent
        // ReportJobCompleted that nulls ActiveJobId between the ExecuteAsync null-guard and
        // SweepStuckConsolidationRunsAsync's first re-read would produce NullReferenceException.
        //
        // This test simulates the race deterministically: the IsRunActive mock callback nulls
        // out ActiveJobId when called, then asserts GetActiveRunStartedAt was still called with
        // the original job ID — proving the captured local was used rather than the live property.
        var now = DateTimeOffset.UtcNow;
        var entry = RegisterBusyAgent("agent-1", "consol-1");
        // No run in runService — takes the consolidation branch.

        // IsRunActive: returns true AND nulls out ActiveJobId to simulate the race.
        _mockConsolidationService
            .Setup(c => c.IsRunActive((RunId)"consol-1"))
            .Returns(() =>
            {
                // Simulate race: concurrent thread clears ActiveJobId right here.
                lock (entry.SyncRoot) { entry.ActiveJobId = null; }
                return true;
            });

        // GetActiveRunStartedAt: capture the RunId it actually receives.
        RunId? capturedId = null;
        _mockConsolidationService
            .Setup(c => c.GetActiveRunStartedAt(It.IsAny<RunId>()))
            .Returns((RunId runId) =>
            {
                capturedId = runId;
                return now.AddMinutes(-45); // past timeout
            });

        // TODO: [WARNING] Also capture the RunId passed to UpdateRunAsync and assert it equals "consol-1".
        // The current assertions only verify GetActiveRunStartedAt, which means a partial regression
        // where UpdateRunAsync re-reads agent.ActiveJobId instead of using the captured local would not
        // be caught. Adding a parallel capturedUpdateId capture + assertion closes this gap.
        _mockConsolidationService
            .Setup(c => c.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        // Act — must not throw NullReferenceException
        await _phase.ExecuteAsync(entry, now, MakeConfig(TimeSpan.FromMinutes(30)), CancellationToken.None);

        // Assert — GetActiveRunStartedAt received the original job ID, not null
        capturedId.Should().NotBeNull("GetActiveRunStartedAt must have been called");
        capturedId!.Value.Value.Should().Be("consol-1",
            "the locally captured job ID must be used, not the live agent.ActiveJobId which was nulled mid-flight by IsRunActive");
    }

    // TODO: [WARNING] Add a TOCTOU regression test for the main run-found path (FailStuckProgressRunAsync).
    // The test above only exercises the consolidation sub-path. If the `jobId` capture on the
    // FailStuckProgressRunAsync call were reverted to agent.ActiveJobId!, no test would catch it.
    // A companion test should: register a run in runService (so GetRun returns non-null), push the
    // progress reference time past the timeout, simulate the race by nulling ActiveJobId inside the
    // GetRun mock callback or immediately before FailStuckProgressRunAsync is called, and assert that
    // FailStuckProgressRunAsync (or its inner calls) received the original "job-1" ID.

    public void Dispose() { }
}

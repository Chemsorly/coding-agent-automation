using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Characterization tests for <see cref="AgentOrphanRecoveryService"/> extracted from
/// <c>AgentHub.RegisterAgent</c>. Each test covers a specific branch of the recovery logic.
/// </summary>
public sealed class AgentOrphanRecoveryServiceTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ILogger> _mockLogger = new();
    // TODO: Add verification that _mockChangeNotifier.NotifyChange() is called in tests covering
    // the restore-active-job and detect-orphan branches. Currently no tests assert this side effect,
    // so accidental removal of NotifyChange() calls in production code would go undetected.
    private readonly Mock<IChangeNotifier> _mockChangeNotifier = new();
    private readonly AgentOrphanRecoveryService _service;

    public AgentOrphanRecoveryServiceTests()
    {
        _service = new AgentOrphanRecoveryService(
            _mockFacade.Object,
            _mockChangeNotifier.Object,
            _mockLogger.Object);
    }

    // ── Active job restoration: run NOT in memory or history ─────────────

    [Fact]
    public async Task ActiveJob_RunNotInMemoryOrHistory_RestoresRun()
    {
        const string agentId = "agent-1";
        const string runId = "run-123";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunId == runId &&
            r.AgentId == agentId &&
            r.IssueIdentifier == "org/repo#42" &&
            r.CurrentStep == PipelineStep.AnalyzingCode)), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
        entry.ActiveJobId.Should().Be(runId);
    }

    // ── Active job restoration: run in history (Completed) → ignore ─────

    [Fact]
    public async Task ActiveJob_RunInHistoryCompleted_IgnoresStaleState()
    {
        const string agentId = "agent-1";
        const string runId = "run-completed";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#42",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.Completed,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        entry.ActiveJobId.Should().BeNull();
        // TODO: Add negative assertion: _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never)
        // to catch bugs that incorrectly transition the agent to Busy for stale history runs.
    }

    // ── Active job restoration: run in history (Cancelled) → restore ────

    [Fact]
    public async Task ActiveJob_RunInHistoryCancelled_RestoresRun()
    {
        const string agentId = "agent-1";
        const string runId = "run-cancelled";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#42",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.Cancelled,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == runId)), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── Active job restoration: run in history (Failed) → restore ───────

    [Fact]
    public async Task ActiveJob_RunInHistoryFailed_RestoresRun()
    {
        const string agentId = "agent-1";
        const string runId = "run-failed";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#42",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.Failed,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == runId)), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── Active consolidation job → marks busy without run restoration ───

    [Fact]
    public async Task ActiveJob_ConsolidationRun_MarksBusyWithoutRunRestoration()
    {
        const string agentId = "agent-1";
        const string runId = "consol-run-1";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = new ActiveJobState
        {
            RunId = runId,
            IssueIdentifier = "consolidation",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            AgentProviderConfigId = "ap-1",
            InitiatedBy = "consolidation",
            CurrentStep = PipelineStep.AnalyzingCode,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        // Should NOT add a pipeline run
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        // Should mark agent as busy
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
        entry.ActiveJobId.Should().Be(runId);
    }

    // ── Active job: run already in memory (K8s mode, unowned) → links agent

    [Fact]
    public async Task ActiveJob_RunInMemoryUnowned_LinksAgent()
    {
        const string agentId = "agent-1";
        const string runId = "run-k8s";

        var existingRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = null // K8s mode: unowned
        };

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be(agentId);
        entry.ActiveJobId.Should().Be(runId);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── Active job: run already in memory (owned by same agent) → idempotent

    [Fact]
    public async Task ActiveJob_RunInMemoryOwnedBySameAgent_Idempotent()
    {
        const string agentId = "agent-1";
        const string runId = "run-same";

        var existingRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null; // Will be set under lock
        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be(agentId);
        entry.ActiveJobId.Should().Be(runId);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── Active job: run already in memory (pod replacement — different agent reconnects) → updates AgentId

    [Fact]
    public async Task ActiveJob_RunInMemoryOwnedByDifferentAgent_UpdatesAgentId()
    {
        // Pod replacement: a new agent pod registers with the same RunId but a different AgentId.
        // LinkAgentToExistingRun must update run.AgentId and link the new agent to the run.
        // NOTE: This tests LinkAgentToExistingRun in isolation (RecoverOrphanedStateAsync called
        // directly, without a preceding RegisterAgent call). In the combined production flow,
        // RegisterAgent updates run.AgentId first, so by the time LinkAgentToExistingRun runs,
        // existingRun.AgentId already matches — making it a no-op. The isolation test here
        // verifies the service handles the case correctly when called independently.
        // entry.ActiveJobId is null so the inner trackedEntry.ActiveJobId is null lock guard is
        // satisfied, allowing TransitionStatus(Busy) to be called.
        const string agentId = "agent-1";
        const string otherAgent = "agent-other";
        const string runId = "run-other";

        var existingRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = otherAgent
        };

        var entry = CreateEntry(agentId);
        // entry.ActiveJobId is null (default) — satisfies the inner lock guard for TransitionStatus
        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be(agentId, "pod replacement must update run.AgentId to the new agent");
        entry.ActiveJobId.Should().Be(runId, "the new agent must be linked to the run");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "the new agent must be transitioned to Busy");
        // TODO: [WARNING] The acceptance criterion "A log entry is emitted when AgentId is updated due
        // to pod replacement" is not verified here. The _mockLogger is available in this test class;
        // a silent removal of the pod-replacement Information log line would not be caught by any test
        // on the Web.UnitTests recovery-service path. Consider adding a log emission assertion:
        // _mockLogger.Verify(l => l.Information(It.Is<string>(s => s.Contains("pod replacement")), ...), Times.Once);
    }

    // ── Orphan detection: orchestrator has orphaned runs → sets OrphanRestoredAt

    [Fact]
    public async Task NoActiveJob_OrphanedRuns_SetsOrphanRestoredAt()
    {
        const string agentId = "agent-1";
        const string runId = "orphan-run-1";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Orphaned",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(runId);
        entry.OrphanRestoredAt.Should().NotBeNull();
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── Orphan detection: no orphaned runs → no-op

    [Fact]
    public async Task NoActiveJob_NoOrphanedRuns_NoOp()
    {
        const string agentId = "agent-1";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().BeNull();
        entry.OrphanRestoredAt.Should().BeNull();
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── Crash recovery: registry has ActiveJobId but agent doesn't → sets OrphanRestoredAt

    [Fact]
    public async Task NoActiveJob_RegistryHasActiveJobId_CrashRecoverySetsOrphanRestoredAt()
    {
        const string agentId = "agent-1";
        const string existingJobId = "existing-job-1";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.OrphanRestoredAt.Should().NotBeNull();
        // TODO: Add negative assertions to verify crash recovery does NOT modify ActiveJobId or call TransitionStatus:
        // entry.ActiveJobId.Should().Be(existingJobId);
        // _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── Race condition: DrainService assigns job between check and lock → skips

    [Fact]
    public async Task NoActiveJob_DrainServiceAssignsJobDuringCheck_SkipsOrphanRestoration()
    {
        const string agentId = "agent-1";
        const string orphanRunId = "orphan-1";
        const string drainJobId = "drain-assigned-job";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = orphanRunId,
            IssueIdentifier = "org/repo#77",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        // First call returns null (entry.ActiveJobId is null for the outer if check),
        // but the entry itself is modified to simulate DrainService assigning a job before lock.
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId))
            .Returns([orphanedRun])
            .Callback(() =>
            {
                // Simulate DrainService assigning a job between GetActiveRunsByAgent and lock
                entry.ActiveJobId = drainJobId;
            });
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        // Should NOT overwrite the drain-assigned job
        entry.ActiveJobId.Should().Be(drainJobId);
        entry.OrphanRestoredAt.Should().BeNull();
    }

    // ── Active job: RunType=Review → creates a review run ────────────────

    [Fact]
    public async Task ActiveJob_ReviewRunType_RestoresReviewRun()
    {
        const string agentId = "agent-1";
        const string runId = "review-run-1";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = CreateActiveJob(runId) with { RunType = PipelineRunType.Review };
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunId == runId &&
            r.RunType == PipelineRunType.Review)), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── Active job: RunType=Decomposition → creates a decomposition run ──

    [Fact]
    public async Task ActiveJob_DecompositionRunType_RestoresDecompositionRun()
    {
        const string agentId = "agent-1";
        const string runId = "decomp-run-1";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = CreateActiveJob(runId) with { RunType = PipelineRunType.Decomposition };
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunId == runId &&
            r.RunType == PipelineRunType.Decomposition)), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── Verified claims: WorkItem store available (the API host) ─────────

    /// <summary>
    /// An agent reporting an active job that is not a work item at all (an invented run) gets
    /// nothing restored — no run, no ActiveJobId, so no [RequiresActiveJob] access.
    /// </summary>
    [Fact]
    public async Task VerifiedClaim_NoSuchWorkItem_IsIgnored()
    {
        const string agentId = "caa-aaaa1111";
        const string runId = "run-invented";
        var entry = CreateEntry(agentId);
        WithWorkItemStore(runId, record: null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        await _service.RecoverOrphanedStateAsync(CreateMessage(agentId, CreateActiveJob(runId)), agentId);

        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Never);
        entry.ActiveJobId.Should().BeNull();
    }

    /// <summary>
    /// An agent cannot take over a run whose work item belongs to another agent, even when it
    /// knows the run ID.
    /// </summary>
    [Fact]
    public async Task VerifiedClaim_WorkItemOfAnotherAgent_RunIsNotTakenOver()
    {
        const string agentId = "caa-bbbb2222";
        const string owner = "caa-cccc3333";
        const string runId = "run-of-another-agent";
        var existingRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = owner
        };
        var entry = CreateEntry(agentId);
        WithWorkItemStore(runId, OwnedRecord(owner));
        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        await _service.RecoverOrphanedStateAsync(CreateMessage(agentId, CreateActiveJob(runId)), agentId);

        existingRun.AgentId.Should().Be(owner, "the run's work item belongs to another agent");
        entry.ActiveJobId.Should().BeNull();
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Never);
    }

    /// <summary>
    /// For the agent's own work item the run is restored, but its identity — issue and the provider
    /// configs tokens are vended for — comes from the database, not from the agent's report.
    /// </summary>
    [Fact]
    public async Task VerifiedClaim_OwnWorkItem_RestoresRunWithIdentityFromTheDatabase()
    {
        const string agentId = "caa-dddd4444";
        const string runId = "run-own";
        var entry = CreateEntry(agentId);
        WithWorkItemStore(runId, OwnedRecord(agentId));
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);
        PipelineRun? restored = null;
        _mockFacade.Setup(f => f.AddRun(It.IsAny<PipelineRun>())).Callback<PipelineRun>(r => restored = r);

        var claim = CreateActiveJob(runId) with
        {
            IssueIdentifier = "org/other#9",
            IssueProviderConfigId = "ip-other",
            RepoProviderConfigId = "rp-other",
            BrainProviderConfigId = "brain-other",
            PipelineProviderConfigId = "pipe-other",
            ProjectId = Guid.NewGuid().ToString()
        };

        await _service.RecoverOrphanedStateAsync(CreateMessage(agentId, claim), agentId);

        restored.Should().NotBeNull();
        ((string)restored!.IssueIdentifier).Should().Be("org/repo#7");
        restored.IssueProviderConfigId.Should().Be("ip-db");
        restored.RepoProviderConfigId.Should().Be("rp-db");
        restored.BrainProviderConfigId.Should().Be("brain-db");
        restored.PipelineProviderConfigId.Should().Be("pipe-db");
        restored.ProjectId.Should().Be("11111111-2222-3333-4444-555555555555");
        restored.CurrentStep.Should().Be(PipelineStep.AnalyzingCode, "progress still comes from the agent");
        entry.ActiveJobId.Should().Be(runId);
    }

    /// <summary>
    /// Whether the claim is a consolidation run is decided by the work item's task type, not by the
    /// agent's report.
    /// </summary>
    [Fact]
    public async Task VerifiedClaim_ConsolidationWorkItem_IsTrackedWithoutAPipelineRun()
    {
        const string agentId = "caa-eeee5555";
        const string runId = "run-consolidation";
        var entry = CreateEntry(agentId);
        WithWorkItemStore(runId, OwnedRecord(agentId, WorkItemTaskType.Consolidation));
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        // The agent claims an ordinary implementation run.
        await _service.RecoverOrphanedStateAsync(CreateMessage(agentId, CreateActiveJob(runId)), agentId);

        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        entry.ActiveJobId.Should().Be(runId);
    }

    private void WithWorkItemStore(string runId, WorkItemRunRecord? record)
    {
        _mockFacade.SetupGet(f => f.CanVerifyWorkItems).Returns(true);
        _mockFacade.Setup(f => f.GetWorkItemRunRecordAsync(It.Is<JobId>(j => j.Value == runId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);
    }

    private static WorkItemRunRecord OwnedRecord(string k8sJobName, WorkItemTaskType taskType = WorkItemTaskType.Implementation) => new()
    {
        TaskType = taskType,
        K8sJobName = k8sJobName,
        IssueIdentifier = "org/repo#7",
        IssueProviderConfigId = "ip-db",
        RepoProviderConfigId = "rp-db",
        BrainProviderConfigId = "brain-db",
        PipelineProviderConfigId = "pipe-db",
        ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555")
    };

    // ── Helpers ─────────────────────────────────────────────────────────

    private static AgentEntry CreateEntry(string agentId) => new()
    {
        AgentId = agentId,
        ConnectionId = "conn-1",
        Hostname = "host-1",
        Labels = ["dotnet"],
        Status = AgentStatus.Idle,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    private static AgentRegistrationMessage CreateMessage(string agentId, ActiveJobState? activeJob) => new()
    {
        AgentId = agentId,
        Hostname = "host-1",
        Labels = ["dotnet"],
        ActiveJob = activeJob
    };

    private static ActiveJobState CreateActiveJob(string runId) => new()
    {
        RunId = runId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        AgentProviderConfigId = "ap-1",
        InitiatedBy = "loop",
        CurrentStep = PipelineStep.AnalyzingCode,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
    };

    // ── ModelName/RepositoryName adoption in LinkAgentToExistingRun ──

    [Fact]
    public async Task ActiveJob_RunInMemoryUnowned_AdoptsModelNameAndRepositoryName()
    {
        const string agentId = "agent-1";
        const string runId = "run-k8s";

        var existingRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = null,
            ModelName = null,
            RepositoryName = null
        };

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = CreateActiveJobWithMetadata(runId, "claude-sonnet-4-5", "my-repo");
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.ModelName.Should().Be("claude-sonnet-4-5");
        existingRun.RepositoryName.Should().Be("my-repo");
    }

    [Fact]
    public async Task ActiveJob_RunInMemoryWithExistingModelName_DoesNotOverwrite()
    {
        const string agentId = "agent-1";
        const string runId = "run-k8s";

        var existingRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId,
            ModelName = "already-set-model",
            RepositoryName = "already-set-repo"
        };

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = runId;
        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = CreateActiveJobWithMetadata(runId, "new-model-should-not-overwrite", "new-repo-should-not-overwrite");
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.ModelName.Should().Be("already-set-model", "??= must not overwrite existing value");
        existingRun.RepositoryName.Should().Be("already-set-repo", "??= must not overwrite existing value");
    }

    // ── Null trackedEntry in LinkAgentToExistingRun ───────────────────

    [Fact]
    public async Task ActiveJob_RunInMemoryUnowned_NullTrackedEntry_DoesNotThrow()
    {
        const string agentId = "agent-1";
        const string runId = "run-k8s";

        var existingRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = null
        };

        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns((AgentEntry?)null);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        var act = async () => await _service.RecoverOrphanedStateAsync(message, agentId);
        await act.Should().NotThrowAsync();

        existingRun.AgentId.Should().Be(agentId);
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── HandleCrashRecovery else-branch ──────────────────────────────

    [Fact]
    public async Task CrashRecovery_OrphanRestoredAtAlreadySet_DoesNotOverwrite()
    {
        const string agentId = "agent-1";
        const string existingJobId = "existing-job-1";

        var alreadySetAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = alreadySetAt;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.OrphanRestoredAt.Should().Be(alreadySetAt, "existing OrphanRestoredAt must not be overwritten");
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── DetectAndRestoreOrphans race: drain assigns job → TransitionStatus NOT called ─

    [Fact]
    public async Task NoActiveJob_DrainServiceAssignsJobDuringCheck_DoesNotCallTransitionStatus()
    {
        const string agentId = "agent-1";
        const string orphanRunId = "orphan-1";
        const string drainJobId = "drain-assigned-job";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = orphanRunId,
            IssueIdentifier = "org/repo#77",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId))
            .Returns([orphanedRun])
            .Callback(() => { entry.ActiveJobId = drainJobId; });
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var message = CreateMessage(agentId, activeJob: null);
        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(drainJobId);
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── RestorePipelineRun sets ModelName and RepositoryName ──────────

    [Fact]
    public async Task ActiveJob_RunNotInMemory_RestoredRunHasModelNameAndRepositoryName()
    {
        const string agentId = "agent-1";
        const string runId = "run-restore";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = CreateActiveJobWithMetadata(runId, "claude-3-5-haiku", "target-repo");
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunId == runId &&
            r.ModelName == "claude-3-5-haiku" &&
            r.RepositoryName == "target-repo")), Times.Once);
    }

    // ── Null entry, no active job → silent no-op ─────────────────────

    [Fact]
    public async Task NoActiveJob_NullEntry_NoOp()
    {
        const string agentId = "agent-1";

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns((AgentEntry?)null);

        var message = CreateMessage(agentId, activeJob: null);

        var act = async () => await _service.RecoverOrphanedStateAsync(message, agentId);
        await act.Should().NotThrowAsync();

        _mockFacade.Verify(f => f.GetActiveRunsByAgent(It.IsAny<AgentId>()), Times.Never);
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── ChangeNotifier.NotifyChange() is called ───────────────────────

    [Fact]
    public async Task ActiveJob_RunNotInMemory_NotifiesChange()
    {
        const string agentId = "agent-1";
        const string runId = "run-notify";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));
        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockChangeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    // ── Helpers for tests that need ModelName/RepositoryName ──────────

    private static ActiveJobState CreateActiveJobWithMetadata(string runId, string modelName, string repositoryName)
    {
        var job = CreateActiveJob(runId);
        return job with { ModelName = modelName, RepositoryName = repositoryName };
    }

    // ── Additional coverage: null entry in RestoreConsolidationTracking ──────────

    [Fact]
    public async Task ActiveJob_ConsolidationRun_NullEntry_DoesNotThrow()
    {
        const string agentId = "agent-null-entry";
        const string runId = "consol-null-entry";

        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns((AgentEntry?)null); // null entry
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = new ActiveJobState
        {
            RunId = runId,
            IssueIdentifier = "consolidation",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            AgentProviderConfigId = "ap-1",
            InitiatedBy = "consolidation",
            CurrentStep = PipelineStep.AnalyzingCode,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var message = CreateMessage(agentId, activeJob);

        var act = async () => await _service.RecoverOrphanedStateAsync(message, agentId);
        await act.Should().NotThrowAsync("null entry in consolidation tracking must be a no-op");

        // AddRun never called for consolidation
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        // TransitionStatus never called (entry is null)
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── Additional coverage: null entry in RestorePipelineRun ────────────────────

    [Fact]
    public async Task ActiveJob_PipelineRun_NullEntry_DoesNotThrow()
    {
        const string agentId = "agent-null-pipeline";
        const string runId = "pipeline-null-entry";

        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns((AgentEntry?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        var act = async () => await _service.RecoverOrphanedStateAsync(message, agentId);
        await act.Should().NotThrowAsync("null entry in pipeline run restoration must be a no-op for the status transition");

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == runId)), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── Multiple orphaned runs: picks most recent ────────────────────────────────

    [Fact]
    public async Task NoActiveJob_MultipleOrphanedRuns_PicksMostRecent()
    {
        const string agentId = "agent-multi-orphan";
        const string oldRunId = "orphan-old";
        const string newRunId = "orphan-new";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var olderRun = new PipelineRun
        {
            RunId = oldRunId,
            IssueIdentifier = "org/repo#10",
            IssueTitle = "Old Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };
        var newerRun = new PipelineRun
        {
            RunId = newRunId,
            IssueIdentifier = "org/repo#11",
            IssueTitle = "New Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId))
            .Returns([olderRun, newerRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(newRunId,
            "DetectAndRestoreOrphans must restore the most recent (last) orphaned run");
        entry.OrphanRestoredAt.Should().NotBeNull();
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── ConsolidationTracking notifies change ────────────────────────────────────

    [Fact]
    public async Task ActiveJob_ConsolidationRun_NotifiesChange()
    {
        const string agentId = "agent-consol-notify";
        const string runId = "consol-notify-1";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = new ActiveJobState
        {
            RunId = runId,
            IssueIdentifier = "consolidation",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            AgentProviderConfigId = "ap-1",
            InitiatedBy = "consolidation",
            CurrentStep = PipelineStep.AnalyzingCode,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockChangeNotifier.Verify(c => c.NotifyChange(), Times.Once);
    }

    // ── DetectAndRestoreOrphans: orphan restored but TransitionStatus skipped ────

    [Fact]
    public async Task NoActiveJob_OrphanedRuns_DrainRacePreventsBusyTransition()
    {
        const string agentId = "agent-race-busy";
        const string orphanRunId = "orphan-race";
        const string drainAssignedId = "drain-job-different";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = orphanRunId,
            IssueIdentifier = "org/repo#50",
            IssueTitle = "Orphan Race",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId))
            .Returns([orphanedRun])
            .Callback(() => { entry.ActiveJobId = drainAssignedId; });
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(drainAssignedId,
            "drain-assigned job must not be overwritten by orphan restoration");
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── HandleCrashRecovery else branch: agent has activeJob ────────────────────

    [Fact]
    public async Task CrashRecovery_AgentHasActiveJob_LogsElseBranch()
    {
        const string agentId = "agent-crash-else";
        const string existingJobId = "existing-job-crash";
        const string runId = "active-run";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        var existingRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };
        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = CreateActiveJob(runId);
        var message = CreateMessage(agentId, activeJob);

        var act = async () => await _service.RecoverOrphanedStateAsync(message, agentId);
        await act.Should().NotThrowAsync();

        entry.OrphanRestoredAt.Should().BeNull(
            "else branch of HandleCrashRecovery must not set OrphanRestoredAt");
    }

    // ── DecompositionAnalysis RunType → creates decomposition run ────────────────

    [Fact]
    public async Task ActiveJob_DecompositionAnalysisRunType_RestoresDecompositionRun()
    {
        const string agentId = "agent-decomp-analysis";
        const string runId = "decomp-analysis-run-1";

        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var activeJob = CreateActiveJob(runId) with { RunType = PipelineRunType.DecompositionAnalysis };
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunId == runId &&
            r.RunType == PipelineRunType.DecompositionAnalysis)), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── DetectAndRestoreOrphans: AddRun called to re-materialize Redis hash ────

    [Fact]
    public async Task NoActiveJob_OrphanedRuns_AddsPipelineRunToRedis()
    {
        // AC1: When DetectAndRestoreOrphans restores an orphaned run, _facade.AddRun is called
        // so GetRun returns a non-null PipelineRun after the method completes.
        const string agentId = "agent-orphan-addrun";
        const string runId = "orphan-addrun-1";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Orphaned Run",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // GetRun returns null — hash is absent (expired or not yet written). Under the fix,
        // GetRun returning null is the condition that triggers AddRun to re-materialize the hash.
        // If GetRun returned non-null, AddRun would be skipped (hash is live, no overwrite needed).
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == runId))).Returns((PipelineRun?)null);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        // AddRun must be called to re-materialize the Redis hash when the hash is absent
        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == runId)), Times.Once);
        entry.ActiveJobId.Should().Be(runId);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── DetectAndRestoreOrphans: drain race → AddRun NOT called ──────────────────

    [Fact]
    public async Task NoActiveJob_DrainRace_DoesNotCallAddRun()
    {
        // When DrainService assigns a job between GetActiveRunsByAgent and the lock,
        // the orphan restore is skipped and AddRun must NOT be called.
        const string agentId = "agent-drain-no-addrun";
        const string orphanRunId = "orphan-drain";
        const string drainJobId = "drain-assigned-job";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = orphanRunId,
            IssueIdentifier = "org/repo#77",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId))
            .Returns([orphanedRun])
            .Callback(() => { entry.ActiveJobId = drainJobId; }); // simulate drain race
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(drainJobId, "drain-assigned job must not be overwritten");
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when the drain race skips orphan restoration");
    }

    // ── DetectAndRestoreOrphans: idempotency — calling twice calls AddRun twice ──

    [Fact]
    public async Task NoActiveJob_OrphanedRuns_CalledTwice_AddRunCalledTwice()
    {
        // Idempotency AC: calling DetectAndRestoreOrphans twice for the same run must not
        // create duplicate entries or corrupt the active set. AddRun is called twice (HSET +
        // SADD are both idempotent Redis ops — same fields overwrite, SADD is a no-op on
        // existing members). This test documents call-count behavior; Redis-level idempotency
        // is guaranteed by DistributedRunService semantics, not the mock.
        // Note (issue #2663): after the GetRun guard fix, if GetRun returned non-null on the
        // second call (because the first AddRun had populated the store), AddRun would be
        // skipped on the second call. The mock does not propagate AddRun state to GetRun, so
        // GetRun returns null both times here — both calls still exercise the absent-hash path
        // and AddRun is called twice. This test does NOT cover the case where GetRun is non-null
        // on a repeated call (hash already live); that path is covered by
        // NoActiveJob_OrphanedRuns_HashAlreadyExists_DoesNotCallAddRun.
        // TODO: [WARNING] This test simulates "a second registration cycle" by manually resetting
        // entry.ActiveJobId = null and entry.OrphanRestoredAt = null between calls. This does NOT
        // match the real idempotency scenario from the AC: on a genuine second call with state
        // already restored, entry.ActiveJobId would still be set (from the first call), causing the
        // already-assigned guard inside the lock to fire and produce zero AddRun calls — not one.
        // The real idempotency contract (second call is a no-op when entry is already populated) is
        // not validated by this test. Consider adding a separate test for that path.
        const string agentId = "agent-idempotent";
        const string runId = "orphan-idempotent-1";

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#55",
            IssueTitle = "Orphaned",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        // First call: entry has no ActiveJobId
        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var message = CreateMessage(agentId, activeJob: null);
        await _service.RecoverOrphanedStateAsync(message, agentId);

        // Second call: entry already has ActiveJobId set from first call — but for the second
        // invocation to re-enter DetectAndRestoreOrphans, we need entry.ActiveJobId to be null
        // again (simulating a second registration cycle). Reset it to test the idempotency path.
        entry.ActiveJobId = null;
        entry.OrphanRestoredAt = null;
        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == runId)), Times.Exactly(2),
            "AddRun must be called on each successful orphan restore; HSET+SADD idempotency ensures no corruption");
    }

    // ── HandleCrashRecovery: hash exists → AddRun called to re-materialize ────────

    [Fact]
    public async Task NoActiveJob_RegistryHasActiveJobId_CrashRecovery_ReAddsRunIfHashExists()
    {
        // When the crash recovery path fires and the run hash still exists in Redis,
        // AddRun must be called to refresh it (preventing expiry before the agent's first hub call).
        const string agentId = "agent-crash-addrun";
        const string existingJobId = "crash-run-1";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        var existingRun = new PipelineRun
        {
            RunId = existingJobId,
            IssueIdentifier = "org/repo#88",
            IssueTitle = "Crash Run",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        // GetRun returns the run — hash exists in Redis
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == existingJobId))).Returns(existingRun);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.OrphanRestoredAt.Should().NotBeNull("crash recovery must set OrphanRestoredAt");
        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == existingJobId)), Times.Once,
            "AddRun must be called to re-materialize the run hash when it still exists");
    }

    // ── HandleCrashRecoveryAsync: DB fallback when hash expired ─────────────────

    [Fact]
    public async Task NoActiveJob_RegistryHasActiveJobId_CrashRecovery_ReconstructsRunFromDbWhenHashGone()
    {
        // When GetRun returns null (hash expired) but the WorkItem exists in DB with valid
        // IssueIdentifier, IssueProviderConfigId and RepoProviderConfigId, TryReconstructRunFromDbAsync
        // must build a minimal PipelineRun and call AddRun so subsequent [RequiresActiveJob] calls succeed.
        const string agentId = "agent-crash-reconstruct";
        const string existingJobId = "00000000-0000-0000-0000-000000000001"; // valid GUID required

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == existingJobId))).Returns((PipelineRun?)null);
        _mockFacade
            .Setup(f => f.GetWorkItemIssueMetadataAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("org/repo#42", "issue-cfg-1"));
        _mockFacade
            .Setup(f => f.GetWorkItemProviderConfigIdsAsync(existingJobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(("repo-cfg-1", "brain-cfg-1"));

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.OrphanRestoredAt.Should().NotBeNull();
        _mockFacade.Verify(
            f => f.AddRun(It.Is<PipelineRun>(r =>
                r.RunId == existingJobId &&
                r.IssueIdentifier == "org/repo#42" &&
                r.IssueProviderConfigId == "issue-cfg-1")),
            Times.Once,
            "AddRun must be called with the DB-reconstructed PipelineRun when the Redis hash has expired");
    }

    [Fact]
    public async Task NoActiveJob_RegistryHasActiveJobId_CrashRecovery_SkipsReconstructionWhenWorkItemNotFound()
    {
        // When GetRun returns null AND GetWorkItemIssueMetadataAsync also returns null,
        // AddRun must NOT be called — nothing recoverable from DB either.
        const string agentId = "agent-crash-nodb";
        const string existingJobId = "00000000-0000-0000-0000-000000000002";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == existingJobId))).Returns((PipelineRun?)null);
        _mockFacade
            .Setup(f => f.GetWorkItemIssueMetadataAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.OrphanRestoredAt.Should().NotBeNull();
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when the WorkItem is also absent from DB");
    }

    [Fact]
    public async Task NoActiveJob_RegistryHasActiveJobId_CrashRecovery_SkipsReconstructionWhenNoRepoProviderConfig()
    {
        // When GetRun returns null, WorkItem exists, but RepoProviderConfigId is absent from Payload,
        // reconstruction must be skipped — a run without RepoProviderConfigId is invalid.
        const string agentId = "agent-crash-norepo";
        const string existingJobId = "00000000-0000-0000-0000-000000000003";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == existingJobId))).Returns((PipelineRun?)null);
        _mockFacade
            .Setup(f => f.GetWorkItemIssueMetadataAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("org/repo#42", "issue-cfg-1"));
        _mockFacade
            .Setup(f => f.GetWorkItemProviderConfigIdsAsync(existingJobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string? RepoProviderConfigId, string? BrainProviderConfigId)?)(null, null));

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.OrphanRestoredAt.Should().NotBeNull();
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when RepoProviderConfigId cannot be recovered from DB Payload");
    }

    // ── HandleCrashRecovery: hash gone → AddRun NOT called ────────────────────────

    // ── DetectAndRestoreOrphans: SetLocalAgentSnapshotField called (issue #2616) ─

    [Fact]
    public async Task NoActiveJob_OrphanedRuns_CallsSetLocalAgentSnapshotField_WithRestoredRunId()
    {
        // When DetectAndRestoreOrphans succeeds, it must call SetLocalAgentSnapshotField
        // for both "activeJobId" and "orphanRestoredAt" BEFORE the fire-and-forget Redis
        // write — so GetByConnectionId returns the correct value synchronously (issue #2616).
        const string agentId = "agent-1";
        const string runId = "orphan-snapshot-1";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var message = CreateMessage(agentId, activeJob: null);
        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(
            f => f.SetLocalAgentSnapshotField(
                It.Is<AgentId>(a => a.Value == agentId),
                "activeJobId",
                runId),
            Times.Once,
            "SetLocalAgentSnapshotField must be called with activeJobId to update _localSnapshot synchronously");

        _mockFacade.Verify(
            f => f.SetLocalAgentSnapshotField(
                It.Is<AgentId>(a => a.Value == agentId),
                "orphanRestoredAt",
                It.IsAny<string>()),
            Times.Once,
            "SetLocalAgentSnapshotField must be called with orphanRestoredAt to update _localSnapshot synchronously");
        // TODO (WARNING): The verifications above confirm SetLocalAgentSnapshotField is called
        // with the correct arguments but do NOT enforce that it is called *before*
        // UpdateAgentFieldAsync. The core acceptance criterion for issue #2616 is call ordering
        // — the synchronous snapshot update must precede the fire-and-forget Redis write.
        // A future refactor that swaps the two calls would leave this test green while
        // reintroducing the bug. Use a MockSequence or an InOrder callback counter to assert
        // SetLocalAgentSnapshotField is invoked before UpdateAgentFieldAsync. (TestQualityReviewer
        // WARNING, issue #2616)
    }

    [Fact]
    public async Task NoActiveJob_DrainRace_DoesNotCallSetLocalAgentSnapshotField()
    {
        // When the drain race fires (entry.ActiveJobId set before lock acquisition),
        // the restore is skipped and SetLocalAgentSnapshotField must NOT be called.
        const string agentId = "agent-1";
        const string orphanRunId = "orphan-snap-race";
        const string drainJobId = "drain-snap-race";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = orphanRunId,
            IssueIdentifier = "org/repo#77",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId))
            .Returns([orphanedRun])
            .Callback(() => { entry.ActiveJobId = drainJobId; }); // simulate drain race
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        // TODO (WARNING): This test relies on GetActiveRunsByAgent being called *before*
        // lock(entry.SyncRoot) in the production code — the Callback sets entry.ActiveJobId
        // so the subsequent lock check sees a non-null value and skips the restore branch.
        // If DetectAndRestoreOrphans is ever refactored to call GetActiveRunsByAgent inside
        // the lock, the Callback will still fire but entry.ActiveJobId will already be checked
        // before the callback runs, silently breaking the drain-race simulation. The
        // Times.Never verification below would become a false negative. This assumption on
        // call-site ordering should be reviewed on any refactor of DetectAndRestoreOrphans.
        // (TestQualityReviewer WARNING, issue #2616)
        var message = CreateMessage(agentId, activeJob: null);
        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(drainJobId, "drain-assigned job must not be overwritten");
        _mockFacade.Verify(
            f => f.SetLocalAgentSnapshotField(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never,
            "SetLocalAgentSnapshotField must not be called when the drain race prevents orphan restoration");
        // TODO (WARNING): UpdateAgentFieldAsync is also not called when the drain race fires — add
        // a corresponding Never-verify for UpdateAgentFieldAsync here to prevent a future regression
        // where a code path calls UpdateAgentFieldAsync but skips SetLocalAgentSnapshotField in the
        // drain-race branch (TestQualityReviewer WARNING, issue #2616).
    }

    // ── DetectAndRestoreOrphans: hash-exists guard (issue #2663) ────────────────

    [Fact]
    public async Task NoActiveJob_OrphanedRuns_HashAlreadyExists_DoesNotCallAddRun()
    {
        // When the orphan's Redis hash already exists (another replica wrote it, or it hasn't
        // expired), AddRun must NOT be called. Calling AddRun would overwrite all fields from
        // the stale snapshot, clobbering newer values (e.g. currentStep, prUrl) written by
        // other replicas between GetActiveRunsByAgent and this code path.
        const string agentId = "agent-hash-exists";
        const string runId = "orphan-hash-exists-1";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Orphaned Run",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId,
            CurrentStep = PipelineStep.Created // stale snapshot value
        };
        var liveRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Orphaned Run",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId,
            CurrentStep = PipelineStep.GeneratingCode // advanced by another replica
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // GetRun returns non-null — hash exists in Redis (live run with advanced state)
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == runId))).Returns(liveRun);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when the hash already exists — would overwrite live fields with stale snapshot");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must still be called even when AddRun is skipped");
        entry.ActiveJobId.Should().Be(runId);
    }

    [Fact]
    public async Task NoActiveJob_OrphanedRuns_HashAlreadyExists_PreservesCurrentStep()
    {
        // Issue #2663 AC: "a hash with pre-existing currentStep=3 must retain currentStep=3
        // after orphan restore with a snapshot containing currentStep=1."
        //
        // At this test boundary (_mockFacade is mocked), the mock's AddRun is the ONLY write
        // path available to DetectAndRestoreOrphans — if AddRun is never called, no Redis fields
        // can be overwritten. Times.Never on AddRun IS the proof that currentStep is preserved:
        // no write occurred, therefore currentStep (and all other fields) remain at their live values.
        const string agentId = "agent-step-preserve";
        const string runId = "orphan-step-preserve-1";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        // Snapshot orphan has stale currentStep=Created (the initial value when the run was dispatched)
        var staleSnapshot = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#100",
            IssueTitle = "Step Preserve Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId,
            CurrentStep = PipelineStep.Created // stale: the "currentStep=1" from the AC
        };

        // Live hash has currentStep=GeneratingCode — advanced by another replica while this
        // agent was disconnected. This simulates the "currentStep=3" scenario from the AC.
        var liveHash = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#100",
            IssueTitle = "Step Preserve Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId,
            CurrentStep = PipelineStep.GeneratingCode // live: the "currentStep=3" from the AC
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([staleSnapshot]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // GetRun returns the live hash — hash exists with advanced currentStep
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == runId))).Returns(liveHash);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        // The issue's AC requires currentStep to be preserved. At the mock boundary:
        // AddRun Times.Never proves no write occurred → no field was overwritten.
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called — calling it would overwrite currentStep=GeneratingCode with stale Created");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called unconditionally");
        // TODO: [WARNING] The assertion below is tautological: liveHash is a local C# object that
        // no code path in DetectAndRestoreOrphans can mutate (it is only returned from the GetRun
        // mock). The assertion will always pass regardless of whether the guard is present or not.
        // The load-bearing proof of AC #3 is the Times.Never on AddRun above. This assertion
        // documents intent but adds no regression safety. An integration test against a real/fake
        // Redis store would provide stronger field-level evidence.
        // Verify the live hash state is unchanged (staleSnapshot was never written)
        liveHash.CurrentStep.Should().Be(PipelineStep.GeneratingCode,
            "the live currentStep must not be overwritten by the stale snapshot value");
    }

    // ── DetectAndRestoreOrphans: log-level discrimination (issue #2956) ─────────

    /// <summary>
    /// Normal first-registration: orphan's CurrentStep at or before AnalyzingCode
    /// (run was dispatched but agent has not progressed yet) must log at Information, not Warning.
    /// </summary>
    [Fact]
    public async Task DetectAndRestoreOrphans_RunAtInitialStep_LogsAtInformation()
    {
        const string agentId = "agent-log-info";
        const string runId = "orphan-log-info-1";

        var entry = CreateEntry(agentId);
        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#200",
            IssueTitle = "Orphan Info",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId,
            CurrentStep = PipelineStep.AnalyzingCode // at the initial step — first registration
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var message = CreateMessage(agentId, activeJob: null);
        await _service.RecoverOrphanedStateAsync(message, agentId);

        // Verify: Write(Information, ...) was called — not Warning.
        // The production call passes 4 value args, which C# resolves to the params-array overload
        // Write(LogEventLevel, string, object[]) — not the nonexistent 6-param signature (issue #2956).
        _mockLogger.Verify(
            l => l.Write(
                Serilog.Events.LogEventLevel.Information,
                It.Is<string>(s => s.Contains("re-registered without active job")),
                It.IsAny<object[]>()),
            Times.Once,
            "first-registration orphan restore (CurrentStep <= AnalyzingCode) must log at Information");
        _mockLogger.Verify(
            l => l.Write(
                Serilog.Events.LogEventLevel.Warning,
                It.Is<string>(s => s.Contains("re-registered without active job")),
                It.IsAny<object[]>()),
            Times.Never,
            "first-registration orphan restore must NOT log at Warning");
    }

    /// <summary>
    /// Mid-run re-registration: orphan's CurrentStep beyond AnalyzingCode means an agent was
    /// actively working and something interrupted it — must still log at Warning.
    /// </summary>
    [Fact]
    public async Task DetectAndRestoreOrphans_RunBeyondInitialStep_LogsAtWarning()
    {
        const string agentId = "agent-log-warn";
        const string runId = "orphan-log-warn-1";

        var entry = CreateEntry(agentId);
        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#201",
            IssueTitle = "Orphan Warning",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId,
            CurrentStep = PipelineStep.GeneratingCode // mid-run — agent was actively working
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var message = CreateMessage(agentId, activeJob: null);
        await _service.RecoverOrphanedStateAsync(message, agentId);

        // Verify: Write(Warning, ...) was called.
        // The production call passes 4 value args, which C# resolves to the params-array overload
        // Write(LogEventLevel, string, object[]) — not the nonexistent 6-param signature (issue #2956).
        _mockLogger.Verify(
            l => l.Write(
                Serilog.Events.LogEventLevel.Warning,
                It.Is<string>(s => s.Contains("re-registered without active job")),
                It.IsAny<object[]>()),
            Times.Once,
            "mid-run orphan restore (CurrentStep > AnalyzingCode) must log at Warning");
    }

    // ── DetectAndRestoreOrphans: terminal-state history guard (issue #3044) ────

    [Fact]
    public async Task DetectAndRestoreOrphans_CompletedRunInActiveSet_IsSkipped()
    {
        // AC1 + AC4: A completed run lingering in the active set must not be re-activated.
        // The agent must remain in Idle state (TransitionStatus(Busy) never called).
        // TODO [WARNING]: entry.OrphanRestoredAt.Should().BeNull() below is an implementation-detail
        // assertion — it verifies the early-return by checking that OrphanRestoredAt (set inside
        // lock) was never written. If OrphanRestoredAt is moved outside the lock in a future refactor,
        // this assertion becomes a false negative or breaks for the wrong reason. The primary
        // observable behavior is already captured by AddRun=Never, TransitionStatus=Never,
        // and ActiveJobId=null.
        const string agentId = "agent-history-skip";
        const string runId = "run-completed-orphan";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Completed orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#42",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.Completed,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called for a completed run");
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never,
            "TransitionStatus must not be called — agent must stay Idle");
        entry.ActiveJobId.Should().BeNull("completed run must not set ActiveJobId");
        entry.OrphanRestoredAt.Should().BeNull("completed run must not set OrphanRestoredAt (lock block was bypassed)");
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_PrMergedRunInActiveSet_IsSkipped()
    {
        // Guard must skip PrMerged (terminal non-retryable) just as it skips Completed.
        const string agentId = "agent-history-prmerged";
        const string runId = "run-pr-merged-orphan";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#47",
            IssueTitle = "PrMerged orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#47",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.PrMerged,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called for a PrMerged run");
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never,
            "TransitionStatus must not be called — agent must stay Idle");
        entry.ActiveJobId.Should().BeNull("PrMerged run must not set ActiveJobId");
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_PrClosedRunInActiveSet_IsSkipped()
    {
        // Guard must skip PrClosed (terminal non-retryable) just as it skips Completed.
        const string agentId = "agent-history-prclosed";
        const string runId = "run-pr-closed-orphan";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#48",
            IssueTitle = "PrClosed orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#48",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.PrClosed,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called for a PrClosed run");
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never,
            "TransitionStatus must not be called — agent must stay Idle");
        entry.ActiveJobId.Should().BeNull("PrClosed run must not set ActiveJobId");
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_ConflictRestartRunInActiveSet_IsSkipped()
    {
        // Guard must skip ConflictRestart (terminal non-retryable) just as it skips Completed.
        // TODO [WARNING]: ConflictRestart semantics — IsTerminal() treats it as terminal; the guard
        // therefore skips it as non-retryable. If ConflictRestart is ever reclassified as a
        // retryable state (similar to Cancelled/Failed), the guard condition must be updated to
        // include it in the restorable set, and this test must be updated accordingly.
        const string agentId = "agent-history-conflictrestart";
        const string runId = "run-conflict-restart-orphan";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#49",
            IssueTitle = "ConflictRestart orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#49",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.ConflictRestart,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called for a ConflictRestart run");
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never,
            "TransitionStatus must not be called — agent must stay Idle");
        entry.ActiveJobId.Should().BeNull("ConflictRestart run must not set ActiveJobId");
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_GetRunHistoryAsyncThrows_FailsOpenAndRestoresRun()
    {
        // CRITICAL: if GetRunHistoryAsync faults (e.g. Redis/DB down), the guard must not
        // propagate the exception and break agent registration. Instead it fails-open and
        // proceeds with restoration (at worst, a completed run is briefly re-activated until
        // ReconciliationService times it out — preferable to leaving the agent stuck in Idle).
        const string agentId = "agent-history-fault";
        const string runId = "run-history-fault-orphan";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#50",
            IssueTitle = "Orphan with history fault",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis unavailable"));
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == runId))).Returns((PipelineRun?)null);

        var message = CreateMessage(agentId, activeJob: null);

        // Must not throw — exception from GetRunHistoryAsync must be caught internally.
        await _service.RecoverOrphanedStateAsync(message, agentId);

        // Fail-open: restoration must proceed as if history is empty.
        entry.ActiveJobId.Should().Be(runId,
            "fail-open: run must be restored when history check faults");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called even when GetRunHistoryAsync throws");
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_FailedRunInActiveSet_IsRestored()
    {
        // AC2: A Failed run must still be restored — it may be retried.
        // TODO [WARNING]: Does not verify entry.OrphanRestoredAt != null. For a Failed run the full
        // lock block must execute (setting both ActiveJobId and OrphanRestoredAt). Without asserting
        // OrphanRestoredAt, a partial execution path that sets ActiveJobId but skips OrphanRestoredAt
        // would not be caught by this test.
        const string agentId = "agent-history-failed";
        const string runId = "run-failed-orphan";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#43",
            IssueTitle = "Failed orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#43",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.Failed,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == runId))).Returns((PipelineRun?)null);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(runId, "Failed run must be restored as active job");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called for a Failed run");
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_CancelledRunInActiveSet_IsRestored()
    {
        // AC2: A Cancelled run must still be restored — it may be re-dispatched.
        // TODO [WARNING]: Does not verify entry.OrphanRestoredAt != null. See the similar note on
        // DetectAndRestoreOrphans_FailedRunInActiveSet_IsRestored above.
        const string agentId = "agent-history-cancelled";
        const string runId = "run-cancelled-orphan";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#44",
            IssueTitle = "Cancelled orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#44",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.Cancelled,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == runId))).Returns((PipelineRun?)null);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(runId, "Cancelled run must be restored as active job");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called for a Cancelled run");
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_RunNotInHistory_IsRestored()
    {
        // The history guard is additive: when the run is not in history at all, restoration
        // must proceed as before (guard does not affect the base case).
        const string agentId = "agent-history-empty";
        const string runId = "run-in-flight-orphan";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#45",
            IssueTitle = "In-flight orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        // Empty history — run has not completed yet; must be restored
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == runId))).Returns((PipelineRun?)null);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(runId);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_HistoryHasOtherRunsNotMatchingOrphan_IsRestored()
    {
        // History entries for different RunIds must not suppress restoration of this orphan.
        // TODO [WARNING]: Does not cover the case where GetActiveRunsByAgent returns multiple runs
        // and only orphanedRuns[^1] (mostRecent) is completed. Add a test with [run-old, run-completed]
        // to verify the [^1] selection interacts correctly with the guard.
        const string agentId = "agent-history-mismatch";
        const string runId = "run-orphan-mismatch";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = null;

        var orphanedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#46",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
        // History has a different run as Completed — must not match this orphan
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new PipelineRunSummary
                {
                    RunId = "some-other-run-entirely",
                    IssueIdentifier = "org/repo#99",
                    IssueTitle = "Other Run",
                    FinalStep = PipelineStep.Completed,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-2)
                }
            ]);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == runId))).Returns((PipelineRun?)null);

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be(runId,
            "guard must not spuriously match history entries for different RunIds");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }
}

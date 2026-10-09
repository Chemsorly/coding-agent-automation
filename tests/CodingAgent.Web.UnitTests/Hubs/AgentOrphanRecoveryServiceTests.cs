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

        var result = await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be(agentId);
        entry.ActiveJobId.Should().Be(runId);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
        _mockFacade.Verify(f => f.ReplaceRun(existingRun), Times.Once,
            "the agent must be written back to the run — GetRun returns a copy under the distributed run service");
        result.FirstPickupRun.Should().BeSameAs(existingRun,
            "the run had no agent, so this is its first pickup and the hub moves its label to in-progress");
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

        var result = await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be(agentId);
        entry.ActiveJobId.Should().Be(runId);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
        _mockFacade.Verify(f => f.ReplaceRun(It.IsAny<PipelineRun>()), Times.Never,
            "a same-agent reconnect changes nothing on the run, so nothing is written back");
        result.FirstPickupRun.Should().BeNull("a same-agent reconnect is not a first pickup");
    }

    // ── Active job: run already in memory (pod replacement — different agent reconnects) → updates AgentId

    [Fact]
    public async Task ActiveJob_RunInMemoryOwnedByDifferentAgent_UpdatesAgentId()
    {
        // Pod replacement: a new agent pod registers with the same RunId but a different AgentId.
        // LinkAgentToExistingRun must update run.AgentId, write it back, and link the new agent to
        // the run — registration records the agent on a run only here, after the claim is accepted.
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

        var result = await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be(agentId, "pod replacement must update run.AgentId to the new agent");
        entry.ActiveJobId.Should().Be(runId, "the new agent must be linked to the run");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "the new agent must be transitioned to Busy");
        _mockFacade.Verify(f => f.ReplaceRun(existingRun), Times.Once, "the new agent must be written back to the run");
        result.FirstPickupRun.Should().BeNull(
            "pod replacement is not a first pickup — the label was moved to in-progress when the first pod picked the run up");
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

    // ── Restored issue URL: reported by the agent, rendered by the UI as a link ─────────

    [Theory]
    [InlineData("https://github.com/org/repo/issues/42", "https://github.com/org/repo/issues/42")]
    [InlineData("http://gitlab.internal/group/repo/-/issues/42", "http://gitlab.internal/group/repo/-/issues/42")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("issues/42", null)]
    public async Task ActiveJob_RunNotInMemory_RestoresOnlyAnHttpIssueUrl(string reportedUrl, string? expectedUrl)
    {
        const string agentId = "agent-1";
        const string runId = "run-issue-url";
        var entry = CreateEntry(agentId);
        _mockFacade.Setup(f => f.GetRun(runId)).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);
        PipelineRun? restored = null;
        _mockFacade.Setup(f => f.AddRun(It.IsAny<PipelineRun>())).Callback<PipelineRun>(r => restored = r);

        var activeJob = CreateActiveJob(runId) with { IssueUrl = reportedUrl };
        await _service.RecoverOrphanedStateAsync(CreateMessage(agentId, activeJob), agentId);

        restored.Should().NotBeNull();
        restored!.IssueUrl.Should().Be(expectedUrl);
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

        var result = await _service.RecoverOrphanedStateAsync(CreateMessage(agentId, CreateActiveJob(runId)), agentId);

        existingRun.AgentId.Should().Be(owner, "the run's work item belongs to another agent");
        entry.ActiveJobId.Should().BeNull();
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Never);
        _mockFacade.Verify(f => f.ReplaceRun(It.IsAny<PipelineRun>()), Times.Never);
        result.FirstPickupRun.Should().BeNull("the hub must not move the issue label for a rejected claim");
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

    /// <summary>
    /// An agent without an active job gets the run it is recorded on re-attached only when that
    /// run's work item is its own.
    /// </summary>
    [Fact]
    public async Task VerifiedOrphanDetection_RunOfAnotherAgentsWorkItem_IsNotRestored()
    {
        const string agentId = "caa-ffff6666";
        const string runId = "run-recorded-on-agent";
        var trackedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };
        var entry = CreateEntry(agentId);
        WithWorkItemStore(runId, OwnedRecord("caa-0000aaaa"));
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([trackedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await _service.RecoverOrphanedStateAsync(CreateMessage(agentId, activeJob: null), agentId);

        entry.ActiveJobId.Should().BeNull();
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Never);
    }

    [Fact]
    public async Task VerifiedOrphanDetection_OwnWorkItem_IsRestored()
    {
        const string agentId = "caa-1111bbbb";
        const string runId = "run-own-orphan";
        var trackedRun = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };
        var entry = CreateEntry(agentId);
        WithWorkItemStore(runId, OwnedRecord(agentId));
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([trackedRun]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await _service.RecoverOrphanedStateAsync(CreateMessage(agentId, activeJob: null), agentId);

        entry.ActiveJobId.Should().Be(runId);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    /// <summary>
    /// A work item that cannot be read is not taken as "not the agent's": registration fails with an
    /// error the agent's SignalR retry pipeline retries ("Failed to ..."), instead of leaving the agent
    /// registered without its run.
    /// </summary>
    [Fact]
    public async Task VerifiedClaim_StoreUnavailable_FailsRegistrationWithARetryableError()
    {
        const string agentId = "caa-2222cccc";
        const string runId = "run-store-down";
        var entry = CreateEntry(agentId);
        _mockFacade.SetupGet(f => f.CanVerifyWorkItems).Returns(true);
        _mockFacade.Setup(f => f.GetWorkItemRunRecordAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        var act = () => _service.RecoverOrphanedStateAsync(CreateMessage(agentId, CreateActiveJob(runId)), agentId);

        (await act.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>())
            .Which.Message.Should().StartWith("Failed to ");
        entry.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task VerifiedOrphanDetection_StoreUnavailable_FailsRegistrationWithARetryableError()
    {
        const string agentId = "caa-3333dddd";
        var trackedRun = new PipelineRun
        {
            RunId = "run-orphan-store-down",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AgentId = agentId
        };
        var entry = CreateEntry(agentId);
        _mockFacade.SetupGet(f => f.CanVerifyWorkItems).Returns(true);
        _mockFacade.Setup(f => f.GetWorkItemRunRecordAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([trackedRun]);

        var act = () => _service.RecoverOrphanedStateAsync(CreateMessage(agentId, activeJob: null), agentId);

        (await act.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>())
            .Which.Message.Should().StartWith("Failed to ");
        entry.ActiveJobId.Should().BeNull();
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

    private static AgentId MakeAgentId(string id = "agent-1") => new(id);

    private static AgentEntry MakeEntry(string agentId = "agent-1") =>
        new()
        {
            AgentId = new AgentId(agentId),
            ConnectionId = $"conn-{agentId}",
            Hostname = "test-host",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };

    private static AgentRegistrationMessage EmptyMessage(string agentId = "agent-1") =>
        new()
        {
            AgentId = new AgentId(agentId),
            Hostname = "test-host",
            Labels = [],
            ActiveJob = null
        };

    private static ActiveJobState MakeActiveJob(
        string runId = "run-1",
        string issueId = "GH-42",
        PipelineRunType runType = PipelineRunType.Implementation,
        string providerConfigId = "github",
        string? modelName = null) =>
        new()
        {
            RunId = runId,
            IssueIdentifier = issueId,
            IssueTitle = "Test issue",
            IssueProviderConfigId = providerConfigId,
            RepoProviderConfigId = "github-repo",
            AgentProviderConfigId = "kiro",
            RunType = runType,
            StartedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test",
            CurrentStep = PipelineStep.Created,
            ModelName = modelName,
            RepositoryName = "my-repo"
        };

    private static AgentRegistrationMessage MessageWithJob(string agentId = "agent-1",
        ActiveJobState? job = null) =>
        new()
        {
            AgentId = new AgentId(agentId),
            Hostname = "test-host",
            Labels = [],
            ActiveJob = job
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

    [Fact]
    public async Task RecoverOrphanedStateAsync_OrphanDetection_DoesNotOverwriteExistingActiveJob_DoesNotCallAddRun()
    {
        // When entry.ActiveJobId is already set (already-assigned guard fires), AddRun must NOT be called.
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        entry.ActiveJobId = "already-assigned";
        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "orphan-1",
            IssueIdentifier = "GH-1",
            IssueTitle = "T",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);

        await _service.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        entry.ActiveJobId.Should().Be("already-assigned");
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when the existing active job guard prevents orphan restore");
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
            .Setup(f => f.GetWorkItemRunRecordAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#42",
                IssueProviderConfigId = "issue-cfg-1",
                RepoProviderConfigId = "repo-cfg-1",
                BrainProviderConfigId = "brain-cfg-1",
                ProjectId = null,
            });

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
        // When GetRun returns null AND GetWorkItemRunRecordAsync also returns null,
        // AddRun must NOT be called — nothing recoverable from DB either.
        const string agentId = "agent-crash-nodb";
        const string existingJobId = "00000000-0000-0000-0000-000000000002";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == existingJobId))).Returns((PipelineRun?)null);
        _mockFacade
            .Setup(f => f.GetWorkItemRunRecordAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkItemRunRecord?)null);

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
            .Setup(f => f.GetWorkItemRunRecordAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "org/repo#42",
                IssueProviderConfigId = "issue-cfg-1",
                RepoProviderConfigId = null,   // missing — triggers skip
                BrainProviderConfigId = null,
                ProjectId = null,
            });

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.OrphanRestoredAt.Should().NotBeNull();
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when RepoProviderConfigId cannot be recovered from DB Payload");
    }

    [Fact]
    public async Task NoActiveJob_RegistryHasActiveJobId_CrashRecovery_ReconstructsDecompositionRunWithProjectId()
    {
        // AC#1: Rebuilding a decomposition WorkItem gives a run with the WorkItem's ProjectId
        // and the decomposition run type (DecompositionAnalysis — the Phase-1 default from ToDefaultRunType).
        const string agentId = "agent-crash-decomp";
        const string existingJobId = "00000000-0000-0000-0000-000000000004";
        var projectId = Guid.NewGuid();

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == existingJobId))).Returns((PipelineRun?)null);
        _mockFacade
            .Setup(f => f.GetWorkItemRunRecordAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Decomposition,
                IssueIdentifier = "org/repo#10",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                BrainProviderConfigId = "brain-1",
                ProjectId = projectId,
            });

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.OrphanRestoredAt.Should().NotBeNull();
        _mockFacade.Verify(
            f => f.AddRun(It.Is<PipelineRun>(r =>
                r.RunId == existingJobId &&
                r.RunType == PipelineRunType.DecompositionAnalysis &&
                r.ProjectId == projectId.ToString())),
            Times.Once,
            "Rebuilt decomposition run must carry RunType=DecompositionAnalysis and the WorkItem's ProjectId");
    }

    [Fact]
    public async Task NoActiveJob_RegistryHasActiveJobId_CrashRecovery_ReconstructsReviewRunWithPullRequestLabelTargetKind()
    {
        // AC#2: Rebuilding a review WorkItem gives LabelTargetKind.PullRequest.
        // LabelTargetKind is derived from RunType: only Review → PullRequest; all others → Issue.
        const string agentId = "agent-crash-review";
        const string existingJobId = "00000000-0000-0000-0000-000000000005";

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == existingJobId))).Returns((PipelineRun?)null);
        _mockFacade
            .Setup(f => f.GetWorkItemRunRecordAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemRunRecord
            {
                TaskType = WorkItemTaskType.Review,
                IssueIdentifier = "org/repo#20",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                BrainProviderConfigId = null,
                ProjectId = null,
            });

        var message = CreateMessage(agentId, activeJob: null);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.OrphanRestoredAt.Should().NotBeNull();
        _mockFacade.Verify(
            f => f.AddRun(It.Is<PipelineRun>(r =>
                r.RunId == existingJobId &&
                r.RunType == PipelineRunType.Review &&
                r.LabelTargetKind == LabelTargetKind.PullRequest)),
            Times.Once,
            "Rebuilt review run must carry RunType=Review and LabelTargetKind=PullRequest");
    }

    [Fact]
    public async Task NoActiveJob_RegistryHasActiveJobId_CrashRecovery_PropagatesOperationCanceledException()
    {
        // When GetWorkItemRunRecordAsync throws OperationCanceledException, it must propagate
        // out of TryReconstructRunFromDbAsync rather than being swallowed and returning null.
        // This is the prerequisite characterization test for the OCE filter fix (issue #3238).
        // TODO: [WARNING] The mock throws OperationCanceledException regardless of token value, and
        // CancellationToken.None is hardcoded in the production call (AgentOrphanRecoveryService.cs:692).
        // This means the test verifies that any OCE thrown by the dependency propagates, rather than
        // demonstrating the token-cancellation causal chain stated in AC#2 ("a cancelled CancellationToken
        // causes OCE to propagate"). The test remains an effective regression guard for the catch-filter
        // change, but does not verify the causal relationship between a cancelled token and the exception.
        const string agentId = "agent-crash-oce";
        const string existingJobId = "00000000-0000-0000-0000-000000000006"; // valid GUID required

        var entry = CreateEntry(agentId);
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == existingJobId))).Returns((PipelineRun?)null);
        _mockFacade
            .Setup(f => f.GetWorkItemRunRecordAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var message = CreateMessage(agentId, activeJob: null);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.RecoverOrphanedStateAsync(message, agentId));
    }

    // ── HandleCrashRecovery: hash gone → AddRun NOT called ────────────────────────

    // TODO: [WARNING] AC#3 (RequestCreateIssueForProvider on a run without a project rejects any
    // provider other than the run's own) is covered by the pre-existing test
    // RequestCreateIssueForProvider_EmptyProjectId_RejectsOtherProvider in
    // AgentHubIssueProxyTests.cs. That test already exercises the correct rejection path.
    // However, no new test was added in this diff that specifically demonstrates the regression: a
    // run rebuilt from DB without a ProjectId bypassing the scope check. Consider adding a test that
    // reconstructs an Implementation WorkItem (ProjectId = null), then calls RequestCreateIssueForProvider
    // with a foreign provider config and asserts it is rejected with a HubException.

    // TODO: [WARNING] The decomposition reconstruction test only covers WorkItemTaskType.Decomposition →
    // PipelineRunType.DecompositionAnalysis. The switch arm for PipelineRunType.Decomposition (Phase 2)
    // is unreachable via ToDefaultRunType and has no test. If future enum additions introduce a
    // Phase-2 task type, add a test for it here. Also consider asserting r.ProjectId == null in the
    // review test to guard against a regression where null ProjectId is mapped to "" or another
    // non-null sentinel.

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

    // ── Guard: null message ───────────────────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_NullMessage_Throws()
    {
        var act = () => _service.RecoverOrphanedStateAsync(null!, MakeAgentId());
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_NullAgentIdValue_Throws()
    {
        var act = () => _service.RecoverOrphanedStateAsync(EmptyMessage(), new AgentId(null!));
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── No active job, no orphaned runs ──────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_NoActiveJob_NoOrphans_DoesNothing()
    {
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        await _service.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── Link to existing run ──────────────────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_ExistingRunWithNullAgentId_LinksAgent()
    {
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1");
        var message = MessageWithJob(job: activeJob);

        // Run exists in memory but not yet linked to any agent (K8s dispatch path)
        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-1",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = null, // unlinked
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(new JobId("run-1"))).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be("agent-1");
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_ExistingRunWithDifferentAgentId_UpdatesAgentId()
    {
        // Pod replacement: a new agent pod calls RecoverOrphanedStateAsync with the same RunId
        // but a different AgentId than the one currently on the run.
        // LinkAgentToExistingRun must update run.AgentId and link the agent.
        // NOTE: This tests the service in isolation (RecoverOrphanedStateAsync called directly,
        // without a preceding RegisterAgent call). In the combined production flow, RegisterAgent
        // updates run.AgentId first, so by the time this code runs existingRun.AgentId already
        // matches — making it a no-op. The isolation test here verifies correct independent behaviour.
        // entry.ActiveJobId is null so the inner trackedEntry.ActiveJobId is null lock guard is
        // satisfied, allowing TransitionStatus(Busy) to be called.
        var agentId = MakeAgentId("agent-1");
        var activeJob = MakeActiveJob("run-1");
        var message = MessageWithJob(job: activeJob);

        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-1",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = "agent-old", // prior pod's identity — different from registering agent
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        var entry = MakeEntry("agent-1");
        // entry.ActiveJobId is null (default from MakeEntry) — satisfies the inner lock guard

        _mockFacade.Setup(f => f.GetRun(new JobId("run-1"))).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be("agent-1", "pod replacement must update run.AgentId to the new agent");
        entry.ActiveJobId.Should().Be("run-1", "the new agent must be linked to the run");
        _mockFacade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never, "existing run must not be re-added");
        // TODO: [WARNING] TransitionStatus(agentId, AgentStatus.Busy) is not verified here, but the
        // test comment explicitly notes the setup satisfies the inner lock guard so it will be called.
        // A silent removal of the TransitionStatus call would not be caught. The analogous Web.UnitTests
        // counterpart does verify TransitionStatus(Busy) Times.Once. Consider adding:
        // _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
        //     "the new agent must be transitioned to Busy");
        // TODO: [WARNING] The acceptance criterion "A log entry is emitted when AgentId is updated due
        // to pod replacement" is not verified here. The _mockLogger mock is available in this test class;
        // a silent removal of the pod-replacement Information log line would not be caught by any test
        // on the Pipeline.UnitTests recovery-service path.
    }

    // ── Crash recovery ────────────────────────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_CrashRecovery_WhenAgentHasActiveJob_LogsInfo()
    {
        // When message.ActiveJob is NOT null (agent has a job, entry also has a job), just logs info
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();
        entry.ActiveJobId = "run-1";

        _mockFacade.Setup(f => f.GetRun(new JobId("run-1"))).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        // Should not throw
        var act = () => _service.RecoverOrphanedStateAsync(message, agentId);
        await act.Should().NotThrowAsync();
    }

    // ── RestoreConsolidationTracking: call order — TransitionStatus before UpdateAgentFieldAsync ──

    [Fact]
    public async Task RestoreConsolidationTracking_CallOrder_TransitionStatusBeforeUpdateAgentField()
    {
        // AC: TransitionStatus must be called before UpdateAgentFieldAsync (not inside the lock).
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1",
            providerConfigId: ConsolidationConstants.ProviderConfigId);
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        var callOrder = new List<string>();
        _mockFacade.Setup(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()))
            .Callback(() => callOrder.Add("TransitionStatus"));
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback(() => callOrder.Add("UpdateAgentFieldAsync"))
            .Returns(Task.CompletedTask);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        // TODO: [WARNING] AC3 requires asserting the full order: in-memory mutation → TransitionStatus →
        // UpdateAgentFieldAsync. This test only captures TransitionStatus and UpdateAgentFieldAsync in
        // callOrder; the in-memory mutation (consolEntry.ActiveJobId assignment) is never asserted as
        // having occurred before TransitionStatus. To fully satisfy AC3, capture the mutation as an
        // ordered event, e.g. by recording entry.ActiveJobId inside the TransitionStatus callback and
        // asserting it was already set at that point.
        // TODO: [WARNING] ContainInOrder checks for a subsequence, not an exclusive sequence. If
        // UpdateAgentFieldAsync were called first and TransitionStatus were called again later by
        // another code path, this assertion would still pass. For a stricter ordering guarantee,
        // replace with: callOrder.Should().Equal(new[] { "TransitionStatus", "UpdateAgentFieldAsync" })
        // (exact sequence equality) and/or add: callOrder.Should().HaveCount(2).
        // TODO: [WARNING] Missing call-count assertions. Without explicit Times.Once verification for
        // TransitionStatus and UpdateAgentFieldAsync, this test would pass even if UpdateAgentFieldAsync
        // were called twice (e.g., if the old inside-lock call were accidentally re-introduced alongside
        // the new outside-lock call). Add:
        //   _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Once);
        //   _mockFacade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
        callOrder.Should().ContainInOrder("TransitionStatus", "UpdateAgentFieldAsync");
    }

    [Fact]
    public async Task RestoreConsolidationTracking_UpdateAgentFieldAsync_CalledWithCorrectArgs()
    {
        // AC: UpdateAgentFieldAsync must be called with (agentId, "activeJobId", runId).
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-consol-args",
            providerConfigId: ConsolidationConstants.ProviderConfigId);
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.UpdateAgentFieldAsync(agentId, "activeJobId", "run-consol-args"), Times.Once,
            "UpdateAgentFieldAsync must be called with the correct agentId, field name, and runId");
    }

    [Fact]
    public async Task RestoreConsolidationTracking_WhenTOCTOUGuardFails_UpdateAgentFieldAsyncNotCalled()
    {
        // When the guard suppresses execution (consolEntry is null), neither TransitionStatus
        // nor UpdateAgentFieldAsync should be called. This verifies that UpdateAgentFieldAsync
        // is only called when TransitionStatus fires (i.e., inside the same TOCTOU guard branch).
        // A genuine concurrent-disconnect TOCTOU race (ActiveJobId cleared between lock-release
        // and the guard check) is not deterministically unit-testable without a test seam; the
        // null-entry path tests the equivalent behavioral invariant: when the guard cannot pass,
        // neither downstream call fires.
        // TODO: [WARNING] This test exercises the null-entry outer guard (consolEntry is null),
        // not the actual TOCTOU inner guard (consolEntry.ActiveJobId == writtenJobId). A regression
        // where UpdateAgentFieldAsync is called when the TOCTOU guard fails but the entry is non-null
        // would not be caught here. To cover the actual TOCTOU guard, a test seam that clears
        // ActiveJobId between lock release and the guard check is needed (e.g., via a callback on
        // a mock that modifies the entry in-flight). Rename test to
        // RestoreConsolidationTracking_WhenNullEntryGuardFails_... to accurately reflect what is tested.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-toctou-consol",
            providerConfigId: ConsolidationConstants.ProviderConfigId);
        var message = MessageWithJob(job: activeJob);

        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        // Return null for consolEntry — the if (consolEntry is not null) guard fails,
        // so the TOCTOU guard and both downstream calls are suppressed.
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns((AgentEntry?)null);
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()),
            Times.Never,
            "TransitionStatus must not be called when consolEntry is null");
        _mockFacade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never,
            "UpdateAgentFieldAsync must not be called when the guard suppresses both calls");
    }

    // ── RestorePipelineRun: call order — TransitionStatus before UpdateAgentFieldAsync ──

    [Fact]
    public async Task RestorePipelineRun_CallOrder_TransitionStatusBeforeUpdateAgentField()
    {
        // AC: TransitionStatus must be called before UpdateAgentFieldAsync (not inside the lock).
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-pipeline-order");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        var callOrder = new List<string>();
        _mockFacade.Setup(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()))
            .Callback(() => callOrder.Add("TransitionStatus"));
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback(() => callOrder.Add("UpdateAgentFieldAsync"))
            .Returns(Task.CompletedTask);

        await _service.RecoverOrphanedStateAsync(message, agentId);
        // TODO: [WARNING] AC3 requires asserting the full order: in-memory mutation → TransitionStatus →
        // UpdateAgentFieldAsync. This test only captures TransitionStatus and UpdateAgentFieldAsync in
        // callOrder; the in-memory mutation (restoredEntry.ActiveJobId assignment) is never asserted as
        // having occurred before TransitionStatus. To fully satisfy AC3, capture the mutation as an
        // ordered event, e.g. by recording entry.ActiveJobId inside the TransitionStatus callback and
        // asserting it was already set at that point.
        // TODO: [WARNING] ContainInOrder checks for a subsequence, not an exclusive sequence. If
        // UpdateAgentFieldAsync were called first and TransitionStatus were called again later by
        // another code path, this assertion would still pass. For a stricter ordering guarantee,
        // replace with: callOrder.Should().Equal(new[] { "TransitionStatus", "UpdateAgentFieldAsync" })
        // (exact sequence equality) and/or add: callOrder.Should().HaveCount(2).
        // TODO: [WARNING] Missing call-count assertions. Without explicit Times.Once verification for
        // TransitionStatus and UpdateAgentFieldAsync, this test would pass even if UpdateAgentFieldAsync
        // were called twice (e.g., if the old inside-lock call were accidentally re-introduced alongside
        // the new outside-lock call). Add:
        //   _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Once);
        //   _mockFacade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
        callOrder.Should().ContainInOrder("TransitionStatus", "UpdateAgentFieldAsync");
    }

    [Fact]
    public async Task RestorePipelineRun_UpdateAgentFieldAsync_CalledWithCorrectArgs()
    {
        // AC: UpdateAgentFieldAsync must be called with (agentId, "activeJobId", runId).
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-pipeline-args");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.UpdateAgentFieldAsync(agentId, "activeJobId", "run-pipeline-args"), Times.Once,
            "UpdateAgentFieldAsync must be called with the correct agentId, field name, and runId");
    }

    [Fact]
    public async Task RestorePipelineRun_WhenTOCTOUGuardFails_UpdateAgentFieldAsyncNotCalled()
    {
        // When the TOCTOU guard fails (entry is null — simulating the guard suppressing both calls),
        // UpdateAgentFieldAsync must not be called. This verifies that UpdateAgentFieldAsync is
        // only called when TransitionStatus fires (i.e., under the same TOCTOU guard).
        // TODO: [WARNING] This test exercises the null-entry outer guard (restoredEntry is null),
        // not the actual TOCTOU inner guard (restoredEntry.ActiveJobId == writtenJobId). A regression
        // where UpdateAgentFieldAsync is called when the TOCTOU guard fails but the entry is non-null
        // would not be caught here. To cover the actual TOCTOU guard, a test seam that clears
        // ActiveJobId between lock release and the guard check is needed (e.g., via a callback on
        // a mock that modifies the entry in-flight). Rename test to
        // RestorePipelineRun_WhenNullEntryGuardFails_... to accurately reflect what is tested.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-toctou-pipeline");
        var message = MessageWithJob(job: activeJob);

        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        // Return null for restoredEntry — simulates the guard failing (entry not found).
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns((AgentEntry?)null);
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        // AddRun is still called (it happens before the entry guard)
        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == "run-toctou-pipeline")), Times.Once);
        // Neither TransitionStatus nor UpdateAgentFieldAsync should fire
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()),
            Times.Never,
            "TransitionStatus must not be called when restoredEntry is null");
        _mockFacade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never,
            "UpdateAgentFieldAsync must not be called when the TOCTOU guard suppresses both calls");
    }

    // ── Concurrent-disconnect scenarios (issue #2662) ─────────────────────────────────────

    // These tests verify the fix for the TOCTOU race in all four affected sites.
    // In the old code, entry.ActiveJobId was read outside SyncRoot to gate TransitionStatus.
    // A concurrent disconnect handler clearing ActiveJobId between lock release and that read
    // could suppress TransitionStatus or fire it on a Disconnected agent.
    //
    // The fix captures `bool shouldTransition` inside the lock. The correctness guarantee is
    // structural: shouldTransition is a stack-local bool set inside the lock and cannot be
    // cleared by a concurrent disconnect handler after the lock is released.
    //
    // Testability note for RestorePipelineRun and RestoreConsolidationTracking:
    // Both methods set shouldTransition = true unconditionally inside the lock with no mock
    // call site between lock-release and `if (shouldTransition)`. There is therefore no
    // injection point to simulate a concurrent disconnect in a single-threaded unit test.
    // The concurrent-disconnect correctness guarantee for these two sites is structural
    // (shouldTransition is a stack-local bool, not a shared field) and is verified by code
    // inspection rather than by executable test. The tests below verify the happy-path
    // behavioral contract only.
    //
    // For LinkAgentToExistingRun, the UpdateAgentFieldAsync call inside the lock provides a
    // feasible injection point: the Moq callback clears ActiveJobId while the lock is held (after
    // the field is written), so the old post-lock check (entry.ActiveJobId == activeJob.RunId)
    // reads null and skips TransitionStatus; the new shouldTransition flag was already set before
    // the callback fired, so TransitionStatus is called — this test genuinely distinguishes old
    // from new.
    // For DetectAndRestoreOrphans, the AddRun callback fires after the lock is released (AddRun is
    // called inside `if (shouldTransition)` which is after the lock block), which similarly
    // distinguishes old from new code.

    [Fact]
    public async Task LinkAgentToExistingRun_ConcurrentDisconnectClearsActiveJobId_StillCallsTransitionStatus()
    {
        // Scenario: LinkAgentToExistingRun sets ActiveJobId under lock (when null), then a
        // concurrent disconnect clears it. Old code: unsynchronized read `trackedEntry.ActiveJobId
        // == activeJob.RunId` → null == "run-1" → false → TransitionStatus skipped.
        // New code: shouldTransition flag set inside lock → TransitionStatus always called.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-concurrent-link");
        var message = MessageWithJob(job: activeJob);

        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-concurrent-link",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = null,
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        var entry = MakeEntry();
        // entry.ActiveJobId is null — the inner lock guard sets it and shouldTransition = true

        _mockFacade.Setup(f => f.GetRun(new JobId("run-concurrent-link"))).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        // Set up GetActiveRunsByAgent in case DetectAndRestoreOrphans is reached after the callback
        // clears entry.ActiveJobId. We return empty to keep the test focused on LinkAgentToExistingRun.
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback(() =>
            {
                // Simulate disconnect clearing ActiveJobId after the lock releases.
                // With the old pattern: the post-lock check `trackedEntry.ActiveJobId == activeJob.RunId`
                // would read null (cleared by this callback) and skip TransitionStatus.
                // With the new shouldTransition flag: the flag was already set inside the lock.
                //
                // TODO: [WARNING] In the new code, UpdateAgentFieldAsync is called INSIDE
                // lock(trackedEntry.SyncRoot) (the call is within the `if (trackedEntry.ActiveJobId
                // is null)` branch inside the lock block). This callback therefore fires while the
                // lock is still held, not after lock release. The comment above ("after the lock
                // releases") is inaccurate for the new code. In the old code, UpdateAgentFieldAsync
                // was called outside the lock, so the callback fired after lock release — the two
                // behaviors have different injection timing. The test still correctly distinguishes
                // old from new (shouldTransition was set before the callback clears ActiveJobId),
                // but the simulated scenario does not model the documented post-lock-release race
                // window; it models a mid-lock disconnect instead.
                entry.ActiveJobId = null;
            })
            .Returns(Task.CompletedTask);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called; the shouldTransition flag was set inside the lock");
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_ConcurrentDisconnectClearsActiveJobId_StillCallsTransitionStatus()
    {
        // Scenario: DetectAndRestoreOrphans sets ActiveJobId under lock (shouldTransition = true),
        // then a concurrent disconnect clears ActiveJobId. Old code: post-lock read
        // `entry.ActiveJobId == mostRecent.RunId` → null == "run-orphan" → false → skipped.
        // New code: shouldTransition flag set inside lock → TransitionStatus always called.
        //
        // TODO: [WARNING] The AddRun callback fires inside `if (shouldTransition)` which is after
        // `if (shouldTransition)` has already been evaluated — the branch decision precedes AddRun.
        // A disconnect simulated here cannot affect whether TransitionStatus is called (the branch
        // is already taken). In the old code, the post-lock guard `entry.ActiveJobId == mostRecent.RunId`
        // was evaluated before AddRun, so clearing ActiveJobId in an AddRun callback would not have
        // affected the old guard either. This test therefore does not demonstrate that the old code
        // would have failed here. The test passes for the right behavioral reason (shouldTransition
        // was set inside the lock) but cannot serve as a regression detector for the specific
        // TOCTOU scenario described in the issue. Correctness is verified by code inspection of
        // the lock boundary in DetectAndRestoreOrphans rather than by this test.
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-orphan-concurrent",
            IssueIdentifier = "GH-42",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // GetRun returns null so AddRun is called; we simulate the disconnect inside AddRun.
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "run-orphan-concurrent")))
            .Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.AddRun(It.IsAny<PipelineRun>()))
            .Callback(() =>
            {
                // Simulate a concurrent disconnect clearing ActiveJobId after the lock releases.
                // With the old pattern: `entry.ActiveJobId == mostRecent.RunId` would read null
                // (cleared here) → false → TransitionStatus skipped.
                // With the new shouldTransition flag: it was set inside the lock, so transition fires.
                entry.ActiveJobId = null;
            });

        await _service.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called; the shouldTransition flag was set inside the lock");
    }

    // ── Fault-logging: faulted UpdateAgentFieldAsync is observed ─────────────────

    // TODO: [WARNING] All five tests below use a TCS-backed Callback to synchronise with the
    // ContinueWith continuation. If the _mockLogger.Warning mock setup does not match the actual
    // overload dispatched by Serilog (e.g. params object[] instead of the typed generic overload),
    // the Callback is never invoked and the test hangs for 30 s before timing out — rather than
    // failing with a clear assertion message. Consider a fallback assertion independent of the TCS
    // to surface this failure sooner. See TestQualityReviewer findings for issue #2779.
    //
    // TODO: [WARNING] All five tests assert Times.AtLeastOnce on _mockLogger.Warning, which passes even
    // if a different Warning call (unrelated to UpdateAgentFieldAsync faults) fires first. For
    // DetectAndRestoreOrphans in particular, the orphan-count Warning at ~L431 shares the same
    // overload signature and would satisfy the assertion even if the ContinueWith continuation never
    // fired. A more precise assertion (e.g. verifying the logged exception is the specific
    // InvalidOperationException from the faulted task) would lock in the requirement more tightly.
    // See TestQualityReviewer findings for issue #2779.

    [Fact]
    public async Task RestoreConsolidationTracking_FaultedUpdateAgentFieldAsync_LogsWarning()
    {
        // Arrange: UpdateAgentFieldAsync faults — the ContinueWith continuation must log a Warning.
        // TCS synchronizes the test thread with the ThreadPool-scheduled continuation:
        // ContinueWith(TaskScheduler.Default) is always async even for already-faulted tasks.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-consol-fault",
            providerConfigId: ConsolidationConstants.ProviderConfigId);
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        // The field write faults — this is the call whose fault must be logged.
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(agentId, "activeJobId", It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));

        // Wire the Callback before the act so the TCS is signalled as soon as the continuation fires.
        // Serilog Warning<T0,T1,T2>(Exception?, string, T0, T1, T2) — use concrete types.
        // T0 = string (callerContext), T1 = AgentId, T2 = string (field).
        _mockLogger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("RestoreConsolidationTracking")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))
            .Callback(() => warningFired.TrySetResult(true));

        // Act
        await _service.RecoverOrphanedStateAsync(message, agentId);

        // Block until the ThreadPool continuation fires (30 s safety-net).
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: Warning logged with exception and field context; no exception propagated.
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("RestoreConsolidationTracking")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.AtLeastOnce); // TODO [WARNING]: Only one UpdateAgentFieldFireAndForget call exists on this
                                // code path, so Times.Once would be tighter and detect spurious extra calls.
                                // Change to Times.Once (keep Times.AtLeastOnce only for DetectAndRestoreOrphans
                                // which intentionally has two field writes both faulting).
    }

    [Fact]
    public async Task RestorePipelineRun_FaultedUpdateAgentFieldAsync_LogsWarning()
    {
        // Arrange: UpdateAgentFieldAsync faults on the RestorePipelineRun path.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-pipeline-fault"); // non-consolidation provider
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(agentId, "activeJobId", It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));

        _mockLogger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("RestorePipelineRun")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))
            .Callback(() => warningFired.TrySetResult(true));

        await _service.RecoverOrphanedStateAsync(message, agentId);
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("RestorePipelineRun")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.AtLeastOnce); // TODO [WARNING]: Only one UpdateAgentFieldFireAndForget call exists on this
                                // code path; Times.Once would be stricter. See RestoreConsolidationTracking
                                // test comment for rationale.
    }

    [Fact]
    public async Task LinkAgentToExistingRun_FaultedUpdateAgentFieldAsync_LogsWarning()
    {
        // Arrange: UpdateAgentFieldAsync faults inside the lock(trackedEntry.SyncRoot) in
        // LinkAgentToExistingRun. The ContinueWith continuation is still queued to the
        // ThreadPool asynchronously (after the lock releases), never inline.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-link-fault");
        var message = MessageWithJob(job: activeJob);

        // Run exists but is not yet linked (AgentId = null) — triggers the ActiveJobId is null branch.
        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-link-fault",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = null,
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(new JobId("run-link-fault"))).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(agentId, "activeJobId", It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));

        _mockLogger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("LinkAgentToExistingRun")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))
            .Callback(() => warningFired.TrySetResult(true));

        await _service.RecoverOrphanedStateAsync(message, agentId);
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("LinkAgentToExistingRun")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.AtLeastOnce); // TODO [WARNING]: Only one UpdateAgentFieldFireAndForget call exists on this
                                // code path; Times.Once would be stricter. See RestoreConsolidationTracking
                                // test comment for rationale.
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_FaultedUpdateAgentFieldAsync_LogsWarning()
    {
        // Arrange: both UpdateAgentFieldAsync calls inside the lock fault.
        // Both are inside lock(entry.SyncRoot) — continuations run after lock release.
        // TCS fires on first Warning; Times.AtLeastOnce accepts one or two.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-orphan-fault",
            IssueIdentifier = "GH-42",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // Both field writes fault — either continuation satisfies the TCS.
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(agentId, It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));
        // GetRun returns null so AddRun is called (hash absent path).
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "run-orphan-fault"))).Returns((PipelineRun?)null);

        _mockLogger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("DetectAndRestoreOrphans")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))
            .Callback(() => warningFired.TrySetResult(true));

        await _service.RecoverOrphanedStateAsync(EmptyMessage(), agentId);
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("DetectAndRestoreOrphans")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task HandleCrashRecovery_FaultedUpdateAgentFieldAsync_LogsWarning()
    {
        // Arrange: UpdateAgentFieldAsync faults inside the lock in HandleCrashRecovery.
        // Entry has ActiveJobId set + OrphanRestoredAt null → crash recovery path.
        var warningFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        entry.ActiveJobId = "crash-job-1";
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(agentId, "orphanRestoredAt", It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("Redis down")));
        // GetRun returns null — hash gone, AddRun not called.
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "crash-job-1"))).Returns((PipelineRun?)null);

        _mockLogger
            .Setup(l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("HandleCrashRecoveryAsync")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()))
            .Callback(() => warningFired.TrySetResult(true));

        // message.ActiveJob = null → triggers HandleCrashRecovery.
        await _service.RecoverOrphanedStateAsync(EmptyMessage(), agentId);
        await warningFired.Task.WaitAsync(TimeSpan.FromSeconds(30));

        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("{CallerContext}") && s.Contains("{AgentId}") && s.Contains("{Field}")),
                It.Is<string>(ctx => ctx.Contains("HandleCrashRecoveryAsync")),
                It.IsAny<AgentId>(),
                It.IsAny<string>()),
            Times.AtLeastOnce); // TODO [WARNING]: Only one UpdateAgentFieldFireAndForget call exists on this
                                // code path; Times.Once would be stricter. See RestoreConsolidationTracking
                                // test comment for rationale.
    }

    // ── Cancellation token propagation (characterization tests for issue #3283) ───

    [Fact]
    public async Task RecoverOrphanedStateAsync_CancelledToken_GetRunHistoryAsync_ActiveJobPath_PropagatesCancel()
    {
        // Arrange: agent reports an active job that is not in history yet, so GetRunHistoryAsync
        // is called via RestoreRunFromAgentStateAsync. The token should reach GetRunHistoryAsync
        // so that an OperationCanceledException propagates out of RecoverOrphanedStateAsync.
        // TODO [WARNING]: The GetRunHistoryAsync mock uses It.IsAny<CancellationToken>() and always throws
        // OperationCanceledException regardless of which token is supplied. This means the test would pass
        // even if the production code still called GetRunHistoryAsync(CancellationToken.None) — it verifies
        // that OCE propagates, not that the caller's specific token is forwarded. A stronger arrangement
        // would capture the token argument via a Callback and assert it equals cts.Token after the call.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-ct-1");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(new JobId("run-ct-1"))).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _service.RecoverOrphanedStateAsync(message, agentId, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_CancelledToken_GetRunHistoryAsync_OrphanPath_PropagatesCancel()
    {
        // Arrange: agent registers without active job, orphan detection calls CheckOrphanInHistoryAsync
        // which calls GetRunHistoryAsync. An OperationCanceledException must propagate, not be
        // swallowed by the fail-open catch block.
        // TODO [WARNING]: The GetRunHistoryAsync mock uses It.IsAny<CancellationToken>() and always throws
        // OperationCanceledException regardless of which token is supplied. A regression restoring
        // CancellationToken.None would still make this test pass. To strengthen, capture the token via a
        // Callback and assert it equals cts.Token.
        // TODO [WARNING]: _mockFacade.CanVerifyWorkItems is not set up explicitly; Moq defaults to false
        // (loose mock), causing VerifyOrphanOwnershipAsync to skip the ReadWorkItemRecordAsync call
        // silently. This is the intended path, but the implicit default is fragile if the mock behavior
        // ever changes. Add an explicit _mockFacade.SetupGet(f => f.CanVerifyWorkItems).Returns(false).
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-ct-orphan",
            IssueIdentifier = "GH-42",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _service.RecoverOrphanedStateAsync(EmptyMessage(), agentId, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_NonOce_GetRunHistoryAsync_OrphanPath_DoesNotPropagate()
    {
        // Counterpart: a non-OCE from GetRunHistoryAsync on the orphan path must still be caught
        // (fail-open). This guards against accidentally removing the when-filter entirely.
        // TODO [WARNING]: TransitionStatus and SetLocalAgentSnapshotField are called inside ActivateOrphanedRun
        // but are not set up on the mock. With MockBehavior.Loose this is silently ignored, but if the mock
        // were ever switched to MockBehavior.Strict the test would fail with unexpected invocations. Consider
        // adding explicit setups: _mockFacade.Setup(f => f.TransitionStatus(agentId, AgentStatus.Busy)) and
        // _mockFacade.Setup(f => f.SetLocalAgentSnapshotField(...)).
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-ct-orphan-noe",
            IssueIdentifier = "GH-42",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "run-ct-orphan-noe")))
            .Returns((PipelineRun?)null);

        // A non-OCE must be swallowed by the fail-open catch and restoration must proceed
        var act = () => _service.RecoverOrphanedStateAsync(EmptyMessage(), agentId, CancellationToken.None);
        await act.Should().NotThrowAsync("non-OCE from GetRunHistoryAsync must be swallowed (fail-open)");

        entry.ActiveJobId.Should().Be("run-ct-orphan-noe",
            "fail-open means restoration proceeds even when history storage fails");
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_CancelledToken_GetWorkItemRunRecordAsync_ReadWorkItem_PropagatesCancel()
    {
        // Arrange: CanVerifyWorkItems=true so ReadWorkItemRecordAsync is called from RestoreActiveJobAsync.
        // An OperationCanceledException from GetWorkItemRunRecordAsync must propagate as OCE (not HubException).
        // TODO [WARNING]: The GetWorkItemRunRecordAsync mock uses It.IsAny<CancellationToken>() and always
        // throws OperationCanceledException regardless of which token is supplied. The test verifies that
        // OCE propagates, not that the caller's specific token is forwarded. A stronger arrangement would
        // capture the token via a Callback and assert it equals cts.Token.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-ct-workitem");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.SetupGet(f => f.CanVerifyWorkItems).Returns(true);
        _mockFacade.Setup(f => f.GetWorkItemRunRecordAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _service.RecoverOrphanedStateAsync(message, agentId, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "OCE from GetWorkItemRunRecordAsync must propagate, not be converted to HubException");
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_NonOce_GetWorkItemRunRecordAsync_ReadWorkItem_ThrowsHubException()
    {
        // Counterpart: a non-OCE from GetWorkItemRunRecordAsync (store unavailable) must still
        // be re-thrown as HubException, not propagate as the original exception type.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-ct-workitem-noe");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.SetupGet(f => f.CanVerifyWorkItems).Returns(true);
        _mockFacade.Setup(f => f.GetWorkItemRunRecordAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store down"));
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        var act = () => _service.RecoverOrphanedStateAsync(message, agentId, CancellationToken.None);
        await act.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>(
            "non-OCE from GetWorkItemRunRecordAsync must be wrapped in HubException");
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_CancelledToken_GetWorkItemRunRecordAsync_CrashRecovery_PropagatesCancel()
    {
        // Arrange: agent registers without active job, registry has an ActiveJobId (crash recovery),
        // and GetRun returns null so TryReconstructRunFromDbAsync is called. An OCE must propagate.
        // TODO [WARNING]: The GetWorkItemRunRecordAsync mock uses It.IsAny<CancellationToken>() and always
        // throws OperationCanceledException regardless of which token is supplied. A regression restoring
        // CancellationToken.None would still make this test pass. To strengthen, capture the token via a
        // Callback and assert it equals cts.Token.
        // TODO [WARNING]: GetActiveRunsByAgent is not set up for agentId. Moq returns null for
        // IReadOnlyList by default with loose mock behavior. If HandleCrashRecoveryAsync calls
        // GetActiveRunsByAgent before the branch that leads to TryReconstructRunFromDbAsync, a null
        // return could cause a NullReferenceException before the asserted code path is reached.
        // Verify the entry.ActiveJobId non-null branch does not call GetActiveRunsByAgent, or add
        // an explicit setup: _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]).
        var agentId = MakeAgentId();
        const string existingJobId = "00000000-0000-0000-0000-000000000099"; // valid GUID required
        var entry = MakeEntry();
        entry.ActiveJobId = existingJobId;
        entry.OrphanRestoredAt = null;

        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == existingJobId))).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetWorkItemRunRecordAsync(It.Is<JobId>(j => j.Value == existingJobId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _service.RecoverOrphanedStateAsync(EmptyMessage(), agentId, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_LiveToken_DoesNotChangeExistingBehavior()
    {
        // Passing a live (non-cancelled) CancellationToken must not affect normal operation.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-live-token");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(new JobId("run-live-token"))).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        using var cts = new CancellationTokenSource();
        // Token is NOT cancelled

        var act = () => _service.RecoverOrphanedStateAsync(message, agentId, cts.Token);
        await act.Should().NotThrowAsync("a live token must not change existing behavior");

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == "run-live-token")), Times.Once);
    }

    // ── ActivateAgentJob helper invariants (via public paths) ─────────────────────────────
    // Tests 1-6 required by analysis prerequisites before the refactor.

    // Test 1: Site 3 (TrackLinkedActiveJob) call-order gap — UpdateAgentFieldAsync BEFORE TransitionStatus.
    // TrackLinkedActiveJob calls UpdateAgentFieldFireAndForget INSIDE the lock, TransitionStatus AFTER.
    // This pins the ordering so the helper cannot accidentally reverse the positions.
    [Fact]
    public async Task TrackLinkedActiveJob_CallOrder_UpdateAgentFieldBeforeTransitionStatus()
    {
        // TrackLinkedActiveJob is reached when the run already exists in memory (K8s path).
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-link-order");
        var message = MessageWithJob(job: activeJob);

        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-link-order",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = null, // unowned so TrackLinkedActiveJob will set ActiveJobId (null branch)
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        var entry = MakeEntry();
        // entry.ActiveJobId is null so the inner null-guard branch fires and UpdateAgentFieldFireAndForget
        // is called inside the lock, then TransitionStatus is called after the lock.

        _mockFacade.Setup(f => f.GetRun(new JobId("run-link-order"))).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        // TODO [WARNING]: GetActiveRunsByAgent returns empty to prevent orphan activation from also
        // firing for the same run. If the routing logic were to allow both TrackLinkedActiveJob and
        // ActivateOrphanedRun to activate for the same run, the HaveCount(2) assertion would fail
        // with count > 2 (a second TransitionStatus or UpdateAgentFieldAsync call), making it
        // indistinguishable from a call-order bug. There is no explicit assertion isolating that only
        // the TrackLinkedActiveJob path fired. Consider adding a comment or assertion that the orphan
        // path is excluded by the empty active-runs list.
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        var callOrder = new List<string>();
        // UpdateAgentFieldFireAndForget (the extension method used in production) calls
        // facade.UpdateAgentFieldAsync synchronously on the same call stack before scheduling
        // the fault-log ContinueWith. The Moq callback on UpdateAgentFieldAsync therefore fires
        // synchronously, so this interception reliably captures call order.
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback(() => callOrder.Add("UpdateAgentFieldAsync"))
            .Returns(Task.CompletedTask);
        _mockFacade.Setup(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()))
            .Callback(() => callOrder.Add("TransitionStatus"));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        // For Site 3 (TrackLinkedActiveJob): UpdateAgentFieldAsync fires first (inside the lock,
        // via UpdateAgentFieldFireAndForget), then TransitionStatus fires after the lock is released.
        callOrder.Should().ContainInOrder("UpdateAgentFieldAsync", "TransitionStatus");
        callOrder.Should().HaveCount(2,
            "exactly one UpdateAgentFieldAsync and one TransitionStatus must fire on this path");
    }

    // Test 2: Unconditional path (Site 1 via RestoreConsolidationTracking) — TransitionStatus before UpdateAgentFieldAsync.
    [Fact]
    public async Task ActivateAgentJob_UnconditionalPath_TransitionStatusBeforeUpdateAgentField()
    {
        // Verify via RestoreConsolidationTracking that for the unconditional path (guardAgainstConcurrentAssignment: false)
        // TransitionStatus fires BEFORE UpdateAgentFieldAsync (both outside the lock).
        // TODO [WARNING]: This test covers Site 1 (RestoreConsolidationTracking) only. Site 2 (RestorePipelineRun)
        // uses the same guardAgainstConcurrentAssignment: false branch. A regression reversing the call order
        // inside the unconditional branch would go undetected for Site 2. Consider adding a parallel test that
        // routes through RestorePipelineRun to pin the same invariant for that call site.
        // TODO [WARNING]: This test does not assert entry.ActiveJobId to confirm the unconditional assignment
        // occurred. If the whole block is skipped (e.g. helper returns false, or consolEntry is null), no
        // assertion catches the omission — only the Times.Once Verify on TransitionStatus partially compensates.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-uncond-order",
            providerConfigId: ConsolidationConstants.ProviderConfigId);
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        var callOrder = new List<string>();
        _mockFacade.Setup(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()))
            .Callback(() => callOrder.Add("TransitionStatus"));
        // UpdateAgentFieldFireAndForget (the extension method used in production) calls
        // facade.UpdateAgentFieldAsync synchronously on the same call stack before scheduling
        // the fault-log ContinueWith. The Moq callback on UpdateAgentFieldAsync therefore fires
        // synchronously, so this interception reliably captures call order.
        _mockFacade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback(() => callOrder.Add("UpdateAgentFieldAsync"))
            .Returns(Task.CompletedTask);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        callOrder.Should().ContainInOrder("TransitionStatus", "UpdateAgentFieldAsync");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // Test 3: Guarded path — when ActiveJobId is null, TransitionStatus is called and entry is set.
    [Fact]
    public async Task ActivateAgentJob_GuardedPath_WhenActiveJobIdNull_TransitionStatusCalled()
    {
        // Via TrackLinkedActiveJob: entry.ActiveJobId is null → guard passes → TransitionStatus called.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-guard-null");
        var message = MessageWithJob(job: activeJob);

        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-guard-null",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = null,
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        var entry = MakeEntry();
        // entry.ActiveJobId is null (default)

        _mockFacade.Setup(f => f.GetRun(new JobId("run-guard-null"))).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be("run-guard-null", "ActiveJobId must be set when guard passes (was null)");
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus must be called when ActiveJobId was null");
        // TODO [WARNING]: This test does not assert that UpdateAgentFieldFireAndForget (via UpdateAgentFieldAsync)
        // was called for the ActiveJobId field on the guarded null branch. The helper is required to call
        // UpdateAgentFieldFireAndForget inside the lock on this path. A regression silently dropping that
        // Redis write would not be caught by the current assertions.
    }

    // Test 4: Guarded path — when ActiveJobId is set to a DIFFERENT run, TransitionStatus is NOT called.
    [Fact]
    public async Task ActivateAgentJob_GuardedPath_WhenActiveJobIdAlreadySetDifferent_NoTransitionStatus()
    {
        // Via TrackLinkedActiveJob: entry.ActiveJobId is already set to a different run →
        // guard prevents assignment → TransitionStatus must NOT be called.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-guard-different");
        var message = MessageWithJob(job: activeJob);

        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-guard-different",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = agentId,
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        var entry = MakeEntry();
        entry.ActiveJobId = "some-other-run"; // already occupied by a different run

        _mockFacade.Setup(f => f.GetRun(new JobId("run-guard-different"))).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        entry.ActiveJobId.Should().Be("some-other-run", "ActiveJobId must not be overwritten by a different run");
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never,
            "TransitionStatus must NOT be called when entry.ActiveJobId is already set to a different run");
        // TODO [WARNING]: This test does not assert that UpdateAgentFieldFireAndForget (via UpdateAgentFieldAsync)
        // was also NOT called on the "already-set, different run" branch. The full contract for this branch is
        // that neither TransitionStatus nor UpdateAgentFieldAsync fires. Only half of that contract is verified here.
    }

    // Test 5: Guarded path — when ActiveJobId MATCHES the run being linked, TransitionStatus IS called.
    [Fact]
    public async Task ActivateAgentJob_GuardedPath_WhenActiveJobIdMatchesRun_TransitionStatusCalled()
    {
        // Via TrackLinkedActiveJob: entry.ActiveJobId already equals activeJob.RunId (same-agent reconnect).
        // The guard's else branch: shouldTransition = (entry.ActiveJobId == runId) = true → TransitionStatus called.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-guard-match");
        var message = MessageWithJob(job: activeJob);

        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-guard-match",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = agentId,
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        var entry = MakeEntry();
        entry.ActiveJobId = "run-guard-match"; // already set to the SAME run

        _mockFacade.Setup(f => f.GetRun(new JobId("run-guard-match"))).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus must be called when entry.ActiveJobId already matches the run being linked");
        // TODO [WARNING]: This test does not assert that UpdateAgentFieldFireAndForget (via UpdateAgentFieldAsync)
        // was NOT called a second time on the already-set-matching branch. In the original code, UpdateAgentFieldFireAndForget
        // was only issued on the null branch; the else branch (already equals runId) did not re-issue it. A regression
        // that incorrectly re-issued the field write on this path would not be caught by the current assertions.
    }

    // Test 6: additionalLockedAction — executed inside the lock ONLY on successful assignment (ActiveJobId was null).
    [Fact]
    public async Task ActivateAgentJob_AdditionalLockedAction_ExecutedOnlyWhenActiveJobIdWasNull()
    {
        // Verify via ActivateOrphanedRun (Site 4) that the additionalLockedAction:
        //   (a) is invoked when entry.ActiveJobId is null (successful assignment)
        //   (b) is NOT invoked when entry.ActiveJobId is already set to a different run ID (guard fires)
        //
        // We test (a) by observing that SetLocalAgentSnapshotField is called (it is invoked by
        // ActivateOrphanedRun's additionalLockedAction inside the extracted helper).
        // We test (b) by pre-setting entry2.ActiveJobId to a different run ID before the helper
        // acquires the lock (simulated via a synchronous Callback on GetActiveRunsByAgent).
        // NOTE: This is a structural precondition test, not a true race simulation. The Callback
        // fires synchronously on the calling thread before ActivateAgentJob is invoked, so
        // entry2.ActiveJobId is already "drain-assigned" when the helper runs its null-check.
        // The real concurrency race (a concurrent write between GetActiveRunsByAgent returning
        // and lock(entry.SyncRoot) being acquired) cannot be reproduced in a single-threaded
        // mock — but the structural precondition (ActiveJobId pre-set to a different run) is
        // sufficient to exercise the guard branch and verify the additionalLockedAction contract.

        // Part (a): successful assignment — additionalLockedAction fires
        {
            const string agentId = "agent-action-a";
            var entry = CreateEntry(agentId);
            entry.ActiveJobId = null;

            var orphanedRun = new PipelineRun
            {
                RunId = "orphan-action-a",
                IssueIdentifier = "org/repo#1",
                IssueTitle = "Test",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                AgentId = agentId
            };

            _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
            _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphanedRun]);
            _mockFacade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            _mockFacade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "orphan-action-a"))).Returns((PipelineRun?)null);

            var service = new AgentOrphanRecoveryService(
                _mockFacade.Object, _mockChangeNotifier.Object, _mockLogger.Object);
            await service.RecoverOrphanedStateAsync(CreateMessage(agentId, null), agentId);

            // SetLocalAgentSnapshotField is called by the additionalLockedAction in ActivateOrphanedRun.
            // If it was invoked, the helper correctly placed additionalLockedAction inside the null branch.
            _mockFacade.Verify(
                f => f.SetLocalAgentSnapshotField(
                    It.Is<AgentId>(a => a.Value == agentId), "activeJobId", "orphan-action-a"),
                Times.Once,
                "SetLocalAgentSnapshotField must be called when entry.ActiveJobId was null (additionalLockedAction fired)");
            entry.OrphanRestoredAt.Should().NotBeNull(
                "OrphanRestoredAt must be set by additionalLockedAction when assignment succeeds");
        }

        // Part (b): guard fires (ActiveJobId already set) — additionalLockedAction must NOT fire
        // TODO [WARNING]: Part (b) creates a separate service2 and mockFacade2, but still uses the
        // shared _mockChangeNotifier and _mockLogger from the test class. Part (a) also uses the
        // class-level _mockFacade without resetting it between parts. Accumulated invocation counts on
        // _mockFacade from Part (a) carry over into Part (b)'s execution; if any production code path
        // in Part (b) happened to call _mockFacade (currently it does not, because service2 uses mockFacade2),
        // shared state could produce misleading Verify results. Consider splitting into two [Fact] methods
        // or calling _mockFacade.Reset() between parts to make the boundary explicit.
        {
            const string agentId = "agent-action-b";
            var entry2 = CreateEntry(agentId);
            entry2.ActiveJobId = null;

            var orphanedRun2 = new PipelineRun
            {
                RunId = "orphan-action-b",
                IssueIdentifier = "org/repo#2",
                IssueTitle = "Test",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                AgentId = agentId
            };

            var mockFacade2 = new Mock<IAgentHubFacade>();
            // Pre-set ActiveJobId to a different run ID (structural precondition, not a real race).
            // The Callback fires synchronously when GetActiveRunsByAgent is called, before
            // ActivateAgentJob is invoked. This puts entry2 in the "already-assigned to a different
            // run" state that the guard is designed to detect. The genuine race (a concurrent write
            // between GetActiveRunsByAgent returning and lock acquisition) cannot be reproduced in a
            // single-threaded mock — this is the closest structural approximation.
            // TODO [WARNING]: This simulation sets entry2.ActiveJobId to "drain-assigned" (a DIFFERENT run id).
            // The helper's else-branch also returns shouldTransition = true when entry.ActiveJobId == runId
            // (same run id). No test covers the ActivateOrphanedRun path where a concurrent writer assigns
            // entry.ActiveJobId = mostRecent.RunId (the identical run) before the lock. That path produces
            // a different outcome: TransitionStatus and AddRun are called (no early return), but
            // OrphanRestoredAt and _localSnapshot are not updated (additionalLockedAction only fires on the
            // null branch). Add a test that sets entry2.ActiveJobId to orphanedRun2.RunId in the callback
            // and asserts whether TransitionStatus/AddRun/OrphanRestoredAt fire to lock in intended behavior.
            mockFacade2.Setup(f => f.GetByAgentId(agentId)).Returns(entry2);
            mockFacade2.Setup(f => f.GetActiveRunsByAgent(agentId))
                .Returns([orphanedRun2])
                .Callback(() => { entry2.ActiveJobId = "drain-assigned"; });
            mockFacade2.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var service2 = new AgentOrphanRecoveryService(
                mockFacade2.Object, _mockChangeNotifier.Object, _mockLogger.Object);
            await service2.RecoverOrphanedStateAsync(CreateMessage(agentId, null), agentId);

            // When the guard fires, additionalLockedAction must NOT be invoked, so
            // SetLocalAgentSnapshotField must NOT be called.
            mockFacade2.Verify(
                f => f.SetLocalAgentSnapshotField(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.Never,
                "SetLocalAgentSnapshotField must NOT be called when guard prevented assignment (additionalLockedAction must not fire)");
            entry2.OrphanRestoredAt.Should().BeNull(
                "OrphanRestoredAt must NOT be set when guard prevented assignment");
        }
    }
}

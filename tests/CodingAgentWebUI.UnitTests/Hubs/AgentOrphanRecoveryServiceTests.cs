using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .ReturnsAsync(new[]
            {
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#42",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.Completed,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            });
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .ReturnsAsync(new[]
            {
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#42",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.Cancelled,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            });
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .ReturnsAsync(new[]
            {
                new PipelineRunSummary
                {
                    RunId = runId,
                    IssueIdentifier = "org/repo#42",
                    IssueTitle = "Test",
                    FinalStep = PipelineStep.Failed,
                    StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1)
                }
            });
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be(agentId);
        entry.ActiveJobId.Should().Be(runId);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── Active job: run already in memory (owned by different agent) → no overwrite

    [Fact]
    public async Task ActiveJob_RunInMemoryOwnedByDifferentAgent_DoesNotOverwrite()
    {
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
        _mockFacade.Setup(f => f.GetRun(runId)).Returns(existingRun);
        _mockFacade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

        var message = CreateMessage(agentId, CreateActiveJob(runId));

        await _service.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be(otherAgent);
        entry.ActiveJobId.Should().BeNull();
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Never);
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
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun> { orphanedRun });

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
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .Returns(new List<PipelineRun> { orphanedRun })
            .Callback(() =>
            {
                // Simulate DrainService assigning a job between GetActiveRunsByAgent and lock
                entry.ActiveJobId = drainJobId;
            });

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

        var activeJob = CreateActiveJob(runId) with { RunType = PipelineRunType.Decomposition };
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunId == runId &&
            r.RunType == PipelineRunType.Decomposition)), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static AgentEntry CreateEntry(string agentId) => new()
    {
        AgentId = agentId,
        ConnectionId = "conn-1",
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
        Status = AgentStatus.Idle,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    private static AgentRegistrationMessage CreateMessage(string agentId, ActiveJobState? activeJob) => new()
    {
        AgentId = agentId,
        Hostname = "host-1",
        Labels = new[] { "dotnet" },
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
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .Returns(new List<PipelineRun> { orphanedRun })
            .Callback(() => { entry.ActiveJobId = drainJobId; });

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .Returns(new List<PipelineRun> { olderRun, newerRun });

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .Returns(new List<PipelineRun> { orphanedRun })
            .Callback(() => { entry.ActiveJobId = drainAssignedId; });

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
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

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
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        _mockFacade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns(new List<PipelineRun>());

        var activeJob = CreateActiveJob(runId) with { RunType = PipelineRunType.DecompositionAnalysis };
        var message = CreateMessage(agentId, activeJob);

        await _service.RecoverOrphanedStateAsync(message, agentId);

        _mockFacade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunId == runId &&
            r.RunType == PipelineRunType.DecompositionAnalysis)), Times.Once);
        _mockFacade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }
}

using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Characterization tests for <see cref="AgentOrphanRecoveryService"/> extracted from
/// <c>AgentHub.RegisterAgent</c>. Each test covers a specific branch of the recovery logic.
/// </summary>
public sealed class AgentOrphanRecoveryServiceTests : IDisposable
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly PipelineOrchestrationService _orchestration;
    private readonly AgentOrphanRecoveryService _service;

    public AgentOrphanRecoveryServiceTests()
    {
        _orchestration = TestOrchestrationFactory.CreateMinimal(
            configStore: Mock.Of<IConfigurationStore>(),
            providerFactory: Mock.Of<IProviderFactory>());

        _service = new AgentOrphanRecoveryService(
            _mockFacade.Object,
            _orchestration,
            _mockLogger.Object);
    }

    public void Dispose()
    {
        _orchestration.Dispose();
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
        // TODO: Add negative assertion: _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<string>(), It.IsAny<AgentStatus>()), Times.Never)
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
        _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<string>(), It.IsAny<AgentStatus>()), Times.Never);
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
        // _mockFacade.Verify(f => f.TransitionStatus(It.IsAny<string>(), It.IsAny<AgentStatus>()), Times.Never);
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
}

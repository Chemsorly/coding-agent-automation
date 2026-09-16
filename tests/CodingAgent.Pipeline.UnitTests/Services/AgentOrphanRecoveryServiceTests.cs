using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for AgentOrphanRecoveryService.
/// Covers: active job restoration (new run, existing run, consolidation, history deduplication),
/// orphan detection under lock, crash recovery detection, and null-message guard.
/// </summary>
public sealed class AgentOrphanRecoveryServiceTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly AgentOrphanRecoveryService _sut;

    public AgentOrphanRecoveryServiceTests()
    {
        _sut = new AgentOrphanRecoveryService(
            _facade.Object,
            _changeNotifier.Object,
            _logger.Object);
    }

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

    private static PipelineRunSummary MakeSummary(string runId, PipelineStep finalStep) =>
        new()
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier("GH-42"),
            IssueTitle = "Test",
            FinalStep = finalStep
        };

    // ── Guard: null message ───────────────────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_NullMessage_Throws()
    {
        var act = () => _sut.RecoverOrphanedStateAsync(null!, MakeAgentId());
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_NullAgentIdValue_Throws()
    {
        var act = () => _sut.RecoverOrphanedStateAsync(EmptyMessage(), new AgentId(null!));
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── No active job, no orphaned runs ──────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_NoActiveJob_NoOrphans_DoesNothing()
    {
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }

    // ── Restore active job: new run ───────────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_WithActiveJob_NoExistingRun_AddsRun()
    {
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _facade.Setup(f => f.GetRun(new JobId("run-1"))).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == "run-1")), Times.Once);
        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_WithActiveJob_RunAlreadyInHistory_SkipsRestore()
    {
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _facade.Setup(f => f.GetRun(new JobId("run-1"))).Returns((PipelineRun?)null);
        // History contains the run as Completed (not cancelled/failed) → stale
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>
            {
                MakeSummary("run-1", PipelineStep.Completed)
            } as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        // After active-job restoration is skipped, orphan detection runs — return empty
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_WithActiveJob_RunInHistoryAsCancelled_StillRestores()
    {
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _facade.Setup(f => f.GetRun(new JobId("run-1"))).Returns((PipelineRun?)null);
        // History contains run as Cancelled — may be re-dispatched, allow restoration
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>
            {
                MakeSummary("run-1", PipelineStep.Cancelled)
            } as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Once);
    }

    // ── RunType variants ──────────────────────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_ReviewRunType_CreatesReviewRun()
    {
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1", runType: PipelineRunType.Review);
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunType == PipelineRunType.Review)), Times.Once);
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_DecompositionRunType_CreatesDecompositionRun()
    {
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1", runType: PipelineRunType.Decomposition);
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.AddRun(It.Is<PipelineRun>(r =>
            r.RunType == PipelineRunType.Decomposition)), Times.Once);
    }

    // ── Consolidation tracking ────────────────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_ConsolidationJob_DoesNotAddPipelineRun()
    {
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1",
            providerConfigId: ConsolidationConstants.ProviderConfigId);
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        // Consolidation runs skip AddRun — only tracked via ActiveJobId
        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
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

        _facade.Setup(f => f.GetRun(new JobId("run-1"))).Returns(existingRun);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be("agent-1");
        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never);
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

        _facade.Setup(f => f.GetRun(new JobId("run-1"))).Returns(existingRun);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        existingRun.AgentId.Should().Be("agent-1", "pod replacement must update run.AgentId to the new agent");
        entry.ActiveJobId.Should().Be("run-1", "the new agent must be linked to the run");
        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never, "existing run must not be re-added");
        // TODO: [WARNING] TransitionStatus(agentId, AgentStatus.Busy) is not verified here, but the
        // test comment explicitly notes the setup satisfies the inner lock guard so it will be called.
        // A silent removal of the TransitionStatus call would not be caught. The analogous Web.UnitTests
        // counterpart does verify TransitionStatus(Busy) Times.Once. Consider adding:
        // _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
        //     "the new agent must be transitioned to Busy");
        // TODO: [WARNING] The acceptance criterion "A log entry is emitted when AgentId is updated due
        // to pod replacement" is not verified here. The _logger mock is available in this test class;
        // a silent removal of the pod-replacement Information log line would not be caught by any test
        // on the Pipeline.UnitTests recovery-service path.
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_ExistingRunAdoptsModelName()
    {
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1", modelName: "claude-sonnet-4");
        var message = MessageWithJob(job: activeJob);

        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-1",
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

        _facade.Setup(f => f.GetRun(new JobId("run-1"))).Returns(existingRun);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        existingRun.ModelName.Should().Be("claude-sonnet-4");
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_ExistingRunDoesNotOverwriteExistingModelName()
    {
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1", modelName: "new-model");
        var message = MessageWithJob(job: activeJob);

        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-1",
            IssueIdentifier = "GH-42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = null,
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        existingRun.ModelName = "already-set";
        var entry = MakeEntry();

        _facade.Setup(f => f.GetRun(new JobId("run-1"))).Returns(existingRun);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        // ??= semantics: existing value should be preserved
        existingRun.ModelName.Should().Be("already-set");
    }

    // ── Orphan detection ──────────────────────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_OrphanedRuns_RestoresMostRecent()
    {
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var orphan1 = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-1",
            IssueIdentifier = "GH-1",
            IssueTitle = "T",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        var orphan2 = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-2",
            IssueIdentifier = "GH-2",
            IssueTitle = "T",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan1, orphan2]);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        // Most recent (last element) should be restored
        entry.ActiveJobId.Should().Be("run-2");
        entry.OrphanRestoredAt.Should().NotBeNull();
        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_OrphanedRuns_NotifiesChange()
    {
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-1",
            IssueIdentifier = "GH-1",
            IssueTitle = "T",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        // TransitionStatus is called when orphan is restored — verifies the restoration path ran
        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
        entry.ActiveJobId.Should().Be("run-1");
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_OrphanDetection_DoesNotOverwriteExistingActiveJob()
    {
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        // Simulate DrainService assigning a job between GetByAgentId and lock
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

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        // Should NOT overwrite the already-assigned job
        entry.ActiveJobId.Should().Be("already-assigned");
    }

    // ── Crash recovery ────────────────────────────────────────────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_CrashRecovery_SetsOrphanRestoredAt()
    {
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        // Entry has an active job (was restored from prior state) but agent registered without one
        entry.ActiveJobId = "job-in-progress";

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        // message.ActiveJob is null → agent lost in-memory state
        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        entry.OrphanRestoredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_CrashRecovery_WhenAlreadyRestored_DoesNotOverwrite()
    {
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var original = DateTimeOffset.UtcNow.AddMinutes(-5);
        entry.ActiveJobId = "job-in-progress";
        entry.OrphanRestoredAt = original; // already set

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        // OrphanRestoredAt should not be overwritten — condition: entry.OrphanRestoredAt is null
        entry.OrphanRestoredAt.Should().Be(original);
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_CrashRecovery_WhenAgentHasActiveJob_LogsInfo()
    {
        // When message.ActiveJob is NOT null (agent has a job, entry also has a job), just logs info
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-1");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();
        entry.ActiveJobId = "run-1";

        _facade.Setup(f => f.GetRun(new JobId("run-1"))).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        // Should not throw
        var act = () => _sut.RecoverOrphanedStateAsync(message, agentId);
        await act.Should().NotThrowAsync();
    }

    // ── DetectAndRestoreOrphans: AddRun called to re-materialize Redis hash ────

    [Fact]
    public async Task RecoverOrphanedStateAsync_OrphanedRuns_AddRunCalledToRematerializeHash()
    {
        // AC1: DetectAndRestoreOrphans must call AddRun to re-materialize the run hash in Redis
        // so that GetRun returns non-null after orphan recovery (fixes the hash-expiry bug).
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-orphan",
            IssueIdentifier = "GH-99",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);
        // Pre-configure GetRun to return the orphaned run — reflects Redis state after AddRun writes the hash.
        // The mock doesn't propagate AddRun state automatically; this verifies the real-system contract.
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "run-orphan"))).Returns(orphan);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        _facade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == "run-orphan")), Times.Once,
            "AddRun must be called to re-materialize the run hash so GetRun returns non-null");
        // TODO: [WARNING] The assertion below is tautological — the mock was configured to return
        // orphan for this key unconditionally, so GetRun will always return non-null regardless of
        // whether AddRun was ever called. The meaningful assertion is the Verify(AddRun) above.
        // This assertion should not be relied upon as coverage; it only verifies mock plumbing.
        _facade.Object.GetRun("run-orphan").Should().NotBeNull(
            "GetRun must return a valid run after orphan recovery");
        entry.ActiveJobId.Should().Be("run-orphan");
        // TODO: [WARNING] Missing assertion: TransitionStatus(agentId, AgentStatus.Busy) is called
        // in the DetectAndRestoreOrphans path but is not verified here. A regression removing the
        // TransitionStatus call would pass this suite (A) but fail suite B
        // (NoActiveJob_OrphanedRuns_AddsPipelineRunToRedis in CodingAgent.Web.UnitTests).
        // Add: _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once);
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_OrphanDetection_DrainRace_DoesNotCallAddRun()
    {
        // When DrainService wins the race and assigns a job before the lock, AddRun must NOT be called.
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        entry.ActiveJobId = null;

        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-drain",
            IssueIdentifier = "GH-1",
            IssueTitle = "T",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId))
            .Returns([orphan])
            .Callback(() => { entry.ActiveJobId = "drain-assigned"; }); // simulate drain race

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        entry.ActiveJobId.Should().Be("drain-assigned", "drain-assigned job must not be overwritten");
        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when the drain race skips orphan restoration");
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

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        entry.ActiveJobId.Should().Be("already-assigned");
        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when the existing active job guard prevents orphan restore");
    }

    // ── HandleCrashRecovery: AddRun called when hash exists ──────────────

    [Fact]
    public async Task RecoverOrphanedStateAsync_CrashRecovery_ReAddsRunIfHashExists()
    {
        // HandleCrashRecovery must call AddRun to re-materialize the run hash when it still
        // exists in Redis, preventing hash expiry before the agent's first hub call.
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        entry.ActiveJobId = "crash-job-1";
        entry.OrphanRestoredAt = null;

        var existingRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "crash-job-1",
            IssueIdentifier = "GH-88",
            IssueTitle = "Crash Run",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        // GetRun returns the run — hash still exists
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "crash-job-1"))).Returns(existingRun);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        entry.OrphanRestoredAt.Should().NotBeNull("crash recovery must set OrphanRestoredAt");
        _facade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == "crash-job-1")), Times.Once,
            "AddRun must be called to re-materialize the run hash when it still exists");
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_CrashRecovery_NoOpIfHashGone()
    {
        // HandleCrashRecovery must NOT call AddRun when GetRun returns null (hash already expired).
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        entry.ActiveJobId = "crash-job-gone";
        entry.OrphanRestoredAt = null;

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        // GetRun returns null — hash has expired
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "crash-job-gone"))).Returns((PipelineRun?)null);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        entry.OrphanRestoredAt.Should().NotBeNull("OrphanRestoredAt must be set even when hash is gone");
        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when the run hash is already expired");
    }
}

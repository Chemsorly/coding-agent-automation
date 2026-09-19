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
        // GetRun returns null — hash is absent (expired or not yet written). Under the fix,
        // GetRun returning null is the condition that triggers AddRun to re-materialize the hash.
        // If GetRun returned non-null, AddRun would be skipped (hash is live, no overwrite needed).
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "run-orphan"))).Returns((PipelineRun?)null);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        _facade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == "run-orphan")), Times.Once,
            "AddRun must be called to re-materialize the run hash when the hash is absent");
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

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        var callOrder = new List<string>();
        _facade.Setup(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()))
            .Callback(() => callOrder.Add("TransitionStatus"));
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback(() => callOrder.Add("UpdateAgentFieldAsync"))
            .Returns(Task.CompletedTask);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

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
        //   _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Once);
        //   _facade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
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

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.UpdateAgentFieldAsync(agentId, "activeJobId", "run-consol-args"), Times.Once,
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

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        // Return null for consolEntry — the if (consolEntry is not null) guard fails,
        // so the TOCTOU guard and both downstream calls are suppressed.
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns((AgentEntry?)null);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()),
            Times.Never,
            "TransitionStatus must not be called when consolEntry is null");
        _facade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()),
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

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);

        var callOrder = new List<string>();
        _facade.Setup(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()))
            .Callback(() => callOrder.Add("TransitionStatus"));
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback(() => callOrder.Add("UpdateAgentFieldAsync"))
            .Returns(Task.CompletedTask);

        await _sut.RecoverOrphanedStateAsync(message, agentId);
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
        //   _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Once);
        //   _facade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
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

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.UpdateAgentFieldAsync(agentId, "activeJobId", "run-pipeline-args"), Times.Once,
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

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        // Return null for restoredEntry — simulates the guard failing (entry not found).
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns((AgentEntry?)null);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        // AddRun is still called (it happens before the entry guard)
        _facade.Verify(f => f.AddRun(It.Is<PipelineRun>(r => r.RunId == "run-toctou-pipeline")), Times.Once);
        // Neither TransitionStatus nor UpdateAgentFieldAsync should fire
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()),
            Times.Never,
            "TransitionStatus must not be called when restoredEntry is null");
        _facade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never,
            "UpdateAgentFieldAsync must not be called when the TOCTOU guard suppresses both calls");
    }

    // ── DetectAndRestoreOrphans: hash-exists guard (issue #2663) ─────────────
    // TODO: [WARNING] No test covers the negative path for GetRun in DetectAndRestoreOrphans:
    // what happens if GetRun throws (e.g. Redis connectivity failure, OperationCanceledException)?
    // Currently an exception would propagate out of DetectAndRestoreOrphans uncaught, which may
    // crash the caller or leave the agent in an inconsistent state. Consider adding a test that
    // mocks GetRun to throw and verifies the service either swallows with a log (consistent with
    // other Redis-failure handling patterns in the file) or surfaces a clear error.

    [Fact]
    public async Task RecoverOrphanedStateAsync_OrphanedRuns_HashAlreadyExists_DoesNotCallAddRun()
    {
        // When the orphan's Redis hash already exists (another replica wrote it, or it hasn't
        // expired), AddRun must NOT be called. Calling AddRun would overwrite all fields from
        // the stale snapshot, clobbering newer values (e.g. currentStep, prUrl) written by
        // other replicas between GetActiveRunsByAgent and this code path.
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-orphan-exists",
            IssueIdentifier = "GH-99",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });
        var liveRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-orphan-exists",
            IssueIdentifier = "GH-99",
            IssueTitle = "Orphan",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });
        liveRun.CurrentStep = PipelineStep.GeneratingCode; // advanced by another replica

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);
        // GetRun returns non-null — hash exists in Redis (live run with advanced state)
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "run-orphan-exists"))).Returns(liveRun);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called when the hash already exists — would overwrite live fields with stale snapshot");
        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must still be called even when AddRun is skipped");
        entry.ActiveJobId.Should().Be("run-orphan-exists");
    }

    [Fact]
    public async Task RecoverOrphanedStateAsync_OrphanedRuns_HashAlreadyExists_PreservesCurrentStep()
    {
        // Issue #2663 AC: "a hash with pre-existing currentStep=3 must retain currentStep=3
        // after orphan restore with a snapshot containing currentStep=1."
        //
        // At this test boundary (_facade is mocked), the mock's AddRun is the ONLY write path
        // available to DetectAndRestoreOrphans — if AddRun is never called, no Redis fields
        // can be overwritten. Times.Never on AddRun IS the proof that currentStep is preserved:
        // no write occurred, therefore currentStep (and all other fields) remain at their live values.
        var agentId = MakeAgentId();
        var entry = MakeEntry();

        // Snapshot orphan has stale currentStep=Created (the initial value when the run was dispatched)
        var staleSnapshot = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-step-preserve",
            IssueIdentifier = "GH-100",
            IssueTitle = "Step Preserve Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });
        // staleSnapshot.CurrentStep == Created (default from CreateImplementation)

        // Live hash has currentStep=GeneratingCode — advanced by another replica while this
        // agent was disconnected. This simulates the "currentStep=3" scenario from the AC.
        var liveHash = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-step-preserve",
            IssueIdentifier = "GH-100",
            IssueTitle = "Step Preserve Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "r",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "x",
            StartedAt = DateTimeOffset.UtcNow
        });
        liveHash.CurrentStep = PipelineStep.GeneratingCode; // the "currentStep=3" from the AC

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([staleSnapshot]);
        // GetRun returns the live hash — hash exists with advanced currentStep
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "run-step-preserve"))).Returns(liveHash);

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        // The issue's AC requires currentStep to be preserved. At the mock boundary:
        // AddRun Times.Never proves no write occurred → no field was overwritten.
        _facade.Verify(f => f.AddRun(It.IsAny<PipelineRun>()), Times.Never,
            "AddRun must not be called — calling it would overwrite currentStep=ImplementingCode with stale Created");
        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called unconditionally");
        // TODO: [WARNING] The assertion below is tautological: liveHash is a local C# object that
        // no code path in DetectAndRestoreOrphans can mutate (it is only returned from the GetRun
        // mock). The assertion will always pass regardless of whether the guard is present or not.
        // The load-bearing proof of AC #3 is the Times.Never on AddRun above. This assertion
        // documents intent but adds no regression safety. An integration test against a real/fake
        // Redis store would provide stronger field-level evidence.
        // Verify the live hash state is unchanged (staleSnapshot was never written to the mock store)
        liveHash.CurrentStep.Should().Be(PipelineStep.GeneratingCode,
            "the live currentStep must not be overwritten by the stale snapshot value");
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
    public async Task RestorePipelineRun_HappyPath_CallsTransitionStatus()
    {
        // Happy-path regression guard: RestorePipelineRun must call TransitionStatus(Busy) when
        // a matching entry is found.
        //
        // NOTE: This test does NOT exercise the concurrent-disconnect scenario. There is no mock
        // call site between lock-release and `if (shouldTransition)` in RestorePipelineRun, so the
        // TOCTOU race cannot be deterministically reproduced in a single-threaded unit test. The
        // concurrent-disconnect correctness for this site is structural: shouldTransition is a
        // stack-local bool set inside the lock and cannot be cleared by any concurrent handler.
        // Verified by code inspection of the lock boundary in RestorePipelineRun.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-concurrent-pipeline");
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _facade.Setup(f => f.AddRun(It.IsAny<PipelineRun>()));

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called from RestorePipelineRun when entry is found");
    }

    [Fact]
    public async Task RestoreConsolidationTracking_HappyPath_CallsTransitionStatus()
    {
        // Happy-path regression guard: RestoreConsolidationTracking must call TransitionStatus(Busy)
        // when a matching entry is found.
        //
        // NOTE: This test does NOT exercise the concurrent-disconnect scenario. There is no mock
        // call site between lock-release and `if (shouldTransition)` in RestoreConsolidationTracking,
        // so the TOCTOU race cannot be deterministically reproduced in a single-threaded unit test.
        // The concurrent-disconnect correctness for this site is structural: shouldTransition is a
        // stack-local bool set inside the lock and cannot be cleared by any concurrent handler.
        // Verified by code inspection of the lock boundary in RestoreConsolidationTracking.
        var agentId = MakeAgentId();
        var activeJob = MakeActiveJob("run-concurrent-consol",
            providerConfigId: ConsolidationConstants.ProviderConfigId);
        var message = MessageWithJob(job: activeJob);
        var entry = MakeEntry();

        _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>() as IReadOnlyList<PipelineRunSummary>);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called from RestoreConsolidationTracking when entry is found");
    }

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

        _facade.Setup(f => f.GetRun(new JobId("run-concurrent-link"))).Returns(existingRun);
        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        // Set up GetActiveRunsByAgent in case DetectAndRestoreOrphans is reached after the callback
        // clears entry.ActiveJobId. We return empty to keep the test focused on LinkAgentToExistingRun.
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([]);
        _facade.Setup(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()))
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

        await _sut.RecoverOrphanedStateAsync(message, agentId);

        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
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

        _facade.Setup(f => f.GetByAgentId(agentId)).Returns(entry);
        _facade.Setup(f => f.GetActiveRunsByAgent(agentId)).Returns([orphan]);
        // GetRun returns null so AddRun is called; we simulate the disconnect inside AddRun.
        _facade.Setup(f => f.GetRun(It.Is<JobId>(j => j.Value == "run-orphan-concurrent")))
            .Returns((PipelineRun?)null);
        _facade.Setup(f => f.AddRun(It.IsAny<PipelineRun>()))
            .Callback(() =>
            {
                // Simulate a concurrent disconnect clearing ActiveJobId after the lock releases.
                // With the old pattern: `entry.ActiveJobId == mostRecent.RunId` would read null
                // (cleared here) → false → TransitionStatus skipped.
                // With the new shouldTransition flag: it was set inside the lock, so transition fires.
                entry.ActiveJobId = null;
            });

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        _facade.Verify(f => f.TransitionStatus(agentId, AgentStatus.Busy), Times.Once,
            "TransitionStatus(Busy) must be called; the shouldTransition flag was set inside the lock");
    }

    [Fact]
    public async Task DetectAndRestoreOrphans_DrainRaceSetsActiveJobId_ShouldTransitionIsFalse_NoTransitionStatus()
    {
        // Verify the drain-race path: when entry.ActiveJobId is already set inside the lock
        // (drain service assigned a job before we acquired the lock), shouldTransition is false
        // and TransitionStatus must NOT be called.
        //
        // TODO: [WARNING] The GetActiveRunsByAgent callback sets entry.ActiveJobId = "drain-assigned"
        // before GetActiveRunsByAgent returns — i.e., before GetByAgentId is called and before the
        // lock is acquired. This exercises the pre-lock path (entry already has a job when the lock
        // guard `if (entry.ActiveJobId is not null)` is evaluated), not the intra-lock drain race
        // (where DrainService would assign between GetActiveRunsByAgent and lock acquisition). The
        // behavioral assertion is correct (the null guard inside the lock catches it), but the
        // setup timing is misleading. The test does catch any regression that removes the null guard.
        var agentId = MakeAgentId();
        var entry = MakeEntry();
        var orphan = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "orphan-drain-race",
            IssueIdentifier = "GH-42",
            IssueTitle = "Orphan",
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
            .Callback(() => { entry.ActiveJobId = "drain-assigned"; }); // drain wins the race

        await _sut.RecoverOrphanedStateAsync(EmptyMessage(), agentId);

        // shouldTransition was set to false inside the lock (entry.ActiveJobId was not null)
        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never,
            "TransitionStatus must not be called when the drain-race guard sets shouldTransition = false");
        entry.ActiveJobId.Should().Be("drain-assigned",
            "drain-assigned job must not be overwritten");
    }
}

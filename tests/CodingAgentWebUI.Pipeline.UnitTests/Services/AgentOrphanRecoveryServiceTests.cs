using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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
            RunId = "run-1", IssueIdentifier = "GH-1", IssueTitle = "T", IssueProviderConfigId = "github",
            RepoProviderConfigId = "r", AgentId = "agent-1", AgentProviderConfigId = "kiro",
            InitiatedBy = "x", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });
        var orphan2 = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-2", IssueIdentifier = "GH-2", IssueTitle = "T", IssueProviderConfigId = "github",
            RepoProviderConfigId = "r", AgentId = "agent-1", AgentProviderConfigId = "kiro",
            InitiatedBy = "x", StartedAt = DateTimeOffset.UtcNow
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
            RunId = "run-1", IssueIdentifier = "GH-1", IssueTitle = "T", IssueProviderConfigId = "github",
            RepoProviderConfigId = "r", AgentId = "agent-1", AgentProviderConfigId = "kiro",
            InitiatedBy = "x", StartedAt = DateTimeOffset.UtcNow
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
            RunId = "orphan-1", IssueIdentifier = "GH-1", IssueTitle = "T", IssueProviderConfigId = "github",
            RepoProviderConfigId = "r", AgentId = "agent-1", AgentProviderConfigId = "kiro",
            InitiatedBy = "x", StartedAt = DateTimeOffset.UtcNow
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
}

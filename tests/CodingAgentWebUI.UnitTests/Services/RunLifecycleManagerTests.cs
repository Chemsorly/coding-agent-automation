using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="RunLifecycleManager"/> — validates lifecycle coordination
/// across run service, agent registry, label service, history, and work item transitions.
/// </summary>
public sealed class RunLifecycleManagerTests
{
    private static readonly string[] DotnetLabels = ["dotnet"];

    private readonly Mock<ILogger> _mockLogger = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly AgentReservationService _dispatcher;
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _dispatcher = new AgentReservationService(_registry, _mockLogger.Object);

        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
            _dispatcher,
            _mockLogger.Object)); // Legacy mode — no DB
    }

    // ── AgentAcceptedRunAsync ────────────────────────────────────────────

    [Fact]
    public async Task AgentAcceptedRunAsync_ReviewRunType_SwapsLabelWithRepoProviderAndPullRequestTarget()
    {
        // Arrange
        var run = CreateRun("run-1", PipelineRunType.Review);
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        await _sut.AgentAcceptedRunAsync("run-1", "agent-1", "org/repo#42",
            "issue-provider-1", "repo-provider-1", PipelineRunType.Review, CancellationToken.None);

        // Assert: label swap uses repoProviderConfigId + PullRequest target
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "repo-provider-1", "org/repo#42", AgentLabels.InProgress, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAcceptedRunAsync_ImplementationRunType_SwapsLabelWithIssueProviderAndIssueTarget()
    {
        // Arrange
        var run = CreateRun("run-2", PipelineRunType.Implementation);
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        await _sut.AgentAcceptedRunAsync("run-2", "agent-1", "org/repo#10",
            "issue-provider-1", "repo-provider-1", PipelineRunType.Implementation, CancellationToken.None);

        // Assert: label swap uses issueProviderConfigId + Issue target
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "issue-provider-1", "org/repo#10", AgentLabels.InProgress, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAcceptedRunAsync_DecompositionAnalysisRunType_SwapsLabelWithIssueProviderAndIssueTarget()
    {
        // Arrange
        var run = CreateRun("run-3", PipelineRunType.DecompositionAnalysis);
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        await _sut.AgentAcceptedRunAsync("run-3", "agent-1", "org/repo#5",
            "issue-provider-1", "repo-provider-1", PipelineRunType.DecompositionAnalysis, CancellationToken.None);

        // Assert: label swap uses issueProviderConfigId + Issue target
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "issue-provider-1", "org/repo#5", AgentLabels.InProgress, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAcceptedRunAsync_SetsAgentIdOnRun_AndTransitionsAgentToBusy()
    {
        // Arrange
        var run = CreateRun("run-4", PipelineRunType.Implementation);
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        await _sut.AgentAcceptedRunAsync("run-4", "agent-1", "org/repo#1",
            "ip-1", "rp-1", PipelineRunType.Implementation, CancellationToken.None);

        // Assert
        run.AgentId.Should().Be("agent-1");
        var agent = _registry.GetByAgentId("agent-1");
        agent!.ActiveJobId.Should().Be("run-4");
        agent.Status.Should().Be(AgentStatus.Busy);
    }

    [Fact]
    public async Task AgentAcceptedRunAsync_RunNotFound_LogsWarning_StillSwapsLabel()
    {
        // Run does not exist in the store
        RegisterAgent("agent-1");

        // Should not throw — warning is logged but label swap still proceeds
        await _sut.AgentAcceptedRunAsync("run-missing", "agent-1", "org/repo#1",
            "ip-1", "rp-1", PipelineRunType.Implementation, CancellationToken.None);

        // Label swap still fires even when run is absent
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.InProgress, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AgentAcceptedRunAsync_AgentNotFound_LogsWarning_StillSetsAgentIdOnRun()
    {
        // Agent is not registered — run exists but agent is absent
        var run = CreateRun("run-5", PipelineRunType.Implementation);
        _runService.AddRun(run);

        // Should not throw
        await _sut.AgentAcceptedRunAsync("run-5", "agent-missing", "org/repo#1",
            "ip-1", "rp-1", PipelineRunType.Implementation, CancellationToken.None);

        // AgentId is still set on the run even though the agent wasn't in registry
        run.AgentId.Should().Be("agent-missing");
    }

    // ── FailRunAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task FailRunAsync_RemovesRun_PersistsHistory_ClearsAgent_SwapsLabel()
    {
        // Arrange
        var run = CreateRun("run-fail", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        _runService.AddRun(run);

        var entry = RegisterAgent("agent-1");
        entry.ActiveJobId = "run-fail";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // Act
        var result = await _sut.FailRunAsync("run-fail", "Something went wrong", CancellationToken.None);

        // Assert: run returned
        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-fail");
        result.FailureReason.Should().Be("Something went wrong");
        result.CurrentStep.Should().Be(PipelineStep.Failed);

        // Run removed from active
        _runService.GetRun("run-fail").Should().BeNull();

        // History persisted
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-fail"), It.IsAny<CancellationToken>()), Times.Once);

        // Agent cleared and transitioned to Idle
        var agent = _registry.GetByAgentId("agent-1");
        agent!.ActiveJobId.Should().BeNull();
        agent.Status.Should().Be(AgentStatus.Idle);

        // Label swapped to error via issue provider (Implementation → Issue target)
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailRunAsync_RunDoesNotExist_ReturnsNull()
    {
        // Act: no run was added with this ID
        var result = await _sut.FailRunAsync("non-existent-run", "reason", CancellationToken.None);

        // Assert
        result.Should().BeNull();

        // No side effects
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailRunAsync_ReviewRun_SwapsLabelViaRepoProvider()
    {
        // Arrange
        var run = CreateRun("run-review-fail", PipelineRunType.Review);
        run.AgentId = "agent-1";
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        var result = await _sut.FailRunAsync("run-review-fail", "Review failed", CancellationToken.None);

        // Assert: label swap routes via repo provider for Review runs
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "rp-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── CompleteRunAsync ────────────────────────────────────────────────

    [Fact]
    public async Task CompleteRunAsync_RemovesRun_PersistsHistory_MarksIssueComplete()
    {
        // Arrange
        var run = CreateRun("run-complete", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        run.CurrentStep = PipelineStep.Completed; // Normal flow: JobCompletionMapper.Apply sets terminal step
        _runService.AddRun(run);

        // Act
        var result = await _sut.CompleteRunAsync("run-complete", WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-complete");

        // Run removed
        _runService.GetRun("run-complete").Should().BeNull();

        // History persisted
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-complete"), It.IsAny<CancellationToken>()), Times.Once);

        // CompleteRunAsync does NOT clear agent state, but DOES swap labels as a fallback for hub crash scenarios.
        // The PipelineRun overload routes via run.ProviderConfigIdForLabel and run.LabelTargetKind.
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Done, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_RunDoesNotExist_ReturnsNull()
    {
        var result = await _sut.CompleteRunAsync("ghost", WorkItemStatus.Succeeded, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task CompleteRunAsync_NonTerminalStep_MapsToFailed_WhenStatusFailed()
    {
        // Arrange: run stuck at a non-terminal step (edge case — normally JobCompletionMapper sets terminal step)
        var run = CreateRun("run-nonterminal-fail", PipelineRunType.Implementation);
        run.CurrentStep = PipelineStep.RunningQualityGates;
        _runService.AddRun(run);

        // Act
        var result = await _sut.CompleteRunAsync("run-nonterminal-fail", WorkItemStatus.Failed, CancellationToken.None);

        // Assert: guard maps non-terminal step to Failed
        result.Should().NotBeNull();
        result!.CurrentStep.Should().Be(PipelineStep.Failed);

        // History persisted with corrected step
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-nonterminal-fail" && r.CurrentStep == PipelineStep.Failed),
            It.IsAny<CancellationToken>()), Times.Once);

        // Label swapped to agent:error (derived from WorkItemStatus.Failed when no FinalLabel set)
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_NonTerminalStep_MapsToCompleted_WhenStatusSucceeded()
    {
        // Arrange: run stuck at a non-terminal step
        var run = CreateRun("run-nonterminal-success", PipelineRunType.Implementation);
        run.CurrentStep = PipelineStep.ReviewingCode;
        _runService.AddRun(run);

        // Act
        var result = await _sut.CompleteRunAsync("run-nonterminal-success", WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert: guard maps non-terminal step to Completed
        result.Should().NotBeNull();
        result!.CurrentStep.Should().Be(PipelineStep.Completed);

        // History persisted with corrected step
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-nonterminal-success" && r.CurrentStep == PipelineStep.Completed),
            It.IsAny<CancellationToken>()), Times.Once);

        // Label swapped to agent:done (derived from WorkItemStatus.Succeeded when no FinalLabel set)
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Done, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_AlreadyTerminalStep_NotMutated()
    {
        // Arrange: run already has terminal step (normal production flow)
        var run = CreateRun("run-already-terminal", PipelineRunType.Implementation);
        run.CurrentStep = PipelineStep.Completed;
        _runService.AddRun(run);

        // Act
        var result = await _sut.CompleteRunAsync("run-already-terminal", WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert: step unchanged — guard is a no-op
        result.Should().NotBeNull();
        result!.CurrentStep.Should().Be(PipelineStep.Completed);

        // Label swapped to agent:done
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Done, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_WhenPostCompletionBookkeepingNotCalled_LabelIsSwapped()
    {
        // Acceptance criteria: simulate hub crash after CompleteRunAsync — PostCompletionBookkeepingAsync
        // is never called — and assert the label was already swapped by CompleteRunAsync itself.
        //
        // Note: at the unit-test level there is no observable difference between a "hub crash scenario"
        // and a normal CompleteRunAsync invocation — PostCompletionBookkeepingAsync is never present in
        // unit tests. This test documents the design property: the label swap in CompleteRunAsync is
        // independent of whether the hub's post-completion path executes.
        var run = CreateRun("run-hub-crash", PipelineRunType.Implementation);
        run.CurrentStep = PipelineStep.Completed;
        _runService.AddRun(run);

        // Act: call CompleteRunAsync without calling PostCompletionBookkeepingAsync or any hub method
        var result = await _sut.CompleteRunAsync("run-hub-crash", WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert: label was swapped by CompleteRunAsync — issue is not stuck at agent:in-progress
        result.Should().NotBeNull();
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Done, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_ConsolidationRun_SkipsLabelSwap()
    {
        // Consolidation runs have no associated issue label — the swap must be skipped.
        var consolidationRun = new PipelineRun
        {
            RunId = "run-consolidation",
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation,
            CurrentStep = PipelineStep.Completed
        };
        _runService.AddRun(consolidationRun);

        await _sut.CompleteRunAsync("run-consolidation", WorkItemStatus.Succeeded, CancellationToken.None);

        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteRunAsync_WithFinalLabel_UsesRunFinalLabel()
    {
        // FinalLabel on the run takes precedence over the terminalStatus-derived label.
        // In production, run.FinalLabel is populated by JobCompletionMapper.Apply (from payload.FinalLabel)
        // before CompleteRunAsync is called.
        var run = CreateRun("run-finallabel", PipelineRunType.Implementation);
        run.CurrentStep = PipelineStep.Completed;
        run.FinalLabel = AgentLabels.NeedsRefinement; // agent set needs-refinement
        _runService.AddRun(run);

        // Status says Succeeded but FinalLabel override takes precedence
        await _sut.CompleteRunAsync("run-finallabel", WorkItemStatus.Succeeded, CancellationToken.None);

        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.NeedsRefinement, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_ReviewRun_SwapsLabelViaRepoProviderAndPullRequestTarget()
    {
        // Review runs swap labels on the PR (via repo provider), not the issue.
        var run = CreateRun("run-review-complete", PipelineRunType.Review);
        run.CurrentStep = PipelineStep.Completed;
        _runService.AddRun(run);

        await _sut.CompleteRunAsync("run-review-complete", WorkItemStatus.Succeeded, CancellationToken.None);

        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "rp-1", "org/repo#1", AgentLabels.Done, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_FailedStatus_SwapsLabelToError()
    {
        var run = CreateRun("run-failed-complete", PipelineRunType.Implementation);
        run.CurrentStep = PipelineStep.Failed;
        _runService.AddRun(run);

        await _sut.CompleteRunAsync("run-failed-complete", WorkItemStatus.Failed, CancellationToken.None);

        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_InvalidFinalLabel_FallsBackToTerminalStatusLabel()
    {
        // A FinalLabel value not in AgentLabels.All is treated as unset — falls back to terminalStatus.
        var run = CreateRun("run-invalid-label", PipelineRunType.Implementation);
        run.CurrentStep = PipelineStep.Completed;
        run.FinalLabel = "some-unknown-label"; // not in AgentLabels.All
        _runService.AddRun(run);

        await _sut.CompleteRunAsync("run-invalid-label", WorkItemStatus.Succeeded, CancellationToken.None);

        // Must swap to Done (Succeeded-derived), NOT "some-unknown-label"
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Done, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), "some-unknown-label",
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── CancelRunAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task CancelRunAsync_RemovesRun_PersistsHistory_ClearsAgent_SwapsLabel()
    {
        // Arrange
        var run = CreateRun("run-cancel", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        _runService.AddRun(run);

        var entry = RegisterAgent("agent-1");
        entry.ActiveJobId = "run-cancel";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // Act
        var result = await _sut.CancelRunAsync("run-cancel", CancellationToken.None);

        // Assert: run returned with Cancelled state
        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-cancel");
        result.CurrentStep.Should().Be(PipelineStep.Cancelled);
        result.CompletedAtOffset.Should().NotBeNull();

        // Run removed from active
        _runService.GetRun("run-cancel").Should().BeNull();

        // History persisted
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-cancel"), It.IsAny<CancellationToken>()), Times.Once);

        // Agent cleared and transitioned to Idle
        var agent = _registry.GetByAgentId("agent-1");
        agent!.ActiveJobId.Should().BeNull();
        agent.Status.Should().Be(AgentStatus.Idle);

        // Label swapped to cancelled via issue provider (Implementation → Issue target)
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Cancelled, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelRunAsync_RunDoesNotExist_ReturnsNull()
    {
        // Act: no run was added with this ID
        var result = await _sut.CancelRunAsync("non-existent-run", CancellationToken.None);

        // Assert
        result.Should().BeNull();

        // No side effects
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelRunAsync_WithFailureReason_SetsReasonOnRun()
    {
        // Arrange
        var run = CreateRun("run-cancel-reason", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        _runService.AddRun(run);

        RegisterAgent("agent-1");

        // Act
        var result = await _sut.CancelRunAsync("run-cancel-reason", CancellationToken.None, "Cancelled — agent not available");

        // Assert
        result.Should().NotBeNull();
        result!.FailureReason.Should().Be("Cancelled — agent not available");
        result.CurrentStep.Should().Be(PipelineStep.Cancelled);
    }

    [Fact]
    public async Task CancelRunAsync_WithoutFailureReason_LeavesExistingReason()
    {
        // Arrange
        var run = CreateRun("run-cancel-no-reason", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        run.FailureReason = "Pre-existing reason";
        _runService.AddRun(run);

        RegisterAgent("agent-1");

        // Act — no failureReason passed (uses default null)
        var result = await _sut.CancelRunAsync("run-cancel-no-reason", CancellationToken.None);

        // Assert: existing reason preserved
        result.Should().NotBeNull();
        result!.FailureReason.Should().Be("Pre-existing reason");
        result.CurrentStep.Should().Be(PipelineStep.Cancelled);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static PipelineRun CreateRun(string runId, PipelineRunType runType)
    {
        return new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = runType
        };
    }

    private AgentEntry RegisterAgent(string agentId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = DotnetLabels
        }, $"conn-{agentId}");
    }
}

/// <summary>
/// Validates finding 1B-001: FailRunAsync must still clean up dedup tracker
/// even if AddRunToHistoryAsync throws, preventing stale entries.
/// </summary>
public sealed class RunLifecycleManagerResilienceTests
{
    private static readonly string[] DotnetLabels = ["dotnet"];
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly AgentReservationService _dispatcher;
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerResilienceTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _dispatcher = new AgentReservationService(_registry, _mockLogger.Object);

        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
            _dispatcher,
            _mockLogger.Object));
    }

    [Fact]
    public async Task FailRunAsync_WhenHistoryThrows_StillClearsAgentState()
    {
        // Arrange: set up a run that's "in-progress"
        var run = CreateRun("run-fail-history-err");
        run.AgentId = "agent-1";
        _runService.AddRun(run);
        var entry = RegisterAgent("agent-1");

        // Make history throw
        _mockHistoryService
            .Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB write failed"));

        // Act
        var result = await _sut.FailRunAsync("run-fail-history-err", "test failure", CancellationToken.None);

        // Assert: run was still returned (claimed successfully)
        result.Should().NotBeNull();

        // Agent state was cleared despite the history exception
        var agent = _registry.GetByAgentId("agent-1");
        agent!.ActiveJobId.Should().BeNull();
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task CompleteRunAsync_WhenHistoryThrows_StillReturnsRun()
    {
        // Arrange
        var run = CreateRun("run-complete-err");
        run.CurrentStep = PipelineStep.Completed; // Ensure terminal step so guard doesn't remap
        _runService.AddRun(run);

        _mockHistoryService
            .Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB write failed"));

        // Act
        var result = await _sut.CompleteRunAsync("run-complete-err", WorkItemStatus.Succeeded, CancellationToken.None);

        // Assert: run still returned despite history exception
        result.Should().NotBeNull();

        // Label swap must still fire — it runs after the history try/catch
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Done, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static PipelineRun CreateRun(string runId)
    {
        return new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation
        };
    }

    private AgentEntry RegisterAgent(string agentId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = DotnetLabels
        }, $"conn-{agentId}");
    }
}

/// <summary>
/// Verifies that <see cref="RunLifecycleManager.CancelRunAsync"/> calls
/// <see cref="IJobCleanupStrategy.TryDeleteJobForRunAsync"/> with the <see cref="RunId"/>
/// value type directly (not the unwrapped string).
/// </summary>
public sealed class RunLifecycleManagerJobCleanupTests
{
    private static readonly string[] DotnetLabels = ["dotnet"];

    private readonly Mock<ILogger> _mockLogger = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly Mock<IJobCleanupStrategy> _mockJobCleanup = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly AgentReservationService _dispatcher;
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerJobCleanupTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _dispatcher = new AgentReservationService(_registry, _mockLogger.Object);

        _mockJobCleanup
            .Setup(c => c.TryDeleteJobForRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
            _dispatcher,
            _mockLogger.Object,
            JobCleanup: _mockJobCleanup.Object));
    }

    [Fact]
    public async Task CancelRunAsync_CallsJobCleanupWithRunId_NotStringValue()
    {
        // Arrange
        const string runIdValue = "test-run-cleanup-1";
        var run = new PipelineRun
        {
            RunId = runIdValue,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation
        };
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        var result = await _sut.CancelRunAsync(runIdValue, CancellationToken.None);

        // Assert: run was cancelled
        result.Should().NotBeNull();
        result!.CurrentStep.Should().Be(PipelineStep.Cancelled);

        // Assert: TryDeleteJobForRunAsync was called with RunId value type (not raw string)
        _mockJobCleanup.Verify(
            c => c.TryDeleteJobForRunAsync(
                It.Is<RunId>(r => r.Value == runIdValue),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "CancelRunAsync must pass RunId directly to IJobCleanupStrategy, not .Value string");
    }

    [Fact]
    public async Task CancelRunAsync_WhenRunNotFound_DoesNotCallJobCleanup()
    {
        // Act: no run added — CancelRunAsync returns null early
        var result = await _sut.CancelRunAsync("nonexistent-run", CancellationToken.None);

        // Assert
        result.Should().BeNull();
        _mockJobCleanup.Verify(
            c => c.TryDeleteJobForRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private AgentEntry RegisterAgent(string agentId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = DotnetLabels
        }, $"conn-{agentId}");
    }
}

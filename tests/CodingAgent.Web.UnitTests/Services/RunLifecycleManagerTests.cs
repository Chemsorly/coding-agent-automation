using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Services;

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
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);

        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
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
    public async Task FailRunAsync_FailureReasonWithNewlines_LogsEscapedReason_KeepsRawReasonOnRun()
    {
        // The HTTP status path passes the agent-supplied WorkItemStatusRequest.ErrorMessage through as
        // failureReason, so CR/LF must be escaped in the terminal log entry (CodeQL cs/log-forging).
        // Escaping is a log-output concern only — the reason stored on the run stays verbatim.
        const string reason = "boom\r\n[ERR] forged entry";
        _runService.AddRun(CreateRun("run-fail-forged", PipelineRunType.Implementation));

        var result = await _sut.FailRunAsync("run-fail-forged", reason, CancellationToken.None);

        result!.FailureReason.Should().Be(reason);
        // {Reason} is the fifth property value of the terminal log entry.
        _mockLogger.Verify(l => l.Information(
            It.Is<string>(t => t.StartsWith("RunLifecycleManager.FailRunAsync:", StringComparison.Ordinal)),
            It.Is<object?[]>(a => a[4] as string == "boom\\r\\n[ERR] forged entry")), Times.Once);
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

    [Fact]
    public async Task FailRunAsync_WithFinalLabel_UsesRunFinalLabelInsteadOfError()
    {
        // FinalLabel on the run takes precedence over the hardcoded agent:error.
        // In production, run.FinalLabel is set by the analysis gate (AgentPhaseExecutor.FailPhaseAsync)
        // before FailRunAsync is called by the timeout/reconciliation path.
        // TODO: run.AgentId is intentionally not set here (omits RegisterAgent call), so ClearAgentStateAsync
        // executes its no-op path for a null/unregistered agent. If a future refactor makes ClearAgentStateAsync
        // non-null-safe for a missing AgentId (and any exception is swallowed before step 6), this test could give
        // a false-positive green. Consider setting run.AgentId and calling RegisterAgent to match the happy-path
        // pattern in FailRunAsync_RemovesRun_PersistsHistory_ClearsAgent_SwapsLabel. (Correctness/TestQuality review)
        var run = CreateRun("run-finallabel-fail", PipelineRunType.Implementation);
        run.FinalLabel = AgentLabels.NeedsRefinement;
        _runService.AddRun(run);

        // Act
        var result = await _sut.FailRunAsync("run-finallabel-fail", "Analysis gate: needs refinement", CancellationToken.None);

        // Assert: run was processed
        result.Should().NotBeNull();
        result!.CurrentStep.Should().Be(PipelineStep.Failed);

        // Label swapped to needs-refinement, NOT error
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.NeedsRefinement, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);

        // agent:error must NOT be applied
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.Error,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
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
    public async Task FailRunAsync_ConsolidationRun_SkipsLabelSwap()
    {
        // ProviderConfigId audit: FailRunAsync must not attempt a label swap for consolidation runs.
        // This is a pre-existing production bug now fixed as part of issue #3024.
        var consolidationRun = new PipelineRun
        {
            RunId = "run-consolidation-fail",
            IssueIdentifier = "consol-identifier",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation
        };
        _runService.AddRun(consolidationRun);

        await _sut.FailRunAsync("run-consolidation-fail", "consolidation agent error", CancellationToken.None);

        // No label swap — consolidation runs have no GitHub issue label
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never,
            "FailRunAsync must not attempt a label swap for consolidation runs (no issue label exists)");

        // History must still be written
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-consolidation-fail"), It.IsAny<CancellationToken>()),
            Times.Once,
            "FailRunAsync must still write history for consolidation runs after guard removal");
    }

    [Fact]
    public async Task CancelRunAsync_ConsolidationRun_SkipsLabelSwap()
    {
        // ProviderConfigId audit: CancelRunAsync must not attempt a label swap for consolidation runs.
        var consolidationRun = new PipelineRun
        {
            RunId = "run-consolidation-cancel",
            IssueIdentifier = "consol-identifier",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation
        };
        _runService.AddRun(consolidationRun);

        await _sut.CancelRunAsync("run-consolidation-cancel", CancellationToken.None);

        // No label swap — consolidation runs have no GitHub issue label
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never,
            "CancelRunAsync must not attempt a label swap for consolidation runs (no issue label exists)");

        // History must still be written
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-consolidation-cancel"), It.IsAny<CancellationToken>()),
            Times.Once,
            "CancelRunAsync must still write history for consolidation runs after guard removal");
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

    // ── FailRunWithLabelAsync (Issue #3009) ─────────────────────────────

    [Fact]
    public async Task FailRunWithLabelAsync_WithNeedsRefinementLabel_SwapsLabelToNeedsRefinement()
    {
        // Arrange: the HTTP path resolved agent:needs-refinement from request.Result
        var run = CreateRun("run-needs-refinement", PipelineRunType.Implementation);
        _runService.AddRun(run);

        // Act
        var result = await _sut.FailRunWithLabelAsync(
            "run-needs-refinement",
            "Analysis gate: issue needs refinement",
            AgentLabels.NeedsRefinement,
            CancellationToken.None);

        // Assert: run processed and final label recorded
        result.Should().NotBeNull();
        result!.FinalLabel.Should().Be(AgentLabels.NeedsRefinement,
            "FailRunWithLabelAsync must set run.FinalLabel from resolvedFinalLabel before terminal cleanup");
        result.CurrentStep.Should().Be(PipelineStep.Failed);

        // Label swap must use agent:needs-refinement, NOT agent:error
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.NeedsRefinement, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once,
            "FailRunWithLabelAsync must swap to agent:needs-refinement when resolvedFinalLabel is provided");

        // agent:error must NOT be applied
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.Error,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never,
            "agent:error must never be applied when resolvedFinalLabel is agent:needs-refinement");
    }

    [Fact]
    public async Task FailRunWithLabelAsync_WithNullResolvedLabel_SwapsLabelToError()
    {
        // Arrange: caller passes null (no override) → falls back to agent:error
        var run = CreateRun("run-nulllabel-fail", PipelineRunType.Implementation);
        _runService.AddRun(run);

        // Act
        var result = await _sut.FailRunWithLabelAsync(
            "run-nulllabel-fail",
            "Infrastructure failure",
            resolvedFinalLabel: null,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();

        // Label swap must be agent:error (standard fallback)
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once,
            "null resolvedFinalLabel must fall back to agent:error");

        // agent:needs-refinement must NOT be applied
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.NeedsRefinement,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailRunWithLabelAsync_ReviewRun_WithNeedsRefinement_UsesRepoProvider()
    {
        // Review runs route the label swap via repo provider + PullRequest target
        var run = CreateRun("run-review-nr", PipelineRunType.Review);
        run.AgentId = "agent-1";
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        // Act
        var result = await _sut.FailRunWithLabelAsync(
            "run-review-nr",
            "Review needs refinement",
            AgentLabels.NeedsRefinement,
            CancellationToken.None);

        // Assert: label swap goes to repo provider + PullRequest target for Review runs
        result.Should().NotBeNull();
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "rp-1", "org/repo#1", AgentLabels.NeedsRefinement, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once,
            "Review runs must route the NeedsRefinement label to the repo provider + PullRequest target");
    }

    [Fact]
    public async Task FailRunWithLabelAsync_HistoryRow_RecordsFinalLabel()
    {
        // The history row must record the resolved FinalLabel, not null.
        // Verified by asserting that AddRunToHistoryAsync is called with a PipelineRun
        // whose FinalLabel is set to the resolved value (set BEFORE RunTerminalCleanupAsync).
        var run = CreateRun("run-history-final-label", PipelineRunType.Implementation);
        _runService.AddRun(run);

        PipelineRun? capturedRun = null;
        _mockHistoryService
            .Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineRun, CancellationToken>((r, _) => capturedRun = r)
            .Returns(Task.CompletedTask);

        // Act
        await _sut.FailRunWithLabelAsync(
            "run-history-final-label",
            "needs refinement",
            AgentLabels.NeedsRefinement,
            CancellationToken.None);

        // Assert: the run passed to history already has FinalLabel set
        capturedRun.Should().NotBeNull("AddRunToHistoryAsync must be called");
        capturedRun!.FinalLabel.Should().Be(AgentLabels.NeedsRefinement,
            "history row must record the resolved FinalLabel; it is set before RunTerminalCleanupAsync is called");
    }

    [Fact]
    public async Task FailRunWithLabelAsync_RunDoesNotExist_ReturnsNull()
    {
        // Same null-return contract as FailRunAsync when run is not in memory
        var result = await _sut.FailRunWithLabelAsync(
            "ghost-run", "reason", AgentLabels.NeedsRefinement, CancellationToken.None);

        result.Should().BeNull();

        // No side effects
        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
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
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerResilienceTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);

        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
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
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerJobCleanupTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);

        _mockJobCleanup
            .Setup(c => c.TryDeleteJobForRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
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

/// <summary>
/// Tests for error/fallback paths not covered in the main test classes:
/// - WorkItemFallbackTransition invocation and failure handling
/// - FailRunAsync job cleanup
/// - CancelRunAsync history-throws resilience
/// - CancelRunAsync Review run type routing
/// - TransitionWorkItemToFailedAsync (public delegating method)
/// - Label swap fires even when history throws in FailRunAsync
/// </summary>
public sealed class RunLifecycleManagerErrorPathTests
{
    private static readonly string[] DotnetLabels = ["dotnet"];

    private readonly Mock<ILogger> _mockLogger = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly Mock<IJobCleanupStrategy> _mockJobCleanup = new();
    private readonly Mock<IWorkItemFallbackTransitionService> _mockFallbackTransition = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerErrorPathTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);

        _mockJobCleanup
            .Setup(c => c.TryDeleteJobForRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockFallbackTransition
            .Setup(f => f.TryFallbackChainAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
            _mockLogger.Object,
            JobCleanup: _mockJobCleanup.Object,
            WorkItemFallbackTransition: _mockFallbackTransition.Object));
    }

    // ── WorkItemFallbackTransition — FailRunAsync path ────────────────────

    [Fact]
    public async Task FailRunAsync_WithFallbackTransition_CallsTryFallbackChain_WithFailedStatus()
    {
        // RunId must be a valid GUID for TransitionWorkItemAsync to proceed
        var runId = Guid.NewGuid().ToString();
        var run = CreateRun(runId, PipelineRunType.Implementation);
        _runService.AddRun(run);

        await _sut.FailRunAsync(runId, "agent crashed", CancellationToken.None,
            FailureReason.AgentError);

        _mockFallbackTransition.Verify(f => f.TryFallbackChainAsync(
            It.Is<Guid>(g => g.ToString() == runId),
            WorkItemStatus.Failed,
            "agent crashed",
            FailureReason.AgentError,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailRunAsync_WithFallbackTransition_ReturnsFalse_LogsWarning_DoesNotThrow()
    {
        var runId = Guid.NewGuid().ToString();
        var run = CreateRun(runId, PipelineRunType.Implementation);
        _runService.AddRun(run);

        // Transition rejected (item already terminal)
        _mockFallbackTransition
            .Setup(f => f.TryFallbackChainAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var act = async () => await _sut.FailRunAsync(runId, "reason", CancellationToken.None);

        await act.Should().NotThrowAsync("rejected transition is non-fatal — run cleanup continues");

        // Label swap must still fire despite rejected transition
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.Error,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailRunAsync_WithFallbackTransition_Throws_LogsWarning_DoesNotPropagate()
    {
        var runId = Guid.NewGuid().ToString();
        var run = CreateRun(runId, PipelineRunType.Implementation);
        _runService.AddRun(run);

        _mockFallbackTransition
            .Setup(f => f.TryFallbackChainAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var act = async () => await _sut.FailRunAsync(runId, "reason", CancellationToken.None);

        // Non-OCE exception from fallback is swallowed — run cleanup must not abort
        await act.Should().NotThrowAsync();

        // Label swap must still fire despite fallback exception
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.Error,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailRunAsync_NonGuidRunId_FallbackTransition_Skipped_RunStillProcessed()
    {
        // TransitionWorkItemAsync short-circuits when runId is not a valid GUID.
        // FailRunAsync must still complete: label swap, agent clear, job cleanup.
        const string nonGuidRunId = "not-a-guid";
        var run = CreateRun(nonGuidRunId, PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        await _sut.FailRunAsync(nonGuidRunId, "reason", CancellationToken.None);

        // FallbackTransition NOT called (non-GUID runId skips the DB path)
        _mockFallbackTransition.Verify(f => f.TryFallbackChainAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatus>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Label swap still fires
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);

        // Job cleanup still fires
        _mockJobCleanup.Verify(c => c.TryDeleteJobForRunAsync(
            It.IsAny<RunId>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── FailRunAsync — job cleanup ────────────────────────────────────────

    [Fact]
    public async Task FailRunAsync_CallsJobCleanup_WithCorrectRunId()
    {
        const string runIdValue = "run-fail-with-cleanup";
        var run = CreateRun(runIdValue, PipelineRunType.Implementation);
        _runService.AddRun(run);

        await _sut.FailRunAsync(runIdValue, "timeout", CancellationToken.None);

        _mockJobCleanup.Verify(c => c.TryDeleteJobForRunAsync(
            It.Is<RunId>(r => r.Value == runIdValue),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── FailRunAsync — label swap fires even when history throws ──────────

    [Fact]
    public async Task FailRunAsync_WhenHistoryThrows_LabelSwapStillFires()
    {
        var run = CreateRun("run-history-fail-label", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        _mockHistoryService
            .Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB write failed"));

        await _sut.FailRunAsync("run-history-fail-label", "reason", CancellationToken.None);

        // Label swap must fire after the history try/catch
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Error, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── FailRunAsync — FinalLabel guard ───────────────────────────────────

    [Fact]
    public async Task FailRunAsync_WithInvalidFinalLabel_FallsBackToAgentError()
    {
        // A FinalLabel value not in AgentLabels.All must be treated as unset — falls back to agent:error.
        var runId = Guid.NewGuid().ToString();
        var run = CreateRun(runId, PipelineRunType.Implementation);
        run.FinalLabel = "some-unknown-label"; // not in AgentLabels.All
        _runService.AddRun(run);

        await _sut.FailRunAsync(runId, "reason", CancellationToken.None);

        // Must swap to agent:error (fallback), NOT the unknown label
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.Error,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), "some-unknown-label",
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailRunAsync_WithWontDoFinalLabel_UsesWontDoLabel()
    {
        // WontDo is a valid agent label — must be respected just like NeedsRefinement.
        var runId = Guid.NewGuid().ToString();
        var run = CreateRun(runId, PipelineRunType.Implementation);
        run.FinalLabel = AgentLabels.WontDo;
        _runService.AddRun(run);

        await _sut.FailRunAsync(runId, "Won't do", CancellationToken.None);

        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.WontDo,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.Error,
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── CancelRunAsync — resilience ───────────────────────────────────────

    [Fact]
    public async Task CancelRunAsync_WhenHistoryThrows_AgentStillCleared_LabelStillSwapped()
    {
        var run = CreateRun("run-cancel-history-err", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        _mockHistoryService
            .Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB write failed"));

        var result = await _sut.CancelRunAsync("run-cancel-history-err", CancellationToken.None);

        // Run still returned
        result.Should().NotBeNull();

        // Agent cleared despite history exception
        var agent = _registry.GetByAgentId("agent-1");
        agent!.ActiveJobId.Should().BeNull();
        agent.Status.Should().Be(AgentStatus.Idle);

        // Label swap still fires
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Cancelled, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelRunAsync_ReviewRun_SwapsLabelViaRepoProviderAndPullRequestTarget()
    {
        var run = CreateRun("run-cancel-review", PipelineRunType.Review);
        run.AgentId = "agent-1";
        _runService.AddRun(run);
        RegisterAgent("agent-1");

        await _sut.CancelRunAsync("run-cancel-review", CancellationToken.None);

        // Review cancelled: label via repo provider + PullRequest target
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "rp-1", "org/repo#1", AgentLabels.Cancelled, LabelTargetKind.PullRequest,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelRunAsync_RunHasNoAgent_LabelStillSwapped()
    {
        // Run exists but has no AgentId set — ClearAgentStateAsync should skip without error
        var run = CreateRun("run-cancel-no-agent", PipelineRunType.Implementation);
        // run.AgentId is null — not set
        _runService.AddRun(run);

        var result = await _sut.CancelRunAsync("run-cancel-no-agent", CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStep.Should().Be(PipelineStep.Cancelled);

        // Label swap still fires even with no agent to clear
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Cancelled, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelRunAsync_AgentDeregisteredBeforeCleanup_LabelStillSwapped()
    {
        // Agent was registered but deregistered between cancel being triggered and cleanup
        var run = CreateRun("run-cancel-ghost-agent", PipelineRunType.Implementation);
        run.AgentId = "ghost-agent";
        _runService.AddRun(run);
        // Deliberately NOT registering "ghost-agent" — simulates deregistration

        var act = async () => await _sut.CancelRunAsync("run-cancel-ghost-agent", CancellationToken.None);

        await act.Should().NotThrowAsync("missing agent in registry must not abort cancel");

        // Label swap still fires
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            "ip-1", "org/repo#1", AgentLabels.Cancelled, LabelTargetKind.Issue,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── TransitionWorkItemToFailedAsync (public delegating method) ────────

    [Fact]
    public async Task TransitionWorkItemToFailedAsync_CallsFallbackChain_WithFailedStatus()
    {
        var runId = Guid.NewGuid().ToString();

        await _sut.TransitionWorkItemToFailedAsync(runId, CancellationToken.None,
            errorMessage: "pipeline step failed", failureReason: FailureReason.QualityGateExhausted);

        _mockFallbackTransition.Verify(f => f.TryFallbackChainAsync(
            It.Is<Guid>(g => g.ToString() == runId),
            WorkItemStatus.Failed,
            "pipeline step failed",
            FailureReason.QualityGateExhausted,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransitionWorkItemToFailedAsync_NonGuidRunId_Skipped_DoesNotThrow()
    {
        // Non-GUID runId is silently skipped — no exception
        var act = async () =>
            await _sut.TransitionWorkItemToFailedAsync("not-a-guid", CancellationToken.None);

        await act.Should().NotThrowAsync();

        _mockFallbackTransition.Verify(f => f.TryFallbackChainAsync(
            It.IsAny<Guid>(), It.IsAny<WorkItemStatus>(),
            It.IsAny<string?>(), It.IsAny<FailureReason?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TransitionWorkItemToFailedAsync_FallbackThrows_DoesNotPropagate()
    {
        var runId = Guid.NewGuid().ToString();

        _mockFallbackTransition
            .Setup(f => f.TryFallbackChainAsync(
                It.IsAny<Guid>(), It.IsAny<WorkItemStatus>(),
                It.IsAny<string?>(), It.IsAny<FailureReason?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var act = async () =>
            await _sut.TransitionWorkItemToFailedAsync(runId, CancellationToken.None);

        await act.Should().NotThrowAsync("non-OCE exception from fallback must be swallowed");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static PipelineRun CreateRun(string runId, PipelineRunType runType = PipelineRunType.Implementation)
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
/// Characterization tests for the terminal-cleanup sequence in <see cref="RunLifecycleManager"/>.
/// Written before the Extract Method refactoring (issue #2795) to lock in the side-effect ordering
/// and per-path invariants so that the extraction cannot silently alter behaviour.
///
/// Covers:
/// - CompleteRunAsync does NOT call ClearAgentState or K8s cleanup (regression guards for extraction)
/// - Span receives the correct telemetry tags per terminal path (Fail → Error status, Cancel → cancelled tag)
/// - History persist is called on all terminal paths (ensures it is not accidentally dropped in extraction)
///
/// Note on span-ordering tests: OrchestratorRunService.RemoveRun always disposes the
/// OrchestratorActivity as a safety net before the terminal lifecycle methods call it. The
/// canonical ordering (history before span-dispose in the shared cleanup tail) is therefore a
/// structural property of the extracted method, not observable via IsStopped in a black-box test.
/// The tests here verify the observable invariants that regression-protect the extraction.
/// </summary>
public sealed class RunLifecycleManagerTerminalCleanupCharacterizationTests : IDisposable
{
    private static readonly string[] DotnetLabels = ["dotnet"];

    private readonly ActivityListener _activityListener;
    private readonly List<Activity> _stoppedActivities = [];

    private readonly Mock<ILogger> _mockLogger = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly Mock<IJobCleanupStrategy> _mockJobCleanup = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly RunLifecycleManager _sut;

    public RunLifecycleManagerTerminalCleanupCharacterizationTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == CodingAgent.Pipeline.Telemetry.PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _stoppedActivities.Add(a)
        };
        ActivitySource.AddActivityListener(_activityListener);

        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);

        _mockJobCleanup
            .Setup(c => c.TryDeleteJobForRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
            _mockLogger.Object,
            JobCleanup: _mockJobCleanup.Object));
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Regression guards: CompleteRunAsync must NOT call ClearAgentState or K8s cleanup ──

    [Fact]
    public async Task CompleteRunAsync_DoesNotCallJobCleanup()
    {
        // CompleteRunAsync intentionally omits K8s job deletion (that is Fail/Cancel's responsibility).
        // If the extraction accidentally pulls TryDeleteJobForRunAsync into the shared tail unconditionally,
        // this test will catch the regression.
        var run = CreateRun("run-complete-nocleanup", PipelineRunType.Implementation);
        run.CurrentStep = PipelineStep.Completed;
        _runService.AddRun(run);

        await _sut.CompleteRunAsync("run-complete-nocleanup", WorkItemStatus.Succeeded, CancellationToken.None);

        _mockJobCleanup.Verify(
            c => c.TryDeleteJobForRunAsync(It.IsAny<RunId>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "CompleteRunAsync must not invoke K8s job cleanup");

        // Sanity: label swap still fires (so the test isn't vacuous from a silent early-exit)
        // TODO: [WARNING] This assertion uses all It.IsAny<> — it does not verify *which* label was passed.
        //       An accidental label regression (e.g. swapping to AgentLabels.Error instead of the correct
        //       completion label) would not be caught here. Add a test that asserts the exact label value
        //       to close the gap for acceptance criterion 2 ("per-method FinalLabel/status fallback values
        //       are preserved exactly").
        _mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_DoesNotCallClearAgentState()
    {
        // CompleteRunAsync intentionally omits ClearAgentState (agent cleanup is Fail/Cancel's responsibility).
        // If the extraction accidentally pulls UpdateAgentFieldAsync into the shared tail, this test catches it.
        //
        // Setup: agent starts Idle (default for fresh registration), then we transition to Busy to create
        // a detectable state that ClearAgentState (→ Idle) would change.
        var run = CreateRun("run-complete-noagentclear", PipelineRunType.Implementation);
        run.AgentId = "agent-1";
        run.CurrentStep = PipelineStep.Completed;
        _runService.AddRun(run);
        RegisterAgent("agent-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);  // set Busy so ClearAgentState would be observable

        await _sut.CompleteRunAsync("run-complete-noagentclear", WorkItemStatus.Succeeded, CancellationToken.None);

        // If ClearAgentState had been called, the agent would be transitioned from Busy back to Idle.
        // If not called (correct behaviour), it remains Busy.
        var agent = _registry.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.Status.Should().Be(AgentStatus.Busy,
            "CompleteRunAsync must not clear agent state; only FailRunAsync and CancelRunAsync transition the agent to Idle");
    }

    // ── Span tag precision tests ───────────────────────────────────────────

    [Fact]
    public async Task FailRunAsync_SetsActivityStatusError_AndDisposesSpan()
    {
        // The existing OrchestratorExecutePipelineSpanTests only checks that the span is stopped;
        // it does not assert ActivityStatusCode.Error is set. This test locks in that contract.
        var run = CreateRunWithActivity("run-fail-span-tags");
        _runService.AddRun(run);

        await _sut.FailRunAsync("run-fail-span-tags", "something broke", CancellationToken.None);

        var stopped = _stoppedActivities.FirstOrDefault(a => a.OperationName == "ExecutePipeline");
        stopped.Should().NotBeNull("FailRunAsync must stop the ExecutePipeline span");
        stopped!.Status.Should().Be(ActivityStatusCode.Error,
            "FailRunAsync must set ActivityStatusCode.Error on the span");
        // TODO: [WARNING] CreateRunWithActivity does not explicitly set run.CurrentStep before calling FailRunAsync.
        //       FailRunAsync sets run.CurrentStep = PipelineStep.Failed, so if PipelineStep.Failed happens to be
        //       the default enum value (0), this assertion is vacuous — it would pass even if SetTag were removed.
        //       Set run.CurrentStep to a non-default value (e.g. PipelineStep.Implementing) in CreateRunWithActivity
        //       or in this test setup so the assertion proves the tag is actively written from the activity.
        stopped.GetTagItem("pipeline.final_step").Should().Be(PipelineStep.Failed.ToString(),
            "FailRunAsync must set pipeline.final_step to Failed");
    }

    [Fact]
    public async Task CancelRunAsync_SetsCancelledTag_AndDisposesSpan_NotErrorStatus()
    {
        // CancelRunAsync uses pipeline.cancelled=true instead of SetStatus(Error).
        // This test locks in the distinction — the span must NOT receive Error status for a graceful cancel.
        var run = CreateRunWithActivity("run-cancel-span-tags");
        _runService.AddRun(run);

        await _sut.CancelRunAsync("run-cancel-span-tags", CancellationToken.None);

        var stopped = _stoppedActivities.FirstOrDefault(a => a.OperationName == "ExecutePipeline");
        stopped.Should().NotBeNull("CancelRunAsync must stop the ExecutePipeline span");
        stopped!.GetTagItem("pipeline.cancelled").Should().Be(true,
            "CancelRunAsync must set pipeline.cancelled=true");
        stopped.GetTagItem("pipeline.final_step").Should().Be(PipelineStep.Cancelled.ToString(),
            "CancelRunAsync must set pipeline.final_step to Cancelled");
        // Graceful cancellation must NOT set Error status
        stopped.Status.Should().NotBe(ActivityStatusCode.Error,
            "CancelRunAsync must not set ActivityStatusCode.Error — use pipeline.cancelled tag instead");
    }

    // ── History is called on all terminal paths ────────────────────────────

    // TODO: [WARNING] The following label-fallback scenarios are not covered by any test in this class
    //       and should be added to satisfy acceptance criterion 2 ("per-method FinalLabel/status fallback
    //       values are preserved exactly"):
    //
    //   a) FailRunAsync label fallback: verify that when run.FinalLabel is null (or not in AgentLabels.All),
    //      TrySwapLabelAsync is called with AgentLabels.Error. Also verify that a valid run.FinalLabel
    //      takes precedence over the default. Without this, a typo in the errorLabel expression goes undetected.
    //
    //   b) CancelRunAsync label: verify that CancelRunAsync always swaps to AgentLabels.Cancelled regardless
    //      of run.FinalLabel (CancelRunAsync does not consult FinalLabel — unlike Fail). A test that sets
    //      run.FinalLabel = "agent:done" and asserts AgentLabels.Cancelled is still used closes this gap.
    //
    //   c) CompleteRunAsync consolidation-run path: verify that when
    //      run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId, TrySwapLabelAsync is
    //      NOT called (targetLabel stays null → RunTerminalCleanupAsync skips the swap). This is the only
    //      exerciser of the null-skip branch in RunTerminalCleanupAsync and currently has no test coverage.

    [Fact]
    public async Task FailRunAsync_CallsHistoryService()
    {
        // Ensures AddRunToHistoryAsync is not accidentally dropped in the extraction.
        var run = CreateRun("run-fail-history", PipelineRunType.Implementation);
        _runService.AddRun(run);

        await _sut.FailRunAsync("run-fail-history", "test", CancellationToken.None);

        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-fail-history"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelRunAsync_CallsHistoryService()
    {
        var run = CreateRun("run-cancel-history", PipelineRunType.Implementation);
        _runService.AddRun(run);

        await _sut.CancelRunAsync("run-cancel-history", CancellationToken.None);

        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-cancel-history"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompleteRunAsync_CallsHistoryService()
    {
        var run = CreateRun("run-complete-history", PipelineRunType.Implementation);
        run.CurrentStep = PipelineStep.Completed;
        _runService.AddRun(run);

        await _sut.CompleteRunAsync("run-complete-history", WorkItemStatus.Succeeded, CancellationToken.None);

        _mockHistoryService.Verify(h => h.AddRunToHistoryAsync(
            It.Is<PipelineRun>(r => r.RunId == "run-complete-history"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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

    private static PipelineRun CreateRunWithActivity(string runId)
    {
        var run = CreateRun(runId, PipelineRunType.Implementation);
        var activity = CodingAgent.Pipeline.Telemetry.PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        activity?.SetTag("pipeline.run_id", runId);
        run.OrchestratorActivity = activity;
        return run;
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


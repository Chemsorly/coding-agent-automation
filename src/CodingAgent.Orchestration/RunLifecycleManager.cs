using CodingAgent.Contracts;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

/// <summary>
/// Default implementation of <see cref="IRunLifecycleManager"/>.
/// Coordinates terminal state transitions across all stores:
/// - In-memory (OrchestratorRunService)
/// - Database (WorkItemFallbackTransitionService / WorkItemTransitionService) — null in test environments
/// - Agent registry (IAgentRegistryService)
/// - Labels (ILabelService)
/// - History (IPipelineRunHistoryService)
/// </summary>
public sealed class RunLifecycleManager : IRunLifecycleManager
{
    private readonly IOrchestratorRunService _runService;
    private readonly IWorkItemFallbackTransitionService? _workItemFallbackTransition;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IAgentRegistryService _registry;
    private readonly ILabelService _labelService;
    private readonly ILogger _logger;
    private readonly IJobCleanupStrategy? _jobCleanup;

    public RunLifecycleManager(
        RunLifecycleManagerDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.RunService);
        ArgumentNullException.ThrowIfNull(deps.HistoryService);
        ArgumentNullException.ThrowIfNull(deps.Registry);
        ArgumentNullException.ThrowIfNull(deps.LabelService);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _runService = deps.RunService;
        _workItemFallbackTransition = deps.WorkItemFallbackTransition;
        _historyService = deps.HistoryService;
        _registry = deps.Registry;
        _labelService = deps.LabelService;
        _logger = deps.Logger;
        _jobCleanup = deps.JobCleanup;
    }

    /// <inheritdoc />
    public async Task<PipelineRun?> FailRunAsync(RunId runId, string failureReason, CancellationToken ct, FailureReason? failureReasonEnum = null)
    {
        return await FailRunCoreAsync(runId, failureReason, resolvedFinalLabel: null, ct, failureReasonEnum);
    }

    /// <inheritdoc />
    public async Task<PipelineRun?> FailRunWithLabelAsync(RunId runId, string failureReason, string? resolvedFinalLabel, CancellationToken ct, FailureReason? failureReasonEnum = null)
    {
        return await FailRunCoreAsync(runId, failureReason, resolvedFinalLabel, ct, failureReasonEnum);
    }

    private async Task<PipelineRun?> FailRunCoreAsync(RunId runId, string failureReason, string? resolvedFinalLabel, CancellationToken ct, FailureReason? failureReasonEnum)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);

        // Atomic claim: RemoveRun returns null if another thread already processed this run
        var run = _runService.RemoveRun(runId);
        if (run is null)
        {
            _logger.Debug("FailRunAsync: run {RunId} not found (already processed)", runId);
            return null;
        }

        // Record recently-completed so OrphanedLabelRecoveryService won't race with us
        _runService.MarkRecentlyCompleted(run.IssueIdentifier, run.IssueProviderConfigId);

        // 1. Mark the run as failed
        run.FailureReason = failureReason;
        run.MarkCompleted();
        run.CurrentStep = PipelineStep.Failed;

        // 2. Transition WorkItem in DB (no-op when WorkItemFallbackTransitionService is not registered)
        await TransitionWorkItemAsync(runId, WorkItemStatus.Failed, ct, failureReason, failureReasonEnum);

        // TODO: [WARNING] ClearAgentState is called here (before history-persist), which deviates from the
        //       originally documented ordering (history → ClearAgentState → label-swap). This is structurally
        //       safe because ClearAgentStateAsync only mutates the agent registry entry and does not affect
        //       run.AgentId, so history-persist is unaffected. However, it means ClearAgentState is outside the
        //       "single canonical place" in RunTerminalCleanupAsync. Consider moving it inside
        //       RunTerminalCleanupAsync (parameterised) to restore the documented ordering.
        // 3. Clear agent state
        await ClearAgentStateAsync(run.AgentId);

        // 4. Apply the pre-resolved label from the HTTP path (if provided), then compute target label.
        //    resolvedFinalLabel is set by WorkItemStatusTransitionService when the HTTP POST payload
        //    carries agent:needs-refinement; it is null for all other callers (timeouts, reconciliation,
        //    operator cancel). When non-null, it overrides the default agent:error fallback.
        if (resolvedFinalLabel is not null && AgentLabels.All.Contains(resolvedFinalLabel))
            run.FinalLabel = resolvedFinalLabel;

        // 5. Compute the target label — skip for consolidation runs (they have no issue label),
        //    respect pipeline-determined FinalLabel, fall back to agent:error.
        string? errorLabel = null;
        if (run.IssueProviderConfigId != ConsolidationConstants.ProviderConfigId)
        {
            // ResolveAgentLabel(Failed, _) always returns a non-null value (either FinalLabel or
            // AgentLabels.Error), so the ?? fallback is purely defensive.
            // Note: run.FinalLabel may have been set above (from resolvedFinalLabel) or earlier by
            // AgentPhaseExecutor. ResolveAgentLabel re-validates it against AgentLabels.All, which is
            // intentional — the double-check is harmless and guards against concurrent mutations.
            errorLabel = CompletionOutcomeResolver.ResolveAgentLabel(WorkItemStatus.Failed, run.FinalLabel)
                ?? AgentLabels.Error;
        }

        // 6. Shared terminal cleanup: history-persist → label-swap
        await RunTerminalCleanupAsync(run, errorLabel, ct);

        // 7. Delete K8s Job to prevent pod retries consuming backoffLimit (mirrors CancelRunAsync step 6).
        // Best-effort: if the Job is already gone or K8s is unavailable, the warning is logged by KubernetesJobCleanup.
        if (_jobCleanup is not null)
            await _jobCleanup.TryDeleteJobForRunAsync(runId, ct);

        _logger.Information(
            "RunLifecycleManager.FailRunAsync: run {RunId} terminal (status=Failed, issue={IssueIdentifier}, step={Step}, highWater={HighWater}, reason={Reason}, agent={AgentId})",
            runId, run.IssueIdentifier, run.CurrentStep, run.HighWaterMark, LogSanitizer.SanitizeForLog(failureReason), run.AgentId ?? "none");

        return run;
    }

    /// <inheritdoc />
    public async Task<PipelineRun?> CompleteRunAsync(RunId runId, WorkItemStatus terminalStatus, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);

        var run = _runService.RemoveRun(runId);
        if (run is null)
        {
            _logger.Debug("CompleteRunAsync: run {RunId} not found (already processed)", runId);
            return null;
        }

        // Record recently-completed so OrphanedLabelRecoveryService won't race with us
        _runService.MarkRecentlyCompleted(run.IssueIdentifier, run.IssueProviderConfigId);

        // Ensure CurrentStep is terminal before persist (defense-in-depth).
        // Normal flow: JobCompletionMapper.Apply already sets terminal step via payload.FinalStep.
        // This guard catches edge cases where CurrentStep was not set (e.g., legacy heartbeat paths).
        if (!run.CurrentStep.IsTerminal())
        {
            var mapped = terminalStatus == WorkItemStatus.Succeeded
                ? PipelineStep.Completed
                : PipelineStep.Failed;
            _logger.Warning(
                "CompleteRunAsync: run {RunId} has non-terminal CurrentStep={Step}, mapping to {Mapped}",
                runId, run.CurrentStep, mapped);
            run.CurrentStep = mapped;
        }

        // Mark completion timestamp. FailRunCoreAsync and CancelRunAsync both call run.MarkCompleted()
        // before their respective RunTerminalCleanupAsync. CompleteRunAsync must do the same so that
        // PipelineRunSummary.CompletedAtOffset is non-null in history — GetStatusDisplay returns
        // "Running" when CompletedAtOffset is null, which caused the E2E consolidation success/fail
        // scenarios to show "Running" instead of "Succeeded"/"Failed" in the history table.
        // TODO [WARNING]: For non-consolidation runs that arrive via JobCompletionMapper,
        // run.MarkCompleted(payload.CompletedAt) is called by JobCompletionMapper.Apply before the
        // hub dispatches to RunLifecycleManager, so CompletedAtOffset will already be non-null and
        // this guard is a no-op on the normal pipeline completion path. The guard correctly preserves
        // the caller-supplied timestamp in that case. However, this contract is undocumented: a future
        // caller that reaches CompleteRunAsync with a pre-populated CompletedAtOffset expecting the
        // timestamp to be preserved will get the correct behaviour, but only because the guard skips
        // re-stamping — not because the intent is stated. Document the invariant explicitly if this
        // method is called from additional sites. (DotNetSpecialist WARNING)
        if (run.CompletedAtOffset is null)
            run.MarkCompleted();

        // 1. Transition WorkItem in DB
        await TransitionWorkItemAsync(runId, terminalStatus, ct, errorMessage, failureReason);

        // 2. Compute the target label — skip for consolidation runs (they have no issue label).
        //    Best-effort fallback for hub crash scenario — PostCompletionBookkeepingAsync is the
        //    authoritative path but may not execute if the hub pod is killed between DB write and
        //    the hub method returning. AgentLabelOperations.SwapAsync is idempotent, so calling
        //    this here and in PostCompletionBookkeepingAsync in the happy path is safe.
        string? label = null;
        if (run.IssueProviderConfigId != ConsolidationConstants.ProviderConfigId)
        {
            label = CompletionOutcomeResolver.ResolveAgentLabel(terminalStatus, run.FinalLabel);
        }

        // 3. Shared terminal cleanup: history-persist → label-swap
        //    Note: CompleteRunAsync intentionally does NOT call ClearAgentState or K8s job cleanup.
        //    Those steps are the responsibility of FailRunAsync and CancelRunAsync only.
        await RunTerminalCleanupAsync(run, label, ct);

        _logger.Information(
            "RunLifecycleManager.CompleteRunAsync: run {RunId} terminal (status={Status}, issue={IssueIdentifier}, step={Step}, highWater={HighWater}, agent={AgentId})",
            runId, terminalStatus, run.IssueIdentifier, run.CurrentStep, run.HighWaterMark, run.AgentId ?? "none");

        return run;
    }

    /// <inheritdoc />
    public async Task<PipelineRun?> CancelRunAsync(RunId runId, CancellationToken ct, string? failureReason = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);

        var run = _runService.RemoveRun(runId);
        if (run is null)
        {
            _logger.Debug("CancelRunAsync: run {RunId} not found (already processed)", runId);
            return null;
        }

        // Record recently-completed so OrphanedLabelRecoveryService won't race with us
        _runService.MarkRecentlyCompleted(run.IssueIdentifier, run.IssueProviderConfigId);

        // 1. Mark the run as cancelled
        if (failureReason is not null)
            run.FailureReason = failureReason;
        run.MarkCompleted();
        run.CurrentStep = PipelineStep.Cancelled;

        // 2. Transition WorkItem in DB
        await TransitionWorkItemAsync(runId, WorkItemStatus.Cancelled, ct);

        // TODO: [WARNING] ClearAgentState is called here (before history-persist via
        //       RunTerminalCleanupAsync), deviating from the originally documented ordering
        //       (history → ClearAgentState → label-swap). This is safe (ClearAgentStateAsync
        //       only mutates the agent registry, not run.AgentId), but ClearAgentState is outside the
        //       shared cleanup tail. Consider moving it inside RunTerminalCleanupAsync (parameterised)
        //       to restore the documented ordering and keep all terminal steps in one place.
        // 3. Clear agent state
        await ClearAgentStateAsync(run.AgentId);

        // 4. Shared terminal cleanup: history-persist → label-swap
        //    Skip the label swap for consolidation runs (they have no issue label to swap).
        var cancelLabel = run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId
            ? null
            : AgentLabels.Cancelled;
        await RunTerminalCleanupAsync(run, cancelLabel, ct);

        // 5. Delete K8s Job to prevent pod retries consuming backoffLimit.
        if (_jobCleanup is not null)
            await _jobCleanup.TryDeleteJobForRunAsync(runId, ct);

        _logger.Information(
            "RunLifecycleManager.CancelRunAsync: run {RunId} terminal (status=Cancelled, issue={IssueIdentifier}, step={Step}, highWater={HighWater}, agent={AgentId}, reason={Reason})",
            runId, run.IssueIdentifier, run.CurrentStep, run.HighWaterMark, run.AgentId ?? "none", failureReason ?? "none");

        return run;
    }

    /// <inheritdoc />
    public async Task AgentAcceptedRunAsync(RunId runId, AgentId agentId, IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderConfigId, ProviderConfigId repoProviderConfigId,
        PipelineRunType runType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        // NOTE: Using ThrowIfNull(agentId.Value) rather than ThrowIfNullOrEmpty(agentId.Value, nameof(agentId))
        // because ThrowIfNull on a struct field reports "Value" as the parameter name in exceptions
        // rather than "agentId". Prefer ThrowIfNullOrEmpty with nameof if this is refactored.
        ArgumentNullException.ThrowIfNull(agentId.Value);

        // 1. Set AgentId on the in-memory PipelineRun and persist
        var run = _runService.GetRun(runId);
        if (run is not null)
        {
            run.AgentId = agentId.Value;
            _runService.ReplaceRun(run);
        }
        else
        {
            _logger.Warning(
                "AgentAcceptedRunAsync: run {RunId} not found in store — AgentId {AgentId} not persisted (run may have expired or been removed by another replica)",
                runId, agentId);
        }

        // 2. Set ActiveJobId on agent + transition to Busy
        var agent = await _registry.GetByAgentIdAsync(agentId, ct);
        if (agent is not null)
        {
            await _registry.UpdateAgentFieldAsync(agentId, AgentFieldNames.ActiveJobId, runId.Value);
            _registry.TransitionStatus(agentId, AgentStatus.Busy);
        }
        else
        {
            _logger.Warning(
                "AgentAcceptedRunAsync: agent {AgentId} not found in registry — activeJobId not set, status not transitioned to Busy",
                agentId);
        }

        // 3. Swap label to agent:in-progress (best-effort)
        // For Review runs, use repoProviderConfigId (PR labels live on repo provider).
        // For all others, use issueProviderConfigId.
        var providerForLabel = runType == PipelineRunType.Review
            ? repoProviderConfigId
            : issueProviderConfigId;
        var targetKind = runType == PipelineRunType.Review
            ? LabelTargetKind.PullRequest
            : LabelTargetKind.Issue;
        // TODO: [WARNING] TrySwapLabelAsync now returns Task<bool> (true = applied, false = non-fatal
        // exception swallowed). The bool is intentionally discarded here — this is fire-and-forget.
        // If diagnostics on swap failures are ever needed, capture and log the result.
        await _labelService.TrySwapLabelAsync(providerForLabel, issueIdentifier, AgentLabels.InProgress, targetKind, _logger, "RunLifecycleManager", ct);

        _logger.Information(
            "RunLifecycleManager.AgentAcceptedRunAsync: agent {AgentId} accepted run {RunId} for issue {IssueIdentifier}",
            agentId, runId, issueIdentifier);
    }

    /// <inheritdoc />
    public async Task TransitionWorkItemToFailedAsync(RunId runId, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null)
    {
        await TransitionWorkItemAsync(runId, WorkItemStatus.Failed, ct, errorMessage, failureReason);
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Performs the shared terminal-cleanup tail common to all three terminal paths:
    /// <list type="number">
    ///   <item>Persist run to history (try/catch — downstream cleanup always runs)</item>
    ///   <item>Swap the issue/PR label to the computed terminal label (skipped when <paramref name="targetLabel"/> is null)</item>
    /// </list>
    /// This is the single canonical location for the history-persist → label-swap ordering that was
    /// previously duplicated across <c>FailRunAsync</c>, <c>CancelRunAsync</c>, and
    /// <c>CompleteRunAsync</c> (issue #2795).
    /// </summary>
    /// <param name="run">The terminal run.</param>
    /// <param name="targetLabel">Label to swap to, or <c>null</c> to skip the swap (consolidation runs, unrecognised status).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RunTerminalCleanupAsync(PipelineRun run, string? targetLabel, CancellationToken ct)
    {
        // Step 1: Persist to history — wrapped in try/catch so downstream cleanup always runs.
        try
        {
            await _historyService.AddRunToHistoryAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "RunTerminalCleanupAsync: failed to persist run {RunId} to history (run data may be lost)", run.RunId);
        }

        // Step 2: Swap label — skip for consolidation runs (targetLabel null) or unrecognised status.
        if (targetLabel is not null)
        {
            // TODO: [WARNING] TrySwapLabelAsync now returns Task<bool> (true = applied, false = non-fatal
            // exception swallowed). The bool is intentionally discarded here — this is fire-and-forget.
            // If diagnostics on swap failures are ever needed, capture and log the result.
            await _labelService.TrySwapLabelAsync(run, targetLabel, _logger, "RunLifecycleManager", ct);
        }
    }

    private async Task TransitionWorkItemAsync(RunId runId, WorkItemStatus status, CancellationToken ct, string? errorMessage = null, FailureReason? failureReason = null)
    {
        if (_workItemFallbackTransition is null || !Guid.TryParse(runId.Value, out var workItemId))
            return;

        try
        {
            var result = await _workItemFallbackTransition.TryFallbackChainAsync(workItemId, status, errorMessage, failureReason, ct);
            if (!result)
            {
                _logger.Warning(
                    "RunLifecycleManager: WorkItem {WorkItemId} transition to {Status} rejected (may already be terminal)",
                    workItemId, status);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "RunLifecycleManager: WorkItem {WorkItemId} transition to {Status} failed (non-fatal)", workItemId, status);
        }
    }

    private async Task ClearAgentStateAsync(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId))
            return;

        var agent = await _registry.GetByAgentIdAsync(new AgentId(agentId), CancellationToken.None);
        if (agent is null)
        {
            _logger.Warning(
                "ClearAgentState: agent {AgentId} not found in registry (hash expired or agent deregistered) — skipping status transition",
                agentId);
            return;
        }

        await _registry.UpdateAgentFieldAsync(new AgentId(agentId), AgentFieldNames.ActiveJobId, null);
        await _registry.UpdateAgentFieldAsync(new AgentId(agentId), AgentFieldNames.OrphanRestoredAt, null);

        _registry.TransitionStatus(new AgentId(agentId), AgentStatus.Idle);
    }
}

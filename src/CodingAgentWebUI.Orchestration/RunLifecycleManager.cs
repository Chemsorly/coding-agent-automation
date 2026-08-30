using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Default implementation of <see cref="IRunLifecycleManager"/>.
/// Coordinates terminal state transitions across all stores:
/// - In-memory (OrchestratorRunService)
/// - Database (WorkItemFallbackTransitionService / WorkItemTransitionService) — null in test environments
/// - Agent registry (IAgentRegistryService)
/// - Labels (ILabelService)
/// - History (IPipelineRunHistoryService)
/// - Dedup tracker (JobDeduplicationGuardService)
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
        ArgumentNullException.ThrowIfNull(deps.Dispatcher);
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

        // 3. Persist to history — wrapped in try/catch so downstream cleanup still runs
        try
        {
            await _historyService.AddRunToHistoryAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "FailRunAsync: failed to persist run {RunId} to history (run data may be lost)", runId);
        }


        // 5. Clear agent state
        await ClearAgentStateAsync(run.AgentId);

        // 6. Swap label to error
        await _labelService.TrySwapLabelAsync(run, AgentLabels.Error, _logger, "RunLifecycleManager", ct);

        // 7. Delete K8s Job to prevent pod retries consuming backoffLimit (mirrors CancelRunAsync step 7).
        // Best-effort: if the Job is already gone or K8s is unavailable, the warning is logged by KubernetesJobCleanup.
        if (_jobCleanup is not null)
            await _jobCleanup.TryDeleteJobForRunAsync(runId, ct);

        _logger.Information(
            "RunLifecycleManager.FailRunAsync: run {RunId} terminal (status=Failed, step={Step}, highWater={HighWater}, reason={Reason}, agent={AgentId})",
            runId, run.CurrentStep, run.HighWaterMark, failureReason, run.AgentId ?? "none");

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

        // 1. Transition WorkItem in DB
        await TransitionWorkItemAsync(runId, terminalStatus, ct, errorMessage, failureReason);

        // 2. Persist to history — wrapped in try/catch so downstream cleanup still runs
        try
        {
            await _historyService.AddRunToHistoryAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "CompleteRunAsync: failed to persist run {RunId} to history (run data may be lost)", runId);
        }


        _logger.Information(
            "RunLifecycleManager.CompleteRunAsync: run {RunId} terminal (status={Status}, step={Step}, highWater={HighWater}, agent={AgentId})",
            runId, terminalStatus, run.CurrentStep, run.HighWaterMark, run.AgentId ?? "none");

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

        // 3. Persist to history — wrapped in try/catch so downstream cleanup still runs
        try
        {
            await _historyService.AddRunToHistoryAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "CancelRunAsync: failed to persist run {RunId} to history (run data may be lost)", runId);
        }


        // 5. Clear agent state
        await ClearAgentStateAsync(run.AgentId);

        // 6. Swap label
        await _labelService.TrySwapLabelAsync(run, AgentLabels.Cancelled, _logger, "RunLifecycleManager", ct);

        // 7. Delete K8s Job to prevent pod retries (backoffLimit)
        // TODO: Consider making _jobCleanup non-nullable and using GetRequiredService in all DI registrations
        // to resolve mode differences entirely at DI registration time (per design goal).
        if (_jobCleanup is not null)
            await _jobCleanup.TryDeleteJobForRunAsync(runId, ct);

        _logger.Information(
            "RunLifecycleManager.CancelRunAsync: run {RunId} terminal (status=Cancelled, step={Step}, highWater={HighWater}, agent={AgentId}, reason={Reason})",
            runId, run.CurrentStep, run.HighWaterMark, run.AgentId ?? "none", failureReason ?? "none");

        return run;
    }

    /// <inheritdoc />
    public async Task AgentAcceptedRunAsync(RunId runId, AgentId agentId, IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderConfigId, ProviderConfigId repoProviderConfigId,
        PipelineRunType runType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        // TODO: Replace ArgumentNullException.ThrowIfNull(agentId.Value) with
        // ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)) — ThrowIfNull on a struct
        // field reports "Value" as the parameter name in exceptions rather than "agentId".
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
            await _registry.UpdateAgentFieldAsync(agentId, "activeJobId", runId.Value);
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

        var agent = await _registry.GetByAgentIdAsync(new AgentId(agentId));
        if (agent is null)
        {
            _logger.Warning(
                "ClearAgentState: agent {AgentId} not found in registry (hash expired or agent deregistered) — skipping status transition",
                agentId);
            return;
        }

        await _registry.UpdateAgentFieldAsync(new AgentId(agentId), "activeJobId", null);
        await _registry.UpdateAgentFieldAsync(new AgentId(agentId), "orphanRestoredAt", null);

        _registry.TransitionStatus(new AgentId(agentId), AgentStatus.Idle);
    }

}

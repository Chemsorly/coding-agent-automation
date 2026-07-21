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
/// - Database (WorkItemTransitionService) — null in Legacy mode
/// - Agent registry (IAgentRegistryService)
/// - Labels (ILabelService)
/// - History (IPipelineRunHistoryService)
/// - Dedup tracker (JobDeduplicationGuardService)
/// </summary>
public sealed class RunLifecycleManager : IRunLifecycleManager
{
    private readonly IOrchestratorRunService _runService;
    private readonly WorkItemTransitionService? _workItemTransition;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IAgentRegistryService _registry;
    private readonly ILabelService _labelService;
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly ILogger _logger;
    private readonly IJobCleanupStrategy? _jobCleanup;

    public RunLifecycleManager(
        IOrchestratorRunService runService,
        IPipelineRunHistoryService historyService,
        IAgentRegistryService registry,
        ILabelService labelService,
        JobDeduplicationGuardService dispatcher,
        ILogger logger,
        WorkItemTransitionService? workItemTransition = null,
        IJobCleanupStrategy? jobCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(labelService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _runService = runService;
        _workItemTransition = workItemTransition;
        _historyService = historyService;
        _registry = registry;
        _labelService = labelService;
        _dispatcher = dispatcher;
        _logger = logger;
        _jobCleanup = jobCleanup;
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

        // 1. Mark the run as failed
        run.FailureReason = failureReason;
        run.MarkCompleted();
        run.CurrentStep = PipelineStep.Failed;

        // 2. Transition WorkItem in DB (no-op in Legacy mode)
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

        // 4. Mark issue complete in dedup tracker
        _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

        // 5. Clear agent state
        ClearAgentState(run.AgentId);

        // 6. Swap label to error
        await _labelService.TrySwapLabelAsync(run, AgentLabels.Error, _logger, "RunLifecycleManager", ct);

        _logger.Information(
            "RunLifecycleManager.FailRunAsync: run {RunId} failed (reason={Reason}, agent={AgentId})",
            runId, failureReason, run.AgentId);

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

        // 3. Mark issue complete in dedup tracker
        _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

        _logger.Information(
            "RunLifecycleManager.CompleteRunAsync: run {RunId} completed (status={Status})",
            runId, terminalStatus);

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

        // 4. Mark issue complete in dedup tracker
        _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

        // 5. Clear agent state
        ClearAgentState(run.AgentId);

        // 6. Swap label
        await _labelService.TrySwapLabelAsync(run, AgentLabels.Cancelled, _logger, "RunLifecycleManager", ct);

        // 7. Delete K8s Job to prevent pod retries (backoffLimit)
        // TODO: Consider making _jobCleanup non-nullable and using GetRequiredService in all DI registrations
        // to resolve mode differences entirely at DI registration time (per design goal).
        if (_jobCleanup is not null)
            await _jobCleanup.TryDeleteJobForRunAsync(runId.Value, ct);

        _logger.Information(
            "RunLifecycleManager.CancelRunAsync: run {RunId} cancelled (agent={AgentId})",
            runId, run.AgentId);

        return run;
    }

    /// <inheritdoc />
    public async Task AgentAcceptedRunAsync(RunId runId, string agentId, string issueIdentifier,
        string issueProviderConfigId, string repoProviderConfigId,
        PipelineRunType runType, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        ArgumentNullException.ThrowIfNull(agentId);

        // 1. Set AgentId on the in-memory PipelineRun
        var run = _runService.GetRun(runId);
        if (run is not null)
            run.AgentId = agentId;

        // 2. Set ActiveJobId on agent + transition to Busy
        var agent = _registry.GetByAgentId(agentId);
        if (agent is not null)
        {
            lock (agent.SyncRoot)
            {
                agent.ActiveJobId = runId.Value;
            }
            _registry.TransitionStatus(agentId, AgentStatus.Busy);
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
        if (_workItemTransition is null || !Guid.TryParse(runId.Value, out var workItemId))
            return;

        try
        {
            var result = await _workItemTransition.TransitionAsync(workItemId, status, item =>
            {
                if (status is WorkItemStatus.Failed or WorkItemStatus.Succeeded or WorkItemStatus.Cancelled)
                    item.CompletedAt = DateTimeOffset.UtcNow;
                if (status == WorkItemStatus.Failed)
                {
                    item.ErrorMessage = errorMessage ?? "Job failed without specific error information";
                    item.FailureReason ??= failureReason ?? FailureReason.AgentError;
                }
            }, ct);

            if (!result)
            {
                // Two-step fallback: Dispatched → Running → terminal
                if (status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
                {
                    var intermediate = await _workItemTransition.TransitionAsync(workItemId, WorkItemStatus.Running, ct: ct);
                    if (intermediate)
                    {
                        await _workItemTransition.TransitionAsync(workItemId, status, item =>
                        {
                            item.CompletedAt = DateTimeOffset.UtcNow;
                            if (status == WorkItemStatus.Failed)
                            {
                                item.ErrorMessage = errorMessage ?? "Job failed without specific error information";
                                item.FailureReason ??= failureReason ?? FailureReason.AgentError;
                            }
                        }, ct);
                    }
                    else
                    {
                        // Third fallback: recover from infrastructure-failure-induced Failed state
                        var recovered = await _workItemTransition.TryRecoverFromInfrastructureFailureAsync(
                            workItemId, status, item =>
                            {
                                item.CompletedAt = DateTimeOffset.UtcNow;
                                if (status == WorkItemStatus.Failed)
                                {
                                    item.ErrorMessage = errorMessage ?? "Job failed without specific error information";
                                    item.FailureReason ??= failureReason ?? FailureReason.AgentError;
                                }
                            }, ct);
                        if (recovered)
                            _logger.Warning("Recovered WorkItem {RunId} from delivery-timeout Failed to {Status} via lifecycle manager", runId, status);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "RunLifecycleManager: WorkItem {RunId} transition to {Status} failed (non-fatal)", runId, status);
        }
    }

    private void ClearAgentState(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId))
            return;

        var agent = _registry.GetByAgentId(agentId);
        if (agent is null)
            return;

        lock (agent.SyncRoot)
        {
            agent.ActiveJobId = null;
            agent.OrphanRestoredAt = null;
        }

        _registry.TransitionStatus(agentId, AgentStatus.Idle);
    }

}

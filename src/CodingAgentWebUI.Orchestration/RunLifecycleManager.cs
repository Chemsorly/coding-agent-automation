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
/// - Labels (ILabelSwapper)
/// - History (IPipelineRunHistoryService)
/// - Dedup tracker (JobDispatcherService)
/// </summary>
public sealed class RunLifecycleManager : IRunLifecycleManager
{
    private readonly IOrchestratorRunService _runService;
    private readonly WorkItemTransitionService? _workItemTransition;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IAgentRegistryService _registry;
    private readonly ILabelSwapper _labelSwapper;
    private readonly JobDispatcherService _dispatcher;
    private readonly ILogger _logger;
    private readonly IJobCleanupStrategy? _jobCleanup;

    public RunLifecycleManager(
        IOrchestratorRunService runService,
        IPipelineRunHistoryService historyService,
        IAgentRegistryService registry,
        ILabelSwapper labelSwapper,
        JobDispatcherService dispatcher,
        ILogger logger,
        WorkItemTransitionService? workItemTransition = null,
        IJobCleanupStrategy? jobCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(labelSwapper);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _runService = runService;
        _workItemTransition = workItemTransition;
        _historyService = historyService;
        _registry = registry;
        _labelSwapper = labelSwapper;
        _dispatcher = dispatcher;
        _logger = logger;
        _jobCleanup = jobCleanup;
    }

    /// <inheritdoc />
    public async Task<PipelineRun?> FailRunAsync(string runId, string failureReason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);

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
        await TransitionWorkItemAsync(runId, WorkItemStatus.Failed, ct);

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
        await TrySwapLabelAsync(run, AgentLabels.Error, ct);

        _logger.Information(
            "RunLifecycleManager.FailRunAsync: run {RunId} failed (reason={Reason}, agent={AgentId})",
            runId, failureReason, run.AgentId);

        return run;
    }

    /// <inheritdoc />
    public async Task<PipelineRun?> CompleteRunAsync(string runId, WorkItemStatus terminalStatus, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);

        var run = _runService.RemoveRun(runId);
        if (run is null)
        {
            _logger.Debug("CompleteRunAsync: run {RunId} not found (already processed)", runId);
            return null;
        }

        // 1. Transition WorkItem in DB
        await TransitionWorkItemAsync(runId, terminalStatus, ct);

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
    public async Task<PipelineRun?> CancelRunAsync(string runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);

        var run = _runService.RemoveRun(runId);
        if (run is null)
        {
            _logger.Debug("CancelRunAsync: run {RunId} not found (already processed)", runId);
            return null;
        }

        // 1. Mark the run as cancelled
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
        await TrySwapLabelAsync(run, AgentLabels.Cancelled, ct);

        // 7. Delete K8s Job to prevent pod retries (backoffLimit)
        // TODO: Consider making _jobCleanup non-nullable and using GetRequiredService in all DI registrations
        // to resolve mode differences entirely at DI registration time (per design goal).
        if (_jobCleanup is not null)
            await _jobCleanup.TryDeleteJobForRunAsync(runId, ct);

        _logger.Information(
            "RunLifecycleManager.CancelRunAsync: run {RunId} cancelled (agent={AgentId})",
            runId, run.AgentId);

        return run;
    }

    /// <inheritdoc />
    public async Task AgentAcceptedRunAsync(string runId, string agentId, string issueIdentifier,
        string issueProviderConfigId, string repoProviderConfigId,
        PipelineRunType runType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runId);
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
                agent.ActiveJobId = runId;
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
        await TrySwapLabelAsync(issueIdentifier, providerForLabel, targetKind, AgentLabels.InProgress, ct);

        _logger.Information(
            "RunLifecycleManager.AgentAcceptedRunAsync: agent {AgentId} accepted run {RunId} for issue {IssueIdentifier}",
            agentId, runId, issueIdentifier);
    }

    /// <inheritdoc />
    public async Task TransitionWorkItemToFailedAsync(string runId, CancellationToken ct)
    {
        await TransitionWorkItemAsync(runId, WorkItemStatus.Failed, ct);
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private async Task TransitionWorkItemAsync(string runId, WorkItemStatus status, CancellationToken ct)
    {
        if (_workItemTransition is null || !Guid.TryParse(runId, out var workItemId))
            return;

        try
        {
            var result = await _workItemTransition.TransitionAsync(workItemId, status, item =>
            {
                if (status is WorkItemStatus.Failed or WorkItemStatus.Succeeded or WorkItemStatus.Cancelled)
                    item.CompletedAt = DateTimeOffset.UtcNow;
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
                        }, ct);
                    }
                    else
                    {
                        // Third fallback: recover from infrastructure-failure-induced Failed state
                        var recovered = await _workItemTransition.TryRecoverFromInfrastructureFailureAsync(
                            workItemId, status, item => { item.CompletedAt = DateTimeOffset.UtcNow; }, ct);
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

    private async Task TrySwapLabelAsync(PipelineRun run, string label, CancellationToken ct)
    {
        try
        {
            var targetKind = run.RunType == PipelineRunType.Review
                ? LabelTargetKind.PullRequest
                : LabelTargetKind.Issue;

            var providerConfigId = targetKind == LabelTargetKind.PullRequest
                ? run.RepoProviderConfigId
                : run.IssueProviderConfigId;

            await _labelSwapper.SwapLabelAsync(providerConfigId, run.IssueIdentifier, label, targetKind, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "RunLifecycleManager: label swap to {Label} failed for run {RunId} (non-fatal)", label, run.RunId);
        }
    }

    private async Task TrySwapLabelAsync(string issueIdentifier, string providerConfigId,
        LabelTargetKind targetKind, string label, CancellationToken ct)
    {
        try
        {
            await _labelSwapper.SwapLabelAsync(providerConfigId, issueIdentifier, label, targetKind, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "RunLifecycleManager: label swap to {Label} failed for issue {IssueIdentifier} (non-fatal)",
                label, issueIdentifier);
        }
    }
}

using CodingAgentWebUI.Infrastructure.Persistence.Entities;
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

    public RunLifecycleManager(
        IOrchestratorRunService runService,
        IPipelineRunHistoryService historyService,
        IAgentRegistryService registry,
        ILabelSwapper labelSwapper,
        JobDispatcherService dispatcher,
        ILogger logger,
        WorkItemTransitionService? workItemTransition = null)
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

        // 3. Persist to history
        _historyService.AddRunToHistory(run);

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

        // 2. Persist to history
        _historyService.AddRunToHistory(run);

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

        // 3. Persist to history
        _historyService.AddRunToHistory(run);

        // 4. Mark issue complete in dedup tracker
        _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

        // 5. Clear agent state
        ClearAgentState(run.AgentId);

        // 6. Swap label
        await TrySwapLabelAsync(run, AgentLabels.Cancelled, ct);

        _logger.Information(
            "RunLifecycleManager.CancelRunAsync: run {RunId} cancelled (agent={AgentId})",
            runId, run.AgentId);

        return run;
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
}

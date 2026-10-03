using System.Diagnostics;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Handles completion of consolidation pipeline runs.
/// Extracted from <see cref="AgentJobLifecycleService.HandleConsolidationRunCompletedAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Consolidation runs route through <see cref="IRunLifecycleManager"/> so that pipeline run
/// history is written and WorkItem status is transitioned consistently. They enter the PipelineRun
/// tracking as ghost entries during orchestrator restart rehydration.
/// </para>
/// <para>
/// Does not perform agent-idle transitions — that is the caller's responsibility.
/// Does not call PostCompletionBookkeepingAsync — consolidation runs have no associated
/// issue labels or feedback comments.
/// </para>
/// </remarks>
internal sealed class ConsolidationJobCompletionStrategy : IJobCompletionStrategy
{
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly IChangeNotifier _changeNotifier;
    private readonly ILogger _logger;

    public ConsolidationJobCompletionStrategy(
        IRunLifecycleManager lifecycleManager,
        IChangeNotifier changeNotifier,
        ILogger logger)
    {
        _lifecycleManager = lifecycleManager;
        _changeNotifier = changeNotifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ExecuteAsync(JobId jobId, PipelineRun run, JobCompletionPayload payload,
                                         Activity? activity, CancellationToken ct)
    {
        _logger.Information(
            "ReportJobCompleted: routing consolidation run {JobId} (IssueIdentifier={IssueIdentifier}) through RunLifecycleManager",
            jobId.Value, run.IssueIdentifier);

        var (workItemStatus, consolidationError, consolidationFailureEnum) =
            CompletionOutcomeResolver.Resolve(payload.FinalStep, payload.FailureReason, payload.FailureCategory,
                "Consolidation run failed");

        // Route through RunLifecycleManager so history is written and WorkItem status is
        // transitioned atomically. RunLifecycleManager.CompleteRunAsync calls _runService.RemoveRun
        // internally — do NOT call _facade.RemoveRun separately.
        // RunLifecycleManager already skips the label swap for consolidation runs
        // (IssueProviderConfigId == ConsolidationConstants.ProviderConfigId guard in each terminal method).
        if (workItemStatus == WorkItemStatus.Succeeded)
        {
            try
            {
                await _lifecycleManager.CompleteRunAsync(jobId.Value, workItemStatus, ct,
                    consolidationError, consolidationFailureEnum);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "ReportJobCompleted: RunLifecycleManager.CompleteRunAsync failed for consolidation run {JobId} (non-fatal)", jobId.Value);
            }
        }
        else if (workItemStatus == WorkItemStatus.Cancelled)
        {
            try
            {
                await _lifecycleManager.CancelRunAsync(jobId.Value, ct, null);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "ReportJobCompleted: RunLifecycleManager.CancelRunAsync failed for consolidation run {JobId} (non-fatal)", jobId.Value);
            }
        }
        else
        {
            try
            {
                await _lifecycleManager.FailRunAsync(jobId.Value, consolidationError ?? "Consolidation run failed", ct,
                    consolidationFailureEnum);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "ReportJobCompleted: RunLifecycleManager.FailRunAsync failed for consolidation run {JobId} (non-fatal)", jobId.Value);
            }
        }

        // TODO: NotifyChange fires here while the agent is still in Busy state — the agent-idle
        // transition happens in AgentJobLifecycleService.HandleJobCompletedAsync after this method
        // returns. In the original HandleConsolidationRunCompletedAsync, NotifyChange was called
        // after agent state was cleared. A UI client reading agent status immediately on receiving
        // the notification will observe a stale Busy state. Consider moving NotifyChange to the
        // caller after the agent-idle transition, or accepting the minor ordering difference.
        _changeNotifier.NotifyChange();

        // Consolidation runs never race with the HTTP Failed path (they use a separate
        // completion endpoint), so the run is always considered alive from this strategy's perspective.
        return true;
    }
}

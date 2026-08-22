using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Unified lifecycle manager for dispatched pipeline runs. Coordinates all state transitions
/// across the in-memory dictionary, database WorkItem rows, agent registry, label state,
/// history persistence, and issue deduplication tracking.
///
/// Every caller that terminates a run (HeartbeatMonitor, AgentHub.ReportJobCompleted,
/// CancelJob, RevertFailedDistribution) MUST use this interface rather than individually
/// calling RemoveRun + TransitionWorkItem + SwapLabel + etc.
/// </summary>
public interface IRunLifecycleManager
{
    /// <summary>
    /// Atomically terminates a run as Failed. Performs in order:
    /// 1. Marks the PipelineRun as Failed (sets FailureReason, CompletedAt, CurrentStep)
    /// 2. Removes from in-memory active runs (OrchestratorRunService)
    /// 3. Transitions the DB WorkItem to Failed
    /// 4. Persists to run history
    /// 5. Marks issue as complete in dedup tracker
    /// 6. Clears agent state (ActiveJobId, OrphanRestoredAt) and transitions to Idle
    /// 7. Swaps label to agent:error
    ///
    /// Returns the removed PipelineRun, or null if the run wasn't found (already processed).
    /// Thread-safe: uses RemoveRun as atomic claim to prevent double-processing.
    /// </summary>
    Task<PipelineRun?> FailRunAsync(RunId runId, string failureReason, CancellationToken ct, FailureReason? failureReasonEnum = null);

    /// <summary>
    /// Atomically terminates a run as Completed/Succeeded. Performs in order:
    /// 1. Removes from in-memory active runs
    /// 2. Transitions the DB WorkItem to the given terminal status
    /// 3. Persists to run history
    /// 4. Marks issue as complete in dedup tracker
    ///
    /// Does NOT clear agent state or swap labels (caller handles those for completion,
    /// since the final label depends on business logic in ReportJobCompleted).
    /// Returns the removed PipelineRun, or null if not found.
    /// </summary>
    Task<PipelineRun?> CompleteRunAsync(RunId runId, WorkItemStatus terminalStatus, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null);

    /// <summary>
    /// Atomically cancels a run. Performs in order:
    /// 1. Marks the PipelineRun as Cancelled (sets FailureReason if provided, CompletedAt, CurrentStep)
    /// 2. Removes from in-memory active runs
    /// 3. Transitions the DB WorkItem to Cancelled
    /// 4. Persists to run history
    /// 5. Marks issue as complete in dedup tracker
    /// 6. Clears agent state and transitions to Idle
    /// 7. Swaps label to agent:cancelled
    ///
    /// Returns the removed PipelineRun, or null if not found.
    /// </summary>
    Task<PipelineRun?> CancelRunAsync(RunId runId, CancellationToken ct, string? failureReason = null);

    /// <summary>
    /// Signals that an agent has accepted a run. Performs in order:
    /// 1. Sets AgentId on the in-memory PipelineRun
    /// 2. Sets ActiveJobId on the agent registry entry and transitions to Busy
    /// 3. Swaps label to agent:in-progress (best-effort)
    ///
    /// Called on every path that hands a run to an agent, so that label swap timing stays
    /// consistent: labels only change when an agent actually starts working on the issue.
    /// </summary>
    Task AgentAcceptedRunAsync(RunId runId, AgentId agentId, IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderConfigId, ProviderConfigId repoProviderConfigId,
        PipelineRunType runType, CancellationToken ct);

    /// <summary>
    /// Transitions a WorkItem to Failed in the database without touching in-memory state.
    /// Used when the in-memory run was already removed by other means (e.g., RevertFailedDistribution)
    /// but the DB row is still in a non-terminal state.
    /// No-op when WorkItemFallbackTransitionService is not registered.
    /// </summary>
    Task TransitionWorkItemToFailedAsync(RunId runId, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null);
}

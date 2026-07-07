using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Result of a consolidation dispatch attempt.
/// </summary>
public enum ConsolidationDispatchResult
{
    /// <summary>Job was dispatched to an idle agent immediately.</summary>
    Dispatched,
    /// <summary>No idle agent available; job was enqueued for later dispatch.</summary>
    Queued,
    /// <summary>Dispatch failed (e.g., broken token vending, no agents registered).</summary>
    Failed
}

/// <summary>
/// Abstracts consolidation job dispatch so that <see cref="Services.ConsolidationService"/>
/// (in the Pipeline project) can dispatch jobs to agents without depending on the
/// Orchestration or WebUI projects directly.
/// Implemented in the WebUI composition root where all dependencies are available.
/// </summary>
public interface IConsolidationDispatcher
{
    /// <summary>
    /// Attempts to dispatch a consolidation run to an idle agent, or enqueues it if none available.
    /// </summary>
    /// <param name="run">The consolidation run to dispatch.</param>
    /// <param name="type">The consolidation run type.</param>
    /// <param name="templateId">The template ID (null for global/harness suggestions).</param>
    /// <param name="feedbackDataJson">Optional feedback data JSON for harness suggestion runs.</param>
    /// <param name="workspacePath">The workspace path for the consolidation run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The dispatch result indicating whether the job was dispatched, queued, or failed.</returns>
    Task<ConsolidationDispatchResult> TryDispatchAsync(
        ConsolidationRun run,
        ConsolidationRunType type,
        string? templateId,
        string? feedbackDataJson,
        string workspacePath,
        CancellationToken ct);

    /// <summary>
    /// Dispatches a previously-queued consolidation job to a specific agent.
    /// Token vending happens at this point (not at enqueue time).
    /// </summary>
    /// <param name="runId">The consolidation run ID.</param>
    /// <param name="type">The consolidation run type.</param>
    /// <param name="templateId">The template ID (null for global).</param>
    /// <param name="workspacePath">The workspace path.</param>
    /// <param name="agentId">The agent ID to dispatch to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if dispatched successfully; <c>false</c> otherwise.</returns>
    Task<bool> TryDispatchToAgentAsync(
        string runId,
        ConsolidationRunType type,
        string? templateId,
        string workspacePath,
        string agentId,
        CancellationToken ct);

    /// <summary>
    /// Notifies the dispatcher that a queued run has been cancelled, so it won't be dispatched.
    /// In DB mode, transitions the WorkItem to Cancelled. In Legacy mode, removes from in-memory queue.
    /// </summary>
    /// <param name="runId">The run ID that was cancelled.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyRunCancelledAsync(string runId, CancellationToken ct);
}

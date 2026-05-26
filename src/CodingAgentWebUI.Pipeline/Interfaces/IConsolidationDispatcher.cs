using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

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
    /// <returns>The dispatch result: Dispatched, Queued, or Failed.</returns>
    Task<ConsolidationDispatchResult> TryDispatchAsync(
        ConsolidationRun run,
        ConsolidationRunType type,
        string? templateId,
        string? feedbackDataJson,
        string workspacePath,
        CancellationToken ct);

    /// <summary>
    /// Dispatches a queued consolidation job to a specific agent (used by drain service).
    /// Token vending and feedback data regeneration happen here.
    /// </summary>
    /// <param name="job">The pending consolidation job to dispatch.</param>
    /// <param name="agentId">The target agent ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if dispatched successfully; <c>false</c> on failure.</returns>
    Task<bool> TryDispatchToAgentAsync(PendingConsolidationJob job, string agentId, CancellationToken ct);
}

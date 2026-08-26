using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/consolidation-work-items endpoint group.
/// Called by the Job Controller's ConsolidationDispatchLoop to fetch pending consolidation
/// WorkItems, claim them (with server-side payload enrichment), and requeue on failure.
/// Requeue on failure reuses the existing /api/work-items/{id}/requeue endpoint.
/// </summary>
public interface IPipelineApiConsolidationWorkItemClient
{
    /// <summary>
    /// Returns pending consolidation WorkItems (TaskType=Consolidation), ordered by CreatedAt ASC.
    /// Mirrors GET /api/work-items/pending but scoped to Consolidation task type.
    /// </summary>
    Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults = 50, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims a consolidation WorkItem (Pending → Dispatched) and returns the
    /// enriched payload (provider configs resolved, tokens vended server-side).
    /// Returns null on 409 (already claimed). Throws <see cref="WorkItemNotFoundException"/> on 404.
    /// </summary>
    Task<ConsolidationWorkItemClaimResponse?> ClaimAsync(
        Guid workItemId, ClaimWorkItemRequest request, CancellationToken ct = default);

    /// <summary>
    /// Transitions a ConsolidationRun status (e.g. Queued→Running, any→Failed).
    /// Delegates to IConsolidationService server-side for correct cache invalidation.
    /// </summary>
    Task TransitionRunAsync(
        string runId, ConsolidationRunStatus status, string? summary = null, CancellationToken ct = default);

    /// <summary>
    /// Requeues a consolidation WorkItem (Dispatched → Pending) after K8s Job creation failure.
    /// Reuses the existing /api/work-items/{id}/requeue endpoint.
    /// </summary>
    Task RequeueAsync(Guid workItemId, CancellationToken ct = default);
}

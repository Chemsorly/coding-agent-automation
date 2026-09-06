namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Request body for POST /api/work-items/{id}/claim.
/// </summary>
public sealed record ClaimWorkItemRequest
{
    /// <summary>Agent ID (K8s Job name) that is claiming the item.</summary>
    public string? AssignedAgentId { get; init; }

    /// <summary>Timestamp at which the item was dispatched.</summary>
    public required DateTimeOffset DispatchedAt { get; init; }

    /// <summary>
    /// The K8s Job name for this work item. Same as <see cref="AssignedAgentId"/> for
    /// job-controller-dispatched items. Written to <c>WorkItems.K8sJobName</c> at claim
    /// time so the reconciliation service can locate the live job without recomputing the
    /// name (which differs between the API path and job-controller path).
    /// </summary>
    public string? K8sJobName { get; init; }

    /// <summary>
    /// The PVC name selected by the caller for kiro agent dispatch.
    /// When non-null, <c>POST /api/consolidation-work-items/{id}/claim</c> performs a
    /// DB-level conflict check: if any <c>Dispatched</c> or <c>Running</c> WorkItem already
    /// holds this PVC, the claim is rejected with 409 (PVC already claimed).
    ///
    /// This closes the cross-process TOCTOU race between <see cref="ConsolidationDispatchLoop"/>
    /// (which selects PVCs by querying live K8s Jobs) and <see cref="DispatchLifecycleService"/>
    /// (which selects PVCs by querying DB-claimed <c>Dispatched</c>/<c>Running</c> WorkItems).
    /// Since the two processes use different mechanisms, the DB claim write becomes the
    /// authoritative synchronization point — whichever write arrives first holds the PVC.
    /// </summary>
    public string? ClaimedPvcName { get; init; }
}

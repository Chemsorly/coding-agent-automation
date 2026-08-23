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
}

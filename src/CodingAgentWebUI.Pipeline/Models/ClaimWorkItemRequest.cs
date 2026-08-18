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
}

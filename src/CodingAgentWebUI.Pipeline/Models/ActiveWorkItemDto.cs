namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// DTO representing an active (non-terminal) work item.
/// </summary>
public sealed record ActiveWorkItemDto
{
    public required Guid Id { get; init; }
    public required WorkItemStatus Status { get; init; }
    public required DateTimeOffset? DispatchedAt { get; init; }
    public required string AgentSelector { get; init; }
    public required string IssueIdentifier { get; init; }
    /// <summary>
    /// The K8s Job name assigned at dispatch time. Null for items not yet dispatched
    /// or dispatched before this field was added.
    /// Used by <c>ReconciliationLoop</c> to match live Jobs without recomputing the name.
    /// </summary>
    public string? K8sJobName { get; init; }

    /// <summary>
    /// Per-item software-level timeout in seconds, sourced from <c>WorkItemEntity.TimeoutSeconds</c>.
    /// Used by <c>ReconciliationLoop.EnforceTimeoutsAsync</c> to enforce the correct per-project
    /// timeout rather than a global threshold.
    /// Zero indicates a legacy row created before the field was populated; callers should fall
    /// back to <c>PipelineConstants.DefaultAgentTimeout</c> in that case.
    /// </summary>
    public int TimeoutSeconds { get; init; }
}

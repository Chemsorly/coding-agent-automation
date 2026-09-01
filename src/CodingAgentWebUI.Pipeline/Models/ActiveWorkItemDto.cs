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
    /// Per-item software-level timeout in seconds, set at enqueue time from
    /// <c>PipelineConfiguration.AgentTimeout</c> (with per-project override applied).
    /// Used by <c>ReconciliationLoop.EnforceTimeoutsAsync</c> to enforce the correct timeout
    /// per work item rather than a single global threshold.
    /// Zero means the value was not recorded (pre-dates this field); callers should fall back
    /// to <c>PipelineConstants.DefaultAgentTimeout</c>.
    /// </summary>
    public int TimeoutSeconds { get; init; }
}

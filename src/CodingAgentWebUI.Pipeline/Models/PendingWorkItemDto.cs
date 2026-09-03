namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// DTO returned by GET /api/work-items/pending.
/// Contains the minimum data the Job Controller needs to decide whether to claim an item,
/// plus optional display fields for the Agent Monitoring Job Queue UI.
/// </summary>
public sealed record PendingWorkItemDto
{
    public required Guid Id { get; init; }
    public required string IssueIdentifier { get; init; }
    public required string IssueProviderConfigId { get; init; }
    public required WorkItemTaskType TaskType { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string AgentSelector { get; init; }
    public required int RetryCount { get; init; }

    /// <summary>
    /// Per-item software-level timeout in seconds. Used by the Job Controller to compute
    /// <c>activeDeadlineSeconds</c> on the K8s Job: <c>TimeoutSeconds + 60</c> (buffer added
    /// by <see cref="CodingAgentWebUI.Kubernetes.JobSpecBuilder"/>).
    /// Populated from <c>WorkItemEntity.TimeoutSeconds</c> by the pending-items API endpoint.
    /// Set at enqueue time from <c>PipelineConfiguration.AgentTimeout</c> (with per-project override applied).
    /// </summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>
    /// Intra-queue dispatch priority. Higher values are dispatched first (ORDER BY PriorityWeight DESC, CreatedAt ASC).
    /// Manual dispatches receive 100; closed-loop dispatches receive 0 (the default). Range: 0–1000.
    /// </summary>
    public int PriorityWeight { get; init; }

    // ── Display fields for the Agent Monitoring Job Queue UI ──────────────
    // Populated by the API from the Payload JSONB column and the ProjectId column.
    // The Job Controller claim path never reads these fields; they are null-safe additions.

    /// <summary>Issue title from <c>JobDistributionRequest.IssueDetail.Title</c>. Null when payload is absent or has no IssueDetail.</summary>
    public string? IssueTitle { get; init; }

    /// <summary>Who initiated the work item (e.g., "loop", "manual"). From <c>JobDistributionRequest.InitiatedBy</c>. Null when payload is absent.</summary>
    public string? InitiatedBy { get; init; }

    /// <summary>Project display name from <c>JobDistributionRequest.ProjectName</c>. Null when payload is absent or item has no project.</summary>
    public string? ProjectName { get; init; }

    /// <summary>Project ID from the <c>WorkItemEntity.ProjectId</c> column. Null when item has no project.</summary>
    public Guid? ProjectId { get; init; }

    /// <summary>
    /// W3C traceparent captured at WorkItem creation time (API span).
    /// Used by the Job Controller to inject TRACEPARENT into the worker K8s Job env so
    /// worker spans attach to the originating API trace rather than starting a new root trace.
    /// </summary>
    public string? TraceParent { get; init; }
}

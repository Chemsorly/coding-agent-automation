using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Represents a unit of work in the distribution queue, persisted to PostgreSQL.
/// Maps to the "WorkItems" table.
/// </summary>
public class WorkItemEntity
{
    public Guid Id { get; set; }
    public WorkItemTaskType TaskType { get; set; }
    public string IssueIdentifier { get; set; } = "";
    public string IssueProviderConfigId { get; set; } = "";
    public WorkItemStatus Status { get; set; }

    /// <summary>Full JobDistributionRequest serialized as JSONB string — contains NO secrets.
    /// Stored as string to avoid JsonDocument memory leaks (ArrayPool buffer retention).</summary>
    public string? Payload { get; set; }

    /// <summary>Sorted comma-joined agent selector labels for image resolution.</summary>
    public string AgentSelector { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? K8sJobName { get; set; }
    public string? AssignedAgentId { get; set; }
    public string? ErrorMessage { get; set; }
    public FailureReason? FailureReason { get; set; }
    public int RetryCount { get; set; }
    public int TimeoutSeconds { get; set; }

    /// <summary>Agent-reported result payload (JSONB string, nullable).</summary>
    public string? Result { get; set; }

    public string? ProjectId { get; set; }

    /// <summary>
    /// The original enqueue timestamp, carried forward across re-dispatches of the same issue.
    /// When a WorkItem is created for an issue that has prior WorkItems, this preserves the
    /// earliest CreatedAt from any predecessor. Falls back to CreatedAt when no prior exists.
    /// Used by the UI to show true "time in queue" rather than the latest WorkItem creation time.
    /// </summary>
    public DateTimeOffset? OriginalEnqueuedAt { get; set; }

    /// <summary>
    /// Last time the agent reported progress (step transition or heartbeat with active step).
    /// Updated with throttling (only when current DB value is >5 min stale) to reduce write load.
    /// Used by ReconciliationService for progress-aware timeout enforcement.
    /// </summary>
    public DateTimeOffset? LastProgressAt { get; set; }

    /// <summary>PVC name claimed from the pool for kiro agents, null for other agent types.</summary>
    public string? ClaimedPvcName { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}

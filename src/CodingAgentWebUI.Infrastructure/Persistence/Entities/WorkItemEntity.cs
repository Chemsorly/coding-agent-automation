using System.Text.Json;
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

    /// <summary>Full JobDistributionRequest serialized as JSONB — contains NO secrets.</summary>
    public JsonDocument? Payload { get; set; }

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

    /// <summary>Agent-reported result payload (JSONB, nullable).</summary>
    public JsonDocument? Result { get; set; }

    public string? ProjectId { get; set; }

    /// <summary>PVC name claimed from the pool for kiro agents, null for other agent types.</summary>
    public string? ClaimedPvcName { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}

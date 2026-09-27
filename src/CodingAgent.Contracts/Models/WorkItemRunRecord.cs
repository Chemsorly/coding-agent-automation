namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The server-side record of a work item as orphan recovery needs it: who owns the work item,
/// and the run identity to rebuild its <c>PipelineRun</c> from the database instead of from
/// what a re-registering agent reports.
/// </summary>
public sealed record WorkItemRunRecord
{
    public required WorkItemTaskType TaskType { get; init; }

    /// <summary>Name of the K8s Job the work item was dispatched as — the agent ID of its pod.</summary>
    public string? K8sJobName { get; init; }

    public string? AssignedAgentId { get; init; }
    public required string IssueIdentifier { get; init; }
    public required string IssueProviderConfigId { get; init; }
    public string? RepoProviderConfigId { get; init; }
    public string? BrainProviderConfigId { get; init; }
    public string? PipelineProviderConfigId { get; init; }
    public Guid? ProjectId { get; init; }

    /// <summary>
    /// True when <paramref name="agentId"/> is the work item's K8s Job or its assigned agent — the
    /// ownership rule the work-item HTTP endpoints enforce.
    /// </summary>
    public bool IsOwnedBy(string agentId) =>
        string.Equals(K8sJobName, agentId, StringComparison.Ordinal)
        || string.Equals(AssignedAgentId, agentId, StringComparison.Ordinal);
}

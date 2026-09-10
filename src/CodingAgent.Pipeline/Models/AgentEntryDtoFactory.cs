namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Projects an <see cref="AgentEntry"/> into an <see cref="AgentEntryDto"/>, enriching with data
/// from the active <see cref="PipelineRun"/> when available. Lives in Pipeline (not on the DTO,
/// which moved to Contracts in Spec 048 Phase 1) because the enrichment reads the live PipelineRun
/// entity, which stays in Pipeline and must never be referenced from Contracts.
/// </summary>
public static class AgentEntryDtoFactory
{
    /// <param name="entry">The agent entry to project. Must not be null.</param>
    /// <param name="run">
    /// The active pipeline run for this agent, or null when the agent is idle or the run is
    /// not found in the active run service.
    /// </param>
    public static AgentEntryDto From(AgentEntry entry, PipelineRun? run)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new AgentEntryDto
        {
            // AgentEntry fields
            AgentId = entry.AgentId,
            ConnectionId = entry.ConnectionId,
            Hostname = entry.Hostname,
            Labels = entry.Labels,
            Status = entry.Status,
            ActiveJobId = entry.ActiveJobId,
            ActiveChatSessionId = entry.ActiveChatSessionId,
            RegisteredAt = entry.RegisteredAt,
            LastHeartbeatAt = entry.LastHeartbeatAt,
            LastJobCompletedAt = entry.LastJobCompletedAt,
            DisconnectedAt = entry.DisconnectedAt,
            Disabled = entry.Disabled,
            OrphanRestoredAt = entry.OrphanRestoredAt,
            BusySince = entry.BusySince,
            // Enrichment from active run (null-safe)
            ActiveIssueIdentifier = run?.IssueIdentifier.Value,
            ActiveIssueTitle = run?.IssueTitle,
            ActiveIssueUrl = run?.IssueUrl,
            ActiveRunId = run?.RunId,
            ActivePullRequestUrl = run?.PullRequestUrl,
        };
    }
}

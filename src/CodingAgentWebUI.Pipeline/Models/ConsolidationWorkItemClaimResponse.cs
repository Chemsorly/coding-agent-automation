namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Response returned by POST /api/consolidation-work-items/{id}/claim.
/// Contains the enriched payload (provider configs resolved, tokens vended) and optional
/// project secrets — everything the Job Controller needs to create the K8s Job.
/// The enrichment happens server-side at claim time so tokens are as fresh as possible.
/// </summary>
public sealed record ConsolidationWorkItemClaimResponse
{
    /// <summary>The work item ID (same as the path parameter — included for convenience).</summary>
    public required Guid WorkItemId { get; init; }

    /// <summary>
    /// The run ID — matches the ConsolidationRun's RunId (IssueIdentifier on the WorkItem).
    /// Used by the JC to call POST /api/consolidation-runs/{runId}/transition.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Serialized <see cref="JobDistributionRequest"/> with ProviderConfigs populated
    /// (short-lived tokens replacing private keys) and PipelineConfiguration injected.
    /// </summary>
    public required string EnrichedPayloadJson { get; init; }

    /// <summary>
    /// Optional per-project secrets to inject as K8s Secret environment variables.
    /// Null when the project has no secrets configured.
    /// </summary>
    public Dictionary<string, string>? ProjectSecrets { get; init; }

    /// <summary>The orchestrator URL the agent should connect to via SignalR.</summary>
    public required string OrchestratorUrl { get; init; }
}

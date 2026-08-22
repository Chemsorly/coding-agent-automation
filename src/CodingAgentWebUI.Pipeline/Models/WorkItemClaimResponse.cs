namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Response returned by the Job Controller when an agent successfully claims a work item.
/// Contains all data the agent needs to begin executing the pipeline run.
/// Returned as the response body of POST /api/work-items/{id}/claim.
/// </summary>
public sealed record WorkItemClaimResponse
{
    /// <summary>The work item ID (same as the path parameter — included for convenience).</summary>
    public required Guid WorkItemId { get; init; }

    /// <summary>The run ID to use for all pipeline run history entries.</summary>
    public required string RunId { get; init; }

    /// <summary>Serialized <see cref="JobAssignmentMessage"/> containing providers, config, and issue context.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>
    /// The short-lived orchestrator URL this agent should connect to via SignalR.
    /// Injected by the Job Controller from the <c>WorkDistribution:OrchestratorUrl</c> config value.
    /// </summary>
    public required string OrchestratorUrl { get; init; }
}

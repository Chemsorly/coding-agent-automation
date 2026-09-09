namespace CodingAgent.Pipeline.Models;

/// <summary>
/// DTO for POST /api/work-items/{id}/status request body.
/// Used by the agent to report status transitions to the orchestrator.
/// Moved from <c>CodingAgent.Agent.WorkItemHttpClient</c> to <c>CodingAgent.Pipeline</c>
/// so that <c>CodingAgent.Api.Client</c> can reference it without depending on the Agent project.
/// </summary>
public sealed class WorkItemStatusUpdate
{
    public required string Status { get; init; }
    public string? AgentId { get; init; }
    public string? Result { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FailureReason { get; init; }
}

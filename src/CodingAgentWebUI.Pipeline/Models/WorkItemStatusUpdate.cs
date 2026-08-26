namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// DTO for POST /api/work-items/{id}/status request body.
/// Used by the agent to report status transitions to the orchestrator.
/// Moved from <c>CodingAgentWebUI.Agent.WorkItemHttpClient</c> to <c>CodingAgentWebUI.Pipeline</c>
/// so that <c>CodingAgentWebUI.Api.Client</c> can reference it without depending on the Agent project.
/// </summary>
public sealed class WorkItemStatusUpdate
{
    public required string Status { get; init; }
    public string? AgentId { get; init; }
    public string? Result { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FailureReason { get; init; }
}

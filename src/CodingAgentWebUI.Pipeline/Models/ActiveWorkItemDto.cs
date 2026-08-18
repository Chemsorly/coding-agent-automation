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
}

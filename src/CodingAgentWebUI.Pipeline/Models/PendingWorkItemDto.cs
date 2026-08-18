namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// DTO returned by GET /api/work-items/pending.
/// Contains the minimum data the Job Controller needs to decide whether to claim an item.
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
}

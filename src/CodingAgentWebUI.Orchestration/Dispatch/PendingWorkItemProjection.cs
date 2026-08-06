using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Lightweight projection of pending work items (no Payload loaded).
/// Shared between <see cref="DispatchService"/>, <see cref="ConsolidationDispatchHandler"/>,
/// and <see cref="DispatchLifecycleService"/>.
/// </summary>
internal sealed record PendingWorkItemProjection
{
    public required Guid Id { get; init; }
    public required string AgentSelector { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required int TimeoutSeconds { get; init; }
    public WorkItemTaskType TaskType { get; init; }
    public string? ProjectId { get; init; }
    public string? IssueIdentifier { get; init; }
    public string? IssueProviderConfigId { get; init; }
}

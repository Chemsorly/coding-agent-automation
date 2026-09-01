using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Dispatch;

/// <summary>
/// Lightweight projection of pending work items (no Payload loaded).
/// Used by <see cref="DispatchStateBuilder"/> and its consumers.
/// Previously defined in <c>CodingAgentWebUI.Orchestration.Dispatch</c> (Spec 043 dead code);
/// moved here as the canonical location (arch-audit 2026-08-22).
/// </summary>
internal sealed record PendingWorkItemProjection
{
    public required Guid Id { get; init; }
    public required string AgentSelector { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required int TimeoutSeconds { get; init; }
    public WorkItemTaskType TaskType { get; init; }
    public Guid? ProjectId { get; init; }
    public string? IssueIdentifier { get; init; }
    public string? IssueProviderConfigId { get; init; }
    public int PriorityWeight { get; init; }
}

/// <summary>
/// Result of <see cref="DispatchStateBuilder.BuildStateAsync"/>: the dispatch state
/// needed for per-item gating decisions.
/// </summary>
internal sealed class DispatchState
{
    public required PipelineDbContext Db { get; init; }
    public required List<PendingWorkItemProjection> PendingItems { get; init; }
    public required Dictionary<string, int> ConcurrencyBySelector { get; init; }
    public required List<string> AvailablePvcs { get; init; }
}

/// <summary>
/// A dispatch-ready candidate that has passed all gating checks.
/// </summary>
internal sealed record DispatchCandidate(
    PendingWorkItemProjection Item,
    JobTemplate Template,
    string EffectiveSelector,
    bool IsKiroAgent);

/// <summary>
/// Parameter object for <see cref="DispatchLifecycleService.ExecuteDispatchLifecycleAsync"/>.
/// Groups the non-delegate parameters to satisfy S107.
/// Previously defined in <c>CodingAgentWebUI.Orchestration.Dispatch</c>; moved here (arch-audit 2026-08-22).
/// </summary>
internal sealed record DispatchLifecycleContext(
    PipelineDbContext Db,
    PendingWorkItemProjection Item,
    JobTemplate Template,
    bool IsKiroAgent,
    List<string> AvailablePvcs,
    Dictionary<string, int> ConcurrencyBySelector,
    string LogPrefix);

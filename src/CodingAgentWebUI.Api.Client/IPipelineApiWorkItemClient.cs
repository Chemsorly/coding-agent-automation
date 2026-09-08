using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/work-items endpoint group.
/// Also implements <see cref="IWorkItemSweepClient"/> so the Scheduler can pass it to
/// <see cref="CodingAgentWebUI.Pipeline.Models.PipelineLoopServiceDependencies.WorkItemClient"/>.
/// </summary>
public interface IPipelineApiWorkItemClient : IWorkItemSweepClient
{
    new Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults = 50, CancellationToken ct = default);

    /// <summary>Project-scoped pending query for the Work / Overview screens. projectId is required so this
    /// overload never collides with the sweep's <see cref="IWorkItemSweepClient.GetPendingAsync"/>.</summary>
    Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults, string? projectId, CancellationToken ct = default);
    Task<WorkItemClaimResponse?> ClaimAsync(Guid workItemId, ClaimWorkItemRequest request, CancellationToken ct = default);
    Task<JobAssignmentMessage?> GetAssignmentAsync(Guid workItemId, CancellationToken ct = default);
    new Task PostStatusAsync(Guid workItemId, WorkItemStatusUpdate request, CancellationToken ct = default);
    Task RequeueAsync(Guid workItemId, CancellationToken ct = default);
    Task<int> GetRetryCountAsync(Guid workItemId, CancellationToken ct = default);
    Task<WorkItemStalenessResult?> GetStalenessAsync(string issueIdentifier, string issueProviderConfigId, DateTimeOffset since, CancellationToken ct = default);
    Task<Guid> CreateAsync(JobDistributionRequest request, CancellationToken ct = default);
    Task PostLabelSwapAsync(Guid workItemId, string label, CancellationToken ct = default);
    Task<IReadOnlyList<ActiveWorkItemDto>> GetActiveAsync(int olderThanSeconds, string? projectId = null, CancellationToken ct = default);
    Task PostLastProgressAsync(Guid workItemId, DateTimeOffset timestamp, CancellationToken ct = default);

    /// <summary>
    /// Returns the K8s Job name set on a WorkItem, or null if not found / not set.
    /// Used by KubernetesJobCleanup to cancel the running Job when an issue is cancelled.
    /// </summary>
    Task<string?> GetK8sJobNameAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>
    /// Returns the current status of a WorkItem, or null if not found.
    /// Used by KubernetesWorkDistributor.GetJobStatusAsync.
    /// </summary>
    Task<WorkItemStatus?> GetStatusAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>
    /// Returns true when the issue has a non-terminal WorkItem or was recently terminated.
    /// Used by KubernetesWorkDistributor.IsIssueDistributedAsync for dispatch deduplication.
    /// </summary>
    Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId, CancellationToken ct = default);

    /// <summary>
    /// Sets the dispatch priority weight for a Pending WorkItem.
    /// Calls <c>POST /api/work-items/{id}/priority</c>.
    /// Throws <see cref="System.Net.Http.HttpRequestException"/> on non-2xx (400 invalid range, 409 not-Pending or concurrency conflict).
    /// </summary>
    Task SetPriorityAsync(Guid workItemId, int priorityWeight, CancellationToken ct = default);

    /// <summary>
    /// Returns all (IssueIdentifier, IssueProviderConfigId) pairs that have active or recently
    /// terminated WorkItems. Used by KubernetesWorkDistributor.GetActiveIssueIdentifiersAsync.
    /// </summary>
    Task<IReadOnlyList<(string IssueIdentifier, string IssueProviderConfigId)>> GetActiveIdentifiersAsync(CancellationToken ct = default);
}

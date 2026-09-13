using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/work-items endpoint group.
/// Also implements <see cref="IWorkItemSweepClient"/> so the Scheduler can pass it to
/// <see cref="CodingAgent.Pipeline.Models.PipelineLoopServiceDependencies.WorkItemClient"/>.
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

    /// <summary>
    /// Calls <c>POST /api/work-items/dispatch</c> to synchronously dispatch a work item:
    /// PVC selection, K8s Job creation, and <c>Dispatched</c> state write happen atomically
    /// in the API. Returns the new WorkItem ID on success.
    /// Throws <see cref="System.Net.Http.HttpRequestException"/> with:
    ///   <list type="bullet">
    ///     <item>409 Conflict — concurrency limit reached or issue ineligible.</item>
    ///     <item>503 Service Unavailable — no PVC available or K8s failure.</item>
    ///   </list>
    /// </summary>
    Task<Guid> DispatchAsync(JobDistributionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Calls <c>POST /api/work-items/{id}/dispatch</c> to dispatch an existing Pending WorkItem by CAS.
    /// Claims the item (Pending→Dispatched) and creates the K8s Job atomically.
    /// Returns a <see cref="DispatchPendingResult"/> indicating the outcome:
    /// <list type="bullet">
    ///   <item><see cref="DispatchPendingResult.Dispatched"/> — item dispatched successfully (200 OK).</item>
    ///   <item><see cref="DispatchPendingResult.PermanentRejection"/> — item not Pending, concurrency limit reached,
    ///     or no template for selector (409 Conflict). Do not retry this item in the current cycle.</item>
    ///   <item><see cref="DispatchPendingResult.Transient"/> — PVC unavailable, advisory lock timeout, or K8s failure
    ///     (503 Service Unavailable). Retry in the next poll cycle.</item>
    /// </list>
    /// Throws <see cref="System.Net.Http.HttpRequestException"/> for all other unexpected status codes.
    /// </summary>
    Task<DispatchPendingResult> DispatchPendingAsync(Guid workItemId, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a <c>POST /api/work-items/{id}/dispatch</c> call.
/// Used by <c>WorkItemDispatchPoller</c> in the Scheduler to distinguish permanent rejections
/// (stop dispatching this selector for the current cycle) from transient failures (retry next cycle).
/// </summary>
public enum DispatchPendingResult
{
    /// <summary>Item was successfully dispatched — K8s Job created, state transitioned to Dispatched.</summary>
    Dispatched,

    /// <summary>
    /// Permanent rejection (409 Conflict): item is not in Pending state, the concurrency limit for this
    /// selector is reached, or no job template matches the selector. Do not retry in the current cycle;
    /// the Scheduler poller treats this as a stop signal for the item's AgentSelector.
    /// </summary>
    PermanentRejection,

    /// <summary>
    /// Transient failure (503 Service Unavailable): no PVC available, advisory lock timeout, or K8s failure.
    /// Retry in the next poll cycle.
    /// </summary>
    Transient,
}

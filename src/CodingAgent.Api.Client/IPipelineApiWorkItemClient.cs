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
    Task<JobAssignmentMessage?> GetAssignmentAsync(Guid workItemId, CancellationToken ct = default);
    new Task<bool> PostStatusAsync(Guid workItemId, WorkItemStatusUpdate request, CancellationToken ct = default);
    Task<WorkItemStalenessResult?> GetStalenessAsync(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId, DateTimeOffset since, CancellationToken ct = default);
    Task<Guid> CreateAsync(JobDistributionRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ActiveWorkItemDto>> GetActiveAsync(int olderThanSeconds, string? projectId = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the current status of a WorkItem, or null if not found.
    /// Used by KubernetesWorkDistributor.GetJobStatusAsync.
    /// </summary>
    Task<WorkItemStatus?> GetStatusAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>
    /// Returns true when the issue has a non-terminal WorkItem or was recently terminated.
    /// Used by KubernetesWorkDistributor.IsIssueDistributedAsync for dispatch deduplication.
    /// </summary>
    Task<bool> IsIssueDistributedAsync(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId, CancellationToken ct = default);

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
    /// Returns the count of decomposition WorkItems that are currently active
    /// (Pending, Dispatched, or Running). Used by <c>KubernetesWorkDistributor</c> to enforce
    /// <c>MaxConcurrentDecompositions</c> across poll cycles.
    /// Calls <c>GET /api/work-items/active-decomposition-count</c>.
    /// </summary>
    Task<int> GetActiveDecompositionCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the status and final label of the most recently created work item for the given
    /// issue and provider, or null when no work item exists.
    /// Used by the orphaned-label recovery sweep to distinguish a silently-succeeded run (hub
    /// completion report was lost) from a genuinely dead one.
    /// Returns null on 404. Other non-success statuses throw through <see cref="System.Net.Http.HttpRequestException"/>.
    /// </summary>
    Task<WorkItemLatestOutcome?> GetLatestOutcomeAsync(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId, CancellationToken ct = default);

    /// <summary>
    /// Calls <c>POST /api/work-items/{id}/dispatch</c> to dispatch an existing Pending WorkItem by CAS.
    /// Claims the item (Pending→Dispatched) and creates the K8s Job atomically.
    /// Returns a <see cref="DispatchPendingResult"/> indicating the outcome:
    /// <list type="bullet">
    ///   <item><see cref="DispatchPendingResult.Dispatched"/> — item dispatched successfully
    ///     (200 OK with <c>{ "dispatched": true }</c>).</item>
    ///   <item><see cref="DispatchPendingResult.PermanentRejection"/> — the item is held or not Pending
    ///     (200 OK with <c>{ "dispatched": false, "reason": "..." }</c>); the outcome names the reason, template and
    ///     credential (spec 056 DD9).</item>
    ///   <item><see cref="DispatchPendingResult.Transient"/> — advisory lock timeout, running agents not countable,
    ///     or K8s failure (503 Service Unavailable). Retry in the next poll cycle.</item>
    /// </list>
    /// Throws <see cref="System.Net.Http.HttpRequestException"/> for all other unexpected status codes.
    /// </summary>
    Task<DispatchPendingOutcome> DispatchPendingAsync(Guid workItemId, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a <c>POST /api/work-items/{id}/dispatch</c> call.
/// Used by <c>WorkItemDispatchLoop</c> in the Scheduler to distinguish permanent rejections
/// (stop dispatching this selector for the current cycle) from transient failures (retry next cycle).
/// </summary>
public enum DispatchPendingResult
{
    /// <summary>Item was successfully dispatched — K8s Job created, state transitioned to Dispatched.</summary>
    Dispatched,

    /// <summary>
    /// The item was not started: it is held (spec 056: a route that cannot run, a paused credential, a reached limit,
    /// no free PVC) or no longer Pending. Returned when the API responds 200 with <c>{ "dispatched": false }</c>. The
    /// Scheduler stops what <see cref="DispatchPendingOutcome"/> names for the cycle.
    /// </summary>
    PermanentRejection,

    /// <summary>
    /// Transient failure (503 Service Unavailable): advisory lock timeout, running agents not countable, or K8s failure.
    /// Retry in the next poll cycle.
    /// </summary>
    Transient,

    /// <summary>
    /// Spec 057: the limit for all agents together is reached (<c>global_concurrency_limit</c>). No other item can
    /// start either, so the Scheduler ends the dispatch cycle.
    /// </summary>
    GlobalLimitReached,
}

/// <summary>
/// Spec 056 DD9: the outcome of <c>POST /api/work-items/{id}/dispatch</c> with what the API resolved: the hold reason, and
/// the job template key and credential the item resolved to. The Scheduler stops the template (<c>concurrency_limit</c>,
/// <c>pvc_unavailable</c>) or the credential (<c>credential_paused</c>) for the cycle. An API of an earlier release sends
/// no template or credential.
/// </summary>
public readonly record struct DispatchPendingOutcome(
    DispatchPendingResult Result,
    string? Reason = null,
    string? TemplateKey = null,
    string? Credential = null)
{
    /// <summary>An outcome without reason, template or credential.</summary>
    public static implicit operator DispatchPendingOutcome(DispatchPendingResult result) => new(result);
}

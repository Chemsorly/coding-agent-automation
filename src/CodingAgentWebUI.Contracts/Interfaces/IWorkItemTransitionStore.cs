namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Abstracts the direct WorkItem database operations that the SignalR agent hub facade
/// performs (retry-count reads, re-queue, provider-config / issue-metadata reads, and the
/// throttled <c>LastProgressAt</c> write). Defined in Contracts so the Hub library carries no
/// compile-time dependency on <c>Infrastructure.Persistence</c> (Spec 048 Phase 2 — DB isolation).
/// The EF-backed implementation lives in <c>Infrastructure.Persistence</c> and is wired only in
/// the API host (the sole database owner); it is absent (null) in in-memory / test hosts, in which
/// case the facade degrades to no-ops exactly as it did with the previous nullable dependencies.
/// </summary>
public interface IWorkItemTransitionStore
{
    /// <summary>
    /// Gets the current retry count for a work item (how many times it has been rejected and
    /// re-queued). Returns 0 if the work item does not exist.
    /// </summary>
    Task<int> GetRetryCountAsync(Guid workItemId, CancellationToken ct);

    /// <summary>
    /// Re-queues a rejected work item: transitions it back to Pending, increments RetryCount,
    /// and clears DispatchedAt / AssignedAgentId so the drain service picks it up again.
    /// </summary>
    Task RequeueAsync(Guid workItemId, CancellationToken ct);

    /// <summary>
    /// Resolves the repo/brain provider config IDs from a WorkItem's JSON payload (K8s-mode
    /// fallback used by token vending when no in-memory PipelineRun exists). Returns null when
    /// the work item does not exist or has no payload.
    /// </summary>
    Task<(string? RepoProviderConfigId, string? BrainProviderConfigId)?> GetWorkItemProviderConfigIdsAsync(
        Guid workItemId, CancellationToken ct);

    /// <summary>
    /// Reads IssueIdentifier and IssueProviderConfigId from a WorkItem (best-effort label recovery
    /// when no in-memory PipelineRun is available). Returns null when the work item does not exist
    /// or has no issue identifier.
    /// </summary>
    Task<(string IssueIdentifier, string IssueProviderConfigId)?> GetWorkItemIssueMetadataAsync(
        Guid workItemId, CancellationToken ct);

    /// <summary>
    /// Updates <c>WorkItemEntity.LastProgressAt</c> with throttling: only writes when the current
    /// DB value is null or older than the throttle interval (5 minutes). No-op when the work item
    /// does not exist. Callers wrap this to translate failures into telemetry.
    /// </summary>
    Task TouchLastProgressAsync(Guid workItemId, DateTimeOffset timestamp, CancellationToken ct);
}

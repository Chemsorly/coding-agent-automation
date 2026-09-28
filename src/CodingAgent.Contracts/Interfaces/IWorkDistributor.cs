using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Abstraction for distributing pipeline work.
/// Primary dispatch abstraction consumed by <see cref="Services.PipelineLoopService"/>.
/// <para>
/// The only implementation is <c>KubernetesWorkDistributor</c>, which inserts a WorkItem row that
/// the Job Controller's dispatch loop turns into a Job. The interface predates Spec 041, when it
/// also covered a no-DB in-memory distributor and a DB+SignalR one; both were deleted with the
/// deployment modes they served.
/// </para>
/// </summary>
public interface IWorkDistributor
{
    /// <summary>
    /// Distributes a job for processing by inserting a WorkItem row.
    /// </summary>
    /// <param name="request">The full job distribution request containing issue context and configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="DistributionResult"/> indicating success/failure and the created work item ID.</returns>
    Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct);

    /// <summary>
    /// Cancels a previously distributed job by its work item ID.
    /// </summary>
    /// <param name="jobId">The work item ID to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the job was successfully cancelled; <c>false</c> if not found or already terminal.</returns>
    Task<bool> CancelJobAsync(JobId jobId, CancellationToken ct);

    /// <summary>
    /// Gets the current status of a distributed job.
    /// Returns <see cref="JobDistributionStatus.Unknown"/> for nonexistent work item IDs without throwing.
    /// </summary>
    /// <param name="jobId">The work item ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current <see cref="JobDistributionStatus"/> of the work item.</returns>
    Task<JobDistributionStatus> GetJobStatusAsync(JobId jobId, CancellationToken ct);

    /// <summary>
    /// Checks whether a specific issue is currently distributed (Pending, Dispatched, or Running).
    /// Used for single-item dedup checks (e.g., manual dispatch from UI).
    /// </summary>
    /// <param name="issueIdentifier">The issue identifier to check.</param>
    /// <param name="issueProviderConfigId">The issue provider config ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the issue has an active (non-terminal) work item.</returns>
    Task<bool> IsIssueDistributedAsync(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId, CancellationToken ct);

    /// <summary>
    /// Returns all currently active (non-terminal) issue identifiers as a set.
    /// Used by PipelineLoopService to batch-load dedup state at cycle start,
    /// avoiding N+1 DB queries in the per-issue dispatch loop.
    /// A single SQL query loading all non-terminal (IssueIdentifier, IssueProviderConfigId) pairs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A set of (IssueIdentifier, IssueProviderConfigId) tuples for all active work items.</returns>
    Task<HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)>> GetActiveIssueIdentifiersAsync(CancellationToken ct);

    /// <summary>
    /// Detects and transitions work items stuck in non-terminal states beyond expected thresholds.
    /// Called once per dispatch cycle before issue polling.
    /// <para>
    /// A no-op in the Kubernetes topology, where the Job Controller's <c>ReconciliationService</c>
    /// owns stuck-item detection: it can see Job state, which this side cannot. The default
    /// implementation returning zero is therefore the effective one.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of stuck items detected and remediated.</returns>
    Task<int> ReconcileStuckItemsAsync(CancellationToken ct) => Task.FromResult(0);

    /// <summary>
    /// Returns the number of active (Pending, Dispatched, or Running) WorkItems whose
    /// <c>TaskType</c> is <c>Decomposition</c>.
    /// <para>
    /// Used by the Scheduler's <c>DispatchScheduler</c> to enforce
    /// <c>MaxConcurrentDecompositions</c> from authoritative DB state. The Scheduler's
    /// in-memory <c>SchedulerRunQueryService.GetActiveRuns()</c> always returns empty,
    /// making the in-process <c>GetAllActiveRuns()</c> gate unreliable; this method
    /// provides the correct cross-process count.
    /// </para>
    /// <para>
    /// The default implementation returns 0. Override in distributor implementations
    /// that have access to the WorkItems database (i.e. <c>KubernetesWorkDistributor</c>).
    /// Returning 0 is safe but disables the gate — all cycles will dispatch decompositions
    /// regardless of <c>MaxConcurrentDecompositions</c>.
    /// </para>
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Count of active decomposition work items.</returns>
    Task<int> GetActiveDecompositionCountAsync(CancellationToken ct) => Task.FromResult(0);
}

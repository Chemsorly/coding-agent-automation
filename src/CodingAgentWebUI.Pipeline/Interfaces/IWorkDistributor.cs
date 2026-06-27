using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstraction for distributing pipeline work across deployment modes.
/// Primary dispatch abstraction consumed by <see cref="Services.PipelineLoopService"/>.
/// <para>
/// Implementations:
/// <list type="bullet">
///   <item><description>LegacyWorkDistributor — no-DB mode, wraps existing AgentJobDispatcher</description></item>
///   <item><description>SignalRWorkDistributor — DB + SignalR mode, inserts WorkItem row + pushes via SignalR</description></item>
///   <item><description>KubernetesWorkDistributor — DB + K8s mode, inserts WorkItem row (DispatchService spawns Jobs)</description></item>
/// </list>
/// </para>
/// </summary>
public interface IWorkDistributor
{
    /// <summary>
    /// Distributes a job for processing. In DB modes, inserts a WorkItem row.
    /// In Legacy mode, delegates to existing AgentJobDispatcher.
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
    Task<bool> CancelJobAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Gets the current status of a distributed job.
    /// Returns <see cref="JobDistributionStatus.Unknown"/> for nonexistent work item IDs without throwing.
    /// </summary>
    /// <param name="jobId">The work item ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current <see cref="JobDistributionStatus"/> of the work item.</returns>
    Task<JobDistributionStatus> GetJobStatusAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Checks whether a specific issue is currently distributed (Pending, Dispatched, or Running).
    /// Used for single-item dedup checks (e.g., manual dispatch from UI).
    /// </summary>
    /// <param name="issueIdentifier">The issue identifier to check.</param>
    /// <param name="issueProviderConfigId">The issue provider config ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the issue has an active (non-terminal) work item.</returns>
    Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId, CancellationToken ct);

    /// <summary>
    /// Returns all currently active (non-terminal) issue identifiers as a set.
    /// Used by PipelineLoopService to batch-load dedup state at cycle start,
    /// avoiding N+1 DB queries in the per-issue dispatch loop.
    /// In Legacy mode: delegates to in-memory OrchestratorRunService + JobDispatcherService.
    /// In DB mode: single SQL query loading all non-terminal (IssueIdentifier, IssueProviderConfigId) pairs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A set of (IssueIdentifier, IssueProviderConfigId) tuples for all active work items.</returns>
    Task<HashSet<(string IssueIdentifier, string IssueProviderConfigId)>> GetActiveIssueIdentifiersAsync(CancellationToken ct);
}

using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Low-level abstraction for dispatching pipeline jobs directly to connected agents.
/// Consumed by <c>LegacyWorkDistributor</c> and <c>JobQueueDrainService</c> in the
/// <c>CodingAgentWebUI.Orchestration</c> assembly.
/// </summary>
/// <remarks>
/// <para>
/// Most callers should use <see cref="IWorkDistributor"/> — the higher-level dispatch
/// abstraction consumed by <c>PipelineLoopService</c>. <see cref="IJobDispatcher"/> is
/// used by the no-DB (Legacy) dispatch path where <c>LegacyWorkDistributor</c> delegates
/// to it, and by <c>JobQueueDrainService</c> which drains the in-memory queue by matching
/// pending jobs to idle agents.
/// </para>
/// <para>
/// In DB modes (SignalR, Kubernetes), <see cref="IWorkDistributor"/> implementations handle
/// dispatch independently and do not use <see cref="IJobDispatcher"/>.
/// </para>
/// </remarks>
public interface IJobDispatcher
{
    /// <summary>
    /// Attempts to dispatch an issue to an available agent.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the issue was dispatched or enqueued successfully;
    /// <c>false</c> if the issue is already being processed or queued.
    /// </returns>
    Task<bool> TryDispatchAsync( // NOSONAR S107 — interface entry point; all params are independent dispatch inputs
        IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderId,
        ProviderConfigId repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        CancellationToken ct,
        string? issueTitle = null,
        PipelineProject? project = null);

    /// <summary>
    /// Attempts to dispatch a PR review job to an available agent.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the review job was dispatched or enqueued successfully;
    /// <c>false</c> if the PR is already being processed or queued.
    /// </returns>
    Task<bool> TryDispatchReviewAsync(ReviewDispatchRequest request, CancellationToken ct, PipelineProject? project = null);

    /// <summary>
    /// Attempts to dispatch a decomposition job to an available agent.
    /// </summary>
    /// <param name="epicIdentifier">The epic issue identifier.</param>
    /// <param name="epicTitle">The epic issue title.</param>
    /// <param name="phaseType">DecompositionAnalysis or Decomposition.</param>
    /// <param name="issueProviderId">Issue provider config ID for the template.</param>
    /// <param name="repoProviderId">Repository provider config ID for the template.</param>
    /// <param name="brainProviderId">Optional brain provider config ID.</param>
    /// <param name="initiatedBy">Who initiated the dispatch (e.g., "loop").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="decompositionSource">Optional source indicator for the decomposition (e.g., "project-level" or "template-level").</param>
    /// <param name="project">Optional project that owns the dispatching template. Used for settings resolution.</param>
    /// <returns>true if dispatched or enqueued; false if already processing.</returns>
    Task<bool> TryDispatchDecompositionAsync( // NOSONAR S107 — interface entry point; all params are independent dispatch inputs
        IssueIdentifier epicIdentifier,
        string epicTitle,
        PipelineRunType phaseType,
        ProviderConfigId issueProviderId,
        ProviderConfigId repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        CancellationToken ct,
        string? decompositionSource = null,
        PipelineProject? project = null);

    /// <summary>
    /// Dispatches a job directly to a pre-selected agent, skipping the dedup checks
    /// (<c>IsIssueQueued</c>/<c>IsIssueBeingProcessed</c>). The caller guarantees
    /// uniqueness via <c>_processingIssues</c>.
    /// </summary>
    /// <param name="agent">The pre-selected agent (already reserved as Busy).</param>
    /// <param name="job">The pending job to dispatch.</param>
    /// <param name="requiredLabels">Resolved required labels for agent matching.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the run was created and dispatched successfully; <c>false</c> on failure.</returns>
    Task<bool> DispatchToAgentDirectAsync(AgentEntry agent, PendingJob job, IReadOnlyList<string> requiredLabels, CancellationToken ct);

    /// <summary>
    /// Whether any agents are registered and available for dispatch.
    /// </summary>
    bool HasRegisteredAgents { get; }

    /// <summary>
    /// Whether the given issue is already being processed or queued.
    /// </summary>
    bool IsIssueBeingProcessedOrQueued(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId);
}

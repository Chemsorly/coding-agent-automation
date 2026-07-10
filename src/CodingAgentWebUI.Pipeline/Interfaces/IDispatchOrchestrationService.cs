using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstraction over dispatch orchestration logic (issue fetch, label swap, profile/QG resolution,
/// run creation, provider config preparation). Consumed by <see cref="Services.PipelineLoopService"/>
/// in DB modes (SignalR/Kubernetes) to build a full <see cref="JobDistributionRequest"/> before
/// calling <see cref="IWorkDistributor.DistributeAsync"/>.
/// <para>
/// NOT registered in Legacy mode (null). <c>PipelineLoopService</c> checks for null before calling.
/// In Legacy mode, <c>LegacyWorkDistributor</c> handles all orchestration internally via
/// <c>AgentJobDispatcher</c>, so this service is not needed.
/// </para>
/// </summary>
public interface IDispatchOrchestrationService
{
    /// <summary>
    /// Performs full orchestration for an implementation issue dispatch and returns
    /// a ready-to-distribute <see cref="JobDistributionRequest"/>.
    /// </summary>
    /// <returns>The distribution request, or null if orchestration failed.</returns>
    Task<JobDistributionRequest?> PrepareDistributionRequestAsync(
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        PipelineProject project,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        PipelineRunType runType = PipelineRunType.Implementation,
        CancellationToken ct = default);

    /// <summary>
    /// Performs full orchestration for a PR review dispatch and returns
    /// a ready-to-distribute <see cref="JobDistributionRequest"/>.
    /// </summary>
    /// <returns>The distribution request, or null if orchestration failed.</returns>
    Task<JobDistributionRequest?> PrepareReviewDistributionRequestAsync(
        ReviewDispatchRequest reviewRequest,
        PipelineProject project,
        CancellationToken ct = default);

    /// <summary>
    /// Performs full orchestration for a decomposition dispatch and returns
    /// a ready-to-distribute <see cref="JobDistributionRequest"/>.
    /// </summary>
    /// <returns>The distribution request, or null if orchestration failed.</returns>
    Task<JobDistributionRequest?> PrepareDecompositionDistributionRequestAsync(
        string epicIdentifier,
        string epicTitle,
        PipelineRunType phaseType,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        PipelineProject project,
        string? decompositionSource = null,
        CancellationToken ct = default);

    /// <summary>
    /// Distributes a pre-prepared request via <see cref="IWorkDistributor.DistributeAsync"/> and
    /// handles the confirm/revert lifecycle:
    /// <list type="bullet">
    ///   <item>On failure → reverts the label and removes the dangling run.</item>
    ///   <item>On success (not queued) → confirms the label swap to <c>agent:in-progress</c>.</item>
    ///   <item>On success (queued) → no label swap (drain service handles it later).</item>
    /// </list>
    /// </summary>
    /// <param name="request">A fully prepared <see cref="JobDistributionRequest"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome of the distribution attempt.</returns>
    Task<DispatchOutcome> DistributeAndFinalizeAsync(JobDistributionRequest request, CancellationToken ct);

    /// <summary>
    /// Reverts the side effects of a failed distribution attempt: swaps the issue label
    /// back to <c>agent:next</c> and removes the dangling <see cref="Models.PipelineRun"/>
    /// created during preparation. Call this when <see cref="IWorkDistributor.DistributeAsync"/>
    /// returns <c>Success = false</c> after a successful <c>PrepareAsync</c>.
    /// </summary>
    /// <param name="request">The request that was prepared but failed to distribute.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RevertFailedDistributionAsync(JobDistributionRequest request, CancellationToken ct);

    /// <summary>
    /// Confirms the distribution was successful by swapping the issue label to
    /// <c>agent:in-progress</c>. Call this when <see cref="IWorkDistributor.DistributeAsync"/>
    /// returns <c>Success = true</c> AND <see cref="DistributionResult.Queued"/> is <c>false</c>
    /// (agent actually accepted the job, not just queued as Pending).
    /// </summary>
    /// <param name="request">The request that was successfully distributed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ConfirmDistributionLabelAsync(JobDistributionRequest request, CancellationToken ct);
}

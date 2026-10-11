using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Abstraction over dispatch orchestration logic (issue fetch, label swap, profile/QG resolution,
/// run creation, provider config preparation). Consumed by <see cref="Services.PipelineLoopService"/>
/// to build a full <see cref="JobDistributionRequest"/> before calling
/// <see cref="IWorkDistributor.DistributeAsync"/>.
/// Always registered. Callers that previously guarded against null can remove those guards.
/// </summary>
public interface IDispatchOrchestrationService
{
    /// <summary>
    /// Performs full orchestration for an implementation issue dispatch and returns
    /// a ready-to-distribute <see cref="JobDistributionRequest"/>.
    /// </summary>
    /// <returns>The distribution request, or null if orchestration failed.</returns>
    Task<JobDistributionRequest?> PrepareDistributionRequestAsync(
        ImplementationDispatchOrchestrationRequest request,
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
        DecompositionDispatchOrchestrationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Performs full orchestration for the triage of an <c>agent:triage</c> tracker issue and returns a
    /// ready-to-distribute <see cref="JobDistributionRequest"/>. A triage reads every enabled repository of
    /// the project, so the request carries the project's repository list.
    /// </summary>
    /// <returns>The distribution request, or null if orchestration failed.</returns>
    Task<JobDistributionRequest?> PrepareTriageDistributionRequestAsync(
        TriageDispatchOrchestrationRequest request,
        CancellationToken ct = default) =>
        throw new NotSupportedException("PrepareTriageDistributionRequestAsync is not implemented by this service");

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
    /// back to the queue label it had before dispatch (<c>agent:epic</c> for a DecompositionAnalysis,
    /// <c>agent:epic-approved</c> for a Decomposition, <c>agent:next</c> for all other run types)
    /// and removes the dangling <see cref="Models.PipelineRun"/>
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

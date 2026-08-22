using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.TestUtilities;

/// <summary>
/// No-op <see cref="IDispatchOrchestrationService"/> for test environments that construct
/// <see cref="CodingAgentWebUI.Pipeline.Services.PipelineLoopService"/> but never exercise
/// the dispatch path. All prepare methods return null (skip dispatch); all lifecycle methods
/// are no-ops.
/// </summary>
public sealed class NullDispatchOrchestrationService : IDispatchOrchestrationService
{
    public Task<JobDistributionRequest?> PrepareDistributionRequestAsync(
        ImplementationDispatchOrchestrationRequest request, CancellationToken ct = default)
        => Task.FromResult<JobDistributionRequest?>(null);

    public Task<JobDistributionRequest?> PrepareReviewDistributionRequestAsync(
        ReviewDispatchRequest reviewRequest, PipelineProject project, CancellationToken ct = default)
        => Task.FromResult<JobDistributionRequest?>(null);

    public Task<JobDistributionRequest?> PrepareDecompositionDistributionRequestAsync(
        DecompositionDispatchOrchestrationRequest request, CancellationToken ct = default)
        => Task.FromResult<JobDistributionRequest?>(null);

    public Task<DispatchOutcome> DistributeAndFinalizeAsync(
        JobDistributionRequest request, CancellationToken ct)
        => Task.FromResult(new DispatchOutcome(false, false, "NullDispatchOrchestrationService — no dispatch"));

    public Task RevertFailedDistributionAsync(JobDistributionRequest request, CancellationToken ct)
        => Task.CompletedTask;

    public Task ConfirmDistributionLabelAsync(JobDistributionRequest request, CancellationToken ct)
        => Task.CompletedTask;
}

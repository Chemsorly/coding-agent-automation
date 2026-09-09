using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// No-op <see cref="IDispatchOrchestrationService"/> for test environments that construct
/// <see cref="CodingAgent.Pipeline.Services.PipelineLoopService"/> but never exercise
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

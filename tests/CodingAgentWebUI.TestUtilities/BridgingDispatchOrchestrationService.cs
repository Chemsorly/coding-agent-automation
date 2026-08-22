using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.TestUtilities;

/// <summary>
/// Test adapter that bridges the old <see cref="IWorkDistributor"/> test API to
/// <see cref="IDispatchOrchestrationService"/>. Used by test helpers that construct
/// <see cref="CodingAgentWebUI.Pipeline.Services.PipelineLoopService"/> with a mock distributor
/// to verify dispatch behavior.
/// <para>
/// Each Prepare* method builds a <see cref="JobDistributionRequest"/> from the orchestration
/// request fields (preserving BrainProviderId, PipelineProviderId, etc.) so that existing tests
/// that capture request fields via <see cref="IWorkDistributor.DistributeAsync"/> callbacks
/// continue to observe the correct values.
/// </para>
/// </summary>
public sealed class BridgingDispatchOrchestrationService(IWorkDistributor distributor)
    : IDispatchOrchestrationService
{
    public Task<JobDistributionRequest?> PrepareDistributionRequestAsync(
        ImplementationDispatchOrchestrationRequest request, CancellationToken ct = default)
        => Task.FromResult<JobDistributionRequest?>(new JobDistributionRequest
        {
            IssueIdentifier = request.IssueIdentifier.Value,
            IssueProviderConfigId = request.IssueProviderId.Value,
            RepoProviderConfigId = request.RepoProviderId.Value,
            BrainProviderConfigId = request.BrainProviderId,
            PipelineProviderConfigId = request.PipelineProviderId,
            InitiatedBy = request.InitiatedBy,
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 3600
        });

    public Task<JobDistributionRequest?> PrepareReviewDistributionRequestAsync(
        ReviewDispatchRequest reviewRequest, PipelineProject project, CancellationToken ct = default)
        => Task.FromResult<JobDistributionRequest?>(new JobDistributionRequest
        {
            IssueIdentifier = reviewRequest.PrIdentifier,
            IssueProviderConfigId = reviewRequest.IssueProviderId.Value,
            RepoProviderConfigId = reviewRequest.RepoProviderId.Value,
            BrainProviderConfigId = reviewRequest.BrainProviderId,
            InitiatedBy = reviewRequest.InitiatedBy,
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "",
            TimeoutSeconds = 3600
        });

    public Task<JobDistributionRequest?> PrepareDecompositionDistributionRequestAsync(
        DecompositionDispatchOrchestrationRequest request, CancellationToken ct = default)
        => Task.FromResult<JobDistributionRequest?>(new JobDistributionRequest
        {
            IssueIdentifier = request.EpicIdentifier.Value,
            IssueProviderConfigId = request.IssueProviderId.Value,
            RepoProviderConfigId = request.RepoProviderId.Value,
            BrainProviderConfigId = request.BrainProviderId,
            InitiatedBy = request.InitiatedBy,
            TaskType = WorkItemTaskType.Decomposition,
            AgentSelector = "",
            TimeoutSeconds = 3600
        });

    public async Task<DispatchOutcome> DistributeAndFinalizeAsync(
        JobDistributionRequest request, CancellationToken ct)
    {
        var result = await distributor.DistributeAsync(request, ct);
        return new DispatchOutcome(result.Success, result.WorkItemId is not null, result.ErrorMessage);
    }

    public Task RevertFailedDistributionAsync(JobDistributionRequest request, CancellationToken ct)
        => Task.CompletedTask;

    public Task ConfirmDistributionLabelAsync(JobDistributionRequest request, CancellationToken ct)
        => Task.CompletedTask;
}

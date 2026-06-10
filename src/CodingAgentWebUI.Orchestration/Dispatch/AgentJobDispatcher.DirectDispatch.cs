using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class AgentJobDispatcher
{
    /// <inheritdoc />
    public async Task<bool> DispatchToAgentDirectAsync(
        AgentEntry agent,
        PendingJob job,
        IReadOnlyList<string> requiredLabels,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(requiredLabels);

        return job.RunType switch
        {
            PipelineRunType.Review => await DispatchReviewToAgentAsync(
                agent,
                new ReviewDispatchRequest
                {
                    PrIdentifier = job.IssueIdentifier,
                    PrBranchName = job.PrBranchName!,
                    PrTitle = job.IssueTitle ?? $"PR #{job.IssueIdentifier}",
                    PrDescription = job.PrDescription ?? string.Empty,
                    PrAuthor = job.PrAuthor,
                    PrUrl = job.PrUrl ?? string.Empty,
                    PrTargetBranch = job.PrTargetBranch ?? "main",
                    IssueProviderId = job.IssueProviderId,
                    RepoProviderId = job.RepoProviderId,
                    BrainProviderId = job.BrainProviderId,
                    InitiatedBy = job.InitiatedBy
                },
                requiredLabels,
                ct,
                project: job.Project),

            PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition =>
                await DispatchDecompositionToAgentAsync(
                    agent,
                    job.IssueIdentifier,
                    job.IssueTitle ?? $"Epic #{job.IssueIdentifier}",
                    job.RunType,
                    job.IssueProviderId,
                    job.RepoProviderId,
                    job.BrainProviderId,
                    job.InitiatedBy,
                    requiredLabels,
                    ct,
                    decompositionSource: job.DecompositionSource,
                    project: job.Project),

            _ => await DispatchToAgentAsync(
                agent,
                job.IssueIdentifier,
                job.IssueProviderId,
                job.RepoProviderId,
                job.BrainProviderId,
                job.PipelineProviderId,
                job.InitiatedBy,
                requiredLabels,
                ct,
                project: job.Project)
        };
    }
}

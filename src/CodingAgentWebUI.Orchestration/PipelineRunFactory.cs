using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Shared factory for creating <see cref="PipelineRun"/> instances from a deserialized
/// <see cref="JobDistributionRequest"/>. Used by startup rehydration.
/// </summary>
public static class PipelineRunFactory
{
    /// <summary>
    /// Creates a <see cref="PipelineRun"/> immediately after a WorkItem is persisted by the API.
    /// Uses <paramref name="workItemId"/> as the RunId so the WorkItem and run share the same ID.
    /// Called from <c>POST /api/work-items</c> to materialise the in-memory run in the API process
    /// (Option A of Req 1a.1 — the API is the single place where both records are created).
    /// </summary>
    /// <param name="workItemId">The newly-persisted WorkItem GUID, used as the RunId.</param>
    /// <param name="request">The <see cref="JobDistributionRequest"/> payload from the WorkItem.</param>
    public static PipelineRun CreateFromWorkItem(Guid workItemId, JobDistributionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Stamp the workItemId onto the request as RunId so FromDistributionRequest uses it.
        var requestWithRunId = request with { RunId = workItemId.ToString() };
        return FromDistributionRequest(requestWithRunId);
    }

    /// <summary>
    /// Creates a <see cref="PipelineRun"/> from a deserialized <see cref="JobDistributionRequest"/>.
    /// </summary>
    /// <param name="request">The deserialized job distribution request (must have non-null RunId).</param>
    /// <param name="agentId">Optional agent ID. Null during rehydration (agents reconnect later).</param>
    /// <param name="initialStep">Optional initial pipeline step. Defaults to <see cref="PipelineStep.Created"/>.</param>
    /// <param name="startedAt">Optional explicit start time. When null, defaults to <see cref="DateTimeOffset.UtcNow"/>.
    /// Used during rehydration to preserve the original dispatch timestamp.</param>
    public static PipelineRun FromDistributionRequest(
        JobDistributionRequest request,
        AgentId? agentId = null,
        PipelineStep? initialStep = null,
        DateTimeOffset? startedAt = null)
    {
        var run = request.RunType switch
        {
            PipelineRunType.Review => PipelineRun.CreateReview(new PipelineRunCreationParams
            {
                RunId = request.RunId!,
                IssueIdentifier = request.IssueIdentifier,
                IssueTitle = string.IsNullOrEmpty(request.IssueDetail?.Title) ? request.IssueIdentifier : request.IssueDetail.Title,
                IssueProviderConfigId = request.IssueProviderConfigId,
                RepoProviderConfigId = request.RepoProviderConfigId,
                RunType = PipelineRunType.Review,
                InitiatedBy = request.InitiatedBy ?? "rehydrated",
                AgentId = agentId,
                StartedAt = startedAt,
                ReviewPrBranchName = request.LinkedPullRequest?.BranchName ?? string.Empty,
                ReviewPrTargetBranch = request.ReviewPrTargetBranch ?? string.Empty,
                ReviewPrUrl = request.LinkedPullRequest?.Url,
                ReviewPrDescription = request.ReviewPrDescription,
                ReviewPrAuthor = request.ReviewPrAuthor
            }),
            PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition => PipelineRun.CreateDecomposition(new PipelineRunCreationParams
            {
                RunId = request.RunId!,
                IssueIdentifier = request.IssueIdentifier,
                IssueTitle = string.IsNullOrEmpty(request.IssueDetail?.Title) ? request.IssueIdentifier : request.IssueDetail.Title,
                IssueProviderConfigId = request.IssueProviderConfigId,
                RepoProviderConfigId = request.RepoProviderConfigId,
                RunType = request.RunType,
                InitiatedBy = request.InitiatedBy ?? "rehydrated",
                AgentId = agentId,
                StartedAt = startedAt
            }),
            _ => PipelineRun.CreateImplementation(new PipelineRunCreationParams
            {
                RunId = request.RunId!,
                IssueIdentifier = request.IssueIdentifier,
                IssueTitle = string.IsNullOrEmpty(request.IssueDetail?.Title) ? request.IssueIdentifier : request.IssueDetail.Title,
                IssueProviderConfigId = request.IssueProviderConfigId,
                RepoProviderConfigId = request.RepoProviderConfigId,
                // TODO: InitiatedBy null fallback — consider whether "rehydrated" is the right
                // label for all callers, or whether each dispatch path should pass its own fallback.
                InitiatedBy = request.InitiatedBy ?? "rehydrated",
                AgentId = agentId,
                StartedAt = startedAt
            })
        };

        if (initialStep.HasValue)
            run.CurrentStep = initialStep.Value;

        run.ProjectId = request.ProjectId;
        run.ProjectName = request.ProjectName;

        return run;
    }
}

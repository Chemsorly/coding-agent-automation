using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration;

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
    public static PipelineRun? CreateFromWorkItem(Guid workItemId, JobDistributionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Consolidation runs now produce a real PipelineRun (Phase 1 of issue #3023).
        //
        // IMPORTANT: IssueProviderConfigId is hardcoded to ConsolidationConstants.ProviderConfigId
        // (the sentinel "consolidation") rather than passed through from request.IssueProviderConfigId.
        // AgentJobLifecycleService selects ConsolidationJobCompletionStrategy based on
        // run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId — NOT on RunType.
        // Using any other value would silently route completion through RegularJobCompletionStrategy.
        //
        // TODO: The || condition here means a request with RunType == Consolidation but TaskType != Consolidation
        // is also routed here. This is correct for the current enum range, but a future RunType value that is
        // unrelated to consolidation would be silently misclassified if it shares the same enum integer. Safe
        // today — revisit if new RunType values are added. See review warning (issue #3023).
        if (request.TaskType == WorkItemTaskType.Consolidation ||
            request.RunType == PipelineRunType.Consolidation)
        {
            var consolidationRun = PipelineRun.CreateForRunType(new PipelineRunCreationParams
            {
                RunId = workItemId.ToString(),
                IssueIdentifier = request.IssueIdentifier,
                IssueTitle = string.IsNullOrEmpty(request.IssueDetail?.Title)
                    ? request.IssueIdentifier.Value
                    : request.IssueDetail.Title,
                // IssueProviderConfigId must be the sentinel so that AgentJobLifecycleService
                // routes completion to ConsolidationJobCompletionStrategy (which checks
                // run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId).
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                RepoProviderConfigId = request.RepoProviderConfigId,
                RunType = PipelineRunType.Consolidation,
                InitiatedBy = request.InitiatedBy ?? InitiatedByConstants.ConsolidationManual,
                AgentProviderConfigId = request.AgentProviderConfigId,
                BrainProviderConfigId = request.BrainProviderConfigId,
            });
            consolidationRun.ProjectId = request.ProjectId?.ToString();
            consolidationRun.ProjectName = request.ProjectName;
            // Set consolidation-specific fields so the Consolidation page can populate
            // _lastRuns per template and render the card status. Without these, IsSameScope
            // never matches (ConsolidationType is null ≠ BrainConsolidation) and the card
            // always shows "Never run" even after a successful completion.
            consolidationRun.ConsolidationType = request.ConsolidationRunType;
            consolidationRun.ConsolidationTemplateId = request.ConsolidationTemplateId;
            consolidationRun.ConsolidationTemplateName = request.ConsolidationTemplateName;
            return consolidationRun;
        }

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
        // Consolidation runs must use the sentinel IssueProviderConfigId so that
        // AgentJobLifecycleService routes completion to ConsolidationJobCompletionStrategy
        // (which checks run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId).
        // All other run types pass through the real provider ID from the request.
        var issueProviderConfigId = request.RunType == PipelineRunType.Consolidation
            ? ConsolidationConstants.ProviderConfigId
            : request.IssueProviderConfigId;

        var run = PipelineRun.CreateForRunType(new PipelineRunCreationParams
        {
            RunId = request.RunId!,
            IssueIdentifier = request.IssueIdentifier,
            IssueTitle = string.IsNullOrEmpty(request.IssueDetail?.Title) ? request.IssueIdentifier : request.IssueDetail.Title,
            IssueUrl = request.IssueDetail?.Url,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = request.RepoProviderConfigId,
            RunType = request.RunType,
            // NOTE: InitiatedBy null fallback — "rehydrated" is a reasonable default for
            // dispatch callers that don't supply an explicit value. Each call site can pass
            // its own fallback via request.InitiatedBy if more specificity is needed.
            InitiatedBy = request.InitiatedBy ?? InitiatedByConstants.Rehydrated,
            AgentId = agentId,
            StartedAt = startedAt,
            ReviewPrBranchName = request.LinkedPullRequest?.BranchName ?? string.Empty,
            ReviewPrTargetBranch = request.ReviewPrTargetBranch ?? string.Empty,
            ReviewPrUrl = request.LinkedPullRequest?.Url,
            ReviewPrDescription = request.ReviewPrDescription,
            ReviewPrAuthor = request.ReviewPrAuthor,
            AgentProviderConfigId = request.AgentProviderConfigId,
            BrainProviderConfigId = request.BrainProviderConfigId
        });

        if (initialStep.HasValue)
            run.CurrentStep = initialStep.Value;

        run.ProjectId = request.ProjectId?.ToString();
        run.ProjectName = request.ProjectName;

        return run;
    }
}

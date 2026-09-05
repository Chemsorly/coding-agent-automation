using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

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
    /// <para>
    /// Also starts an <c>ExecutePipeline</c> <see cref="System.Diagnostics.Activity"/> on the run
    /// (<see cref="PipelineRun.OrchestratorActivity"/>) using <see cref="PipelineTelemetry.ActivitySource"/>.
    /// This span is stopped by <c>RunLifecycleManager</c> when the run reaches a terminal state,
    /// providing an end-to-end orchestrator-side trace that Grafana Tempo can surface under
    /// <c>rootServiceName="coding-agent-orchestrator"</c> (fix for issue #2255).
    /// </para>
    /// </summary>
    /// <param name="workItemId">The newly-persisted WorkItem GUID, used as the RunId.</param>
    /// <param name="request">The <see cref="JobDistributionRequest"/> payload from the WorkItem.</param>
    public static PipelineRun? CreateFromWorkItem(Guid workItemId, JobDistributionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Consolidation runs are tracked via ConsolidationRun, not PipelineRun.
        // Return null so the caller skips AddRun and avoids ghost "Impl" entries in Active Runs.
        if (request.TaskType == WorkItemTaskType.Consolidation ||
            request.RunType == PipelineRunType.Consolidation)
            return null;

        // Stamp the workItemId onto the request as RunId so FromDistributionRequest uses it.
        var requestWithRunId = request with { RunId = workItemId.ToString() };
        var run = FromDistributionRequest(requestWithRunId);

        // Start an orchestrator-side ExecutePipeline span (issue #2255).
        // The span spans the full run lifecycle: dispatch → terminal state.
        // PipelineTelemetry.ActivitySource.StartActivity returns null when no ActivityListener is
        // subscribed (e.g. in test environments without a TracerProvider) — all access must be null-guarded.
        var activity = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        activity?.SetTag("pipeline.run_id", run.RunId);
        activity?.SetTag("pipeline.issue", run.IssueIdentifier.Value);
        activity?.SetTag("pipeline.run_type", run.RunType.ToString());
        PipelineTelemetry.SetProjectTags(activity, run.ProjectId, run.ProjectName);
        run.OrchestratorActivity = activity;

        return run;
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
                IssueUrl = request.IssueDetail?.Url,
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
                ReviewPrAuthor = request.ReviewPrAuthor,
                AgentProviderConfigId = request.AgentProviderConfigId,
                BrainProviderConfigId = request.BrainProviderConfigId
            }),
            PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition => PipelineRun.CreateDecomposition(new PipelineRunCreationParams
            {
                RunId = request.RunId!,
                IssueIdentifier = request.IssueIdentifier,
                IssueTitle = string.IsNullOrEmpty(request.IssueDetail?.Title) ? request.IssueIdentifier : request.IssueDetail.Title,
                IssueUrl = request.IssueDetail?.Url,
                IssueProviderConfigId = request.IssueProviderConfigId,
                RepoProviderConfigId = request.RepoProviderConfigId,
                RunType = request.RunType,
                InitiatedBy = request.InitiatedBy ?? "rehydrated",
                AgentId = agentId,
                StartedAt = startedAt,
                AgentProviderConfigId = request.AgentProviderConfigId,
                BrainProviderConfigId = request.BrainProviderConfigId
            }),
            _ => PipelineRun.CreateImplementation(new PipelineRunCreationParams
            {
                RunId = request.RunId!,
                IssueIdentifier = request.IssueIdentifier,
                IssueTitle = string.IsNullOrEmpty(request.IssueDetail?.Title) ? request.IssueIdentifier : request.IssueDetail.Title,
                IssueUrl = request.IssueDetail?.Url,
                IssueProviderConfigId = request.IssueProviderConfigId,
                RepoProviderConfigId = request.RepoProviderConfigId,
                // NOTE: InitiatedBy null fallback — "rehydrated" is a reasonable default for
                // dispatch callers that don't supply an explicit value. Each call site can pass
                // its own fallback via request.InitiatedBy if more specificity is needed.
                InitiatedBy = request.InitiatedBy ?? "rehydrated",
                AgentId = agentId,
                StartedAt = startedAt,
                AgentProviderConfigId = request.AgentProviderConfigId,
                BrainProviderConfigId = request.BrainProviderConfigId
            })
        };

        if (initialStep.HasValue)
            run.CurrentStep = initialStep.Value;

        run.ProjectId = request.ProjectId?.ToString();
        run.ProjectName = request.ProjectName;

        return run;
    }
}

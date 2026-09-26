using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

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
    /// <para>
    /// Also starts an <c>ExecutePipeline</c> <see cref="System.Diagnostics.Activity"/> on the run
    /// (<see cref="PipelineRun.OrchestratorActivity"/>) using <see cref="PipelineTelemetry.ActivitySource"/>.
    /// This span is stopped by <c>RunLifecycleManager</c> when the run reaches a terminal state,
    /// providing an end-to-end orchestrator-side trace that Grafana Tempo can surface under
    /// <c>rootServiceName="coding-agent-web"</c> (fix for issue #2255).
    /// </para>
    /// </summary>
    /// <param name="workItemId">The newly-persisted WorkItem GUID, used as the RunId.</param>
    /// <param name="request">The <see cref="JobDistributionRequest"/> payload from the WorkItem.</param>
    public static PipelineRun? CreateFromWorkItem(Guid workItemId, JobDistributionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Consolidation runs now produce a real PipelineRun (Phase 1 of issue #3023).
        // Early-return before the StartActivity span block — consolidation runs do not get an
        // OrchestratorActivity span (consistent with the original design intent; the comment
        // in PipelineRun.cs noted "Null for Consolidation runs").
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
            // TODO: PipelineRun.CreateImplementation is semantically misnamed for consolidation usage. It works
            // today because CreateImplementation has no RunType guard, but if a guard is ever added to reject
            // non-Implementation run types, this call site will throw at runtime. Consider introducing a
            // CreateConsolidation factory method mirroring CreateDecomposition. See review warning (issue #3023).
            var consolidationRun = PipelineRun.CreateImplementation(new PipelineRunCreationParams
            {
                RunId = workItemId.ToString(),
                IssueIdentifier = request.IssueIdentifier,
                IssueTitle = string.IsNullOrEmpty(request.IssueDetail?.Title)
                    ? request.IssueIdentifier.Value
                    : request.IssueDetail.Title,
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                RepoProviderConfigId = request.RepoProviderConfigId,
                RunType = PipelineRunType.Consolidation,
                InitiatedBy = request.InitiatedBy ?? InitiatedByConstants.ConsolidationManual,
                AgentProviderConfigId = request.AgentProviderConfigId,
                BrainProviderConfigId = request.BrainProviderConfigId,
            });
            consolidationRun.ProjectId = request.ProjectId?.ToString();
            consolidationRun.ProjectName = request.ProjectName;
            return consolidationRun;
        }

        // Stamp the workItemId onto the request as RunId so FromDistributionRequest uses it.
        var requestWithRunId = request with { RunId = workItemId.ToString() };
        var run = FromDistributionRequest(requestWithRunId);

        // Start an orchestrator-side ExecutePipeline span (issue #2255).
        // The span spans the full run lifecycle: dispatch → terminal state.
        // PipelineTelemetry.ActivitySource.StartActivity returns null when no ActivityListener is
        // subscribed (e.g. in test environments without a TracerProvider) — all access must be null-guarded.
        // TODO: If FromDistributionRequest throws between StartActivity and the assignment below,
        // the started Activity will be orphaned (local variable goes out of scope without Dispose).
        // Consider wrapping the tag-setting block in try/catch and calling activity?.Dispose() on
        // exception to prevent open-span leaks in Tempo. See review warning (issue #2255).
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
                // TODO: FromDistributionRequest has no Consolidation arm in this switch — a consolidation
                // work item rehydrated after an API restart (e.g. pod reconnect while a consolidation job is
                // in-flight) will fall through here and produce a run with IssueProviderConfigId =
                // request.IssueProviderConfigId (the real provider ID) rather than ConsolidationConstants.ProviderConfigId.
                // This will silently route completion through RegularJobCompletionStrategy instead of
                // ConsolidationJobCompletionStrategy. Add a PipelineRunType.Consolidation arm that mirrors
                // CreateFromWorkItem's consolidation branch (hardcoding the sentinel ProviderConfigId).
                // See review warning (issue #3023).
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

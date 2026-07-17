using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Shared factory for creating <see cref="PipelineRun"/> instances from a deserialized
/// <see cref="JobDistributionRequest"/>. Used by startup rehydration and
/// <c>PendingWorkItemDrainService</c> recovery path to avoid duplication.
/// </summary>
public static class PipelineRunFactory
{
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
        string? agentId = null,
        PipelineStep? initialStep = null,
        DateTimeOffset? startedAt = null)
    {
        var run = PipelineRun.Create(
            runId: request.RunId!,
            issueIdentifier: request.IssueIdentifier,
            issueTitle: string.IsNullOrEmpty(request.IssueDetail?.Title)
                ? request.IssueIdentifier
                : request.IssueDetail!.Title,
            issueProviderConfigId: request.IssueProviderConfigId.Value,
            repoProviderConfigId: request.RepoProviderConfigId.Value,
            runType: request.RunType,
            // TODO: Behavioral change — the old PendingWorkItemDrainService inline code used "loop" as the
            // null fallback for InitiatedBy. Now that the drain service shares this factory, a null InitiatedBy
            // will be labeled "rehydrated" instead of "loop". In practice InitiatedBy is always set by
            // dispatchers so this is unlikely to trigger, but consider whether the drain path should pass
            // its own fallback or if "rehydrated" is acceptable for both callers.
            initiatedBy: request.InitiatedBy ?? "rehydrated",
            agentId: agentId,
            startedAt: startedAt);

        if (initialStep.HasValue)
            run.CurrentStep = initialStep.Value;

        return run;
    }
}

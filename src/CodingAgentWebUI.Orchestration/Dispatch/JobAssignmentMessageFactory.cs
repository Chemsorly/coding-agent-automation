using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Maps a <see cref="JobDistributionRequest"/> onto the <see cref="JobAssignmentMessage"/> handed
/// to an agent.
/// </summary>
/// <remarks>
/// Previously a static member of <c>DbWorkDistributorBase</c>. That class was deleted once
/// KubernetesWorkDistributor became a pure Pipeline API client and left it without a production
/// subclass; this mapping was the only part of it still reachable from production code.
/// </remarks>
public static class JobAssignmentMessageFactory
{
    /// <summary>
    /// Builds a <see cref="JobAssignmentMessage"/> from a <see cref="JobDistributionRequest"/>.
    /// Used by the WorkItem HTTP endpoints to serve a K8s job its assignment.
    /// </summary>
    public static JobAssignmentMessage BuildJobAssignmentMessage(Guid workItemId, JobDistributionRequest request)
    {
        return new JobAssignmentMessage
        {
            JobId = workItemId.ToString(),
            IssueIdentifier = request.IssueIdentifier,
            IssueDetail = request.IssueDetail ?? new IssueDetail
            {
                Identifier = request.IssueIdentifier,
                Title = string.Empty,
                Description = string.Empty,
                Labels = []
            },
            ParsedIssue = request.ParsedIssue ?? new ParsedIssue
            {
                AcceptanceCriteria = [],
                RequirementsSection = string.Empty
            },
            IssueComments = request.IssueComments ?? [],
            ExistingAnalysis = request.ExistingAnalysis,
            ForceRefreshAnalysis = request.ForceRefreshAnalysis,
            LinkedPullRequest = request.LinkedPullRequest,
            LinkedIssueContexts = request.LinkedIssueContexts,
            RepoProviderConfigId = request.RepoProviderConfigId,
            AgentProviderConfigId = request.AgentProviderConfigId ?? request.RepoProviderConfigId,
            BrainProviderConfigId = request.BrainProviderConfigId,
            PipelineProviderConfigId = request.PipelineProviderConfigId,
            ProviderConfigs = request.ProviderConfigs ?? [],
            PipelineConfiguration = request.PipelineConfiguration ?? new PipelineConfiguration(),
            InitiatedBy = request.InitiatedBy,
            ResolvedProfileId = request.ResolvedProfileId,
            QualityGateConfigs = request.QualityGateConfigs ?? [],
            McpServers = request.McpServers ?? [],
            ReviewerConfigs = request.ReviewerConfigs ?? [],
            RunType = request.RunType,
            ReviewPrTargetBranch = request.ReviewPrTargetBranch,
            ReviewPrDescription = request.ReviewPrDescription,
            ReviewPrAuthor = request.ReviewPrAuthor,
            ProjectContext = request.ProjectContext,
            ProjectId = request.ProjectId,
            ProjectName = request.ProjectName,
            ProjectSteeringContent = request.ProjectSteeringContent,
            RepoSteeringContent = request.RepoSteeringContent,
            TraceContext = request.TraceContext,
            IssueProviderConfigId = request.IssueProviderConfigId,
            TaskType = request.TaskType,
            ConsolidationRunType = request.ConsolidationRunType,
            ConsolidationTemplateId = request.ConsolidationTemplateId,
            ConsolidationWorkspacePath = request.ConsolidationWorkspacePath,
            AutoDispatch = request.AutoDispatch,
            StalenessSignal = request.StalenessSignal,
            AnalysisRefreshCount = request.AnalysisRefreshCount
            // NOTE: ProjectSecrets are NOT serialized to WorkItem payload (security).
            // Injected at delivery time from IProjectStore.
        };
    }
}

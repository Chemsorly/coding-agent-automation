using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Single construction point for <see cref="ActiveJobState"/> instances.
/// Resolves <see cref="ActiveJobState.ModelName"/> from the agent provider config's
/// <see cref="ProviderSettingKeys.Model"/> setting, and <see cref="ActiveJobState.RepositoryName"/>
/// from the repo provider config's Owner/Repo settings.
/// </summary>
/// <remarks>
/// Eliminates the divergence where <see cref="WorkItemAgentService"/> (K8s mode)
/// and <see cref="AgentWorkerService"/> (SignalR mode) each constructed <see cref="ActiveJobState"/>
/// independently, both omitting ModelName and RepositoryName.
/// </remarks>
public static class ActiveJobStateFactory
{
    /// <summary>
    /// Creates an <see cref="ActiveJobState"/> from a <see cref="JobAssignmentMessage"/>,
    /// resolving ModelName and RepositoryName from the embedded ProviderConfigs.
    /// </summary>
    /// <param name="runId">The run/job ID to report back to the orchestrator.</param>
    /// <param name="assignment">The job assignment containing all context.</param>
    /// <param name="currentStep">The current pipeline step.</param>
    /// <param name="startedAt">When the job started executing.</param>
    public static ActiveJobState Create(
        string runId,
        JobAssignmentMessage assignment,
        PipelineStep currentStep,
        DateTimeOffset startedAt)
    {
        return new ActiveJobState
        {
            RunId = runId,
            IssueIdentifier = assignment.IssueIdentifier,
            IssueTitle = assignment.IssueDetail?.Title is { Length: > 0 } title
                ? title
                : assignment.IssueIdentifier,
            IssueProviderConfigId = assignment.IssueProviderConfigId ?? assignment.RepoProviderConfigId,
            RepoProviderConfigId = assignment.RepoProviderConfigId,
            AgentProviderConfigId = assignment.AgentProviderConfigId,
            BrainProviderConfigId = assignment.BrainProviderConfigId,
            PipelineProviderConfigId = assignment.PipelineProviderConfigId,
            InitiatedBy = assignment.InitiatedBy,
            ResolvedProfileId = assignment.ResolvedProfileId,
            ProjectId = assignment.ProjectId,
            ProjectName = assignment.ProjectName,
            CurrentStep = currentStep,
            StartedAt = startedAt,
            RunType = assignment.RunType,
            RepositoryName = ResolveRepositoryName(assignment),
            ModelName = ResolveModelName(assignment)
        };
    }

    /// <summary>
    /// Resolves the model name from the agent provider config in the assignment's ProviderConfigs.
    /// Returns null if the config is not found or doesn't have a Model setting.
    /// </summary>
    private static string? ResolveModelName(JobAssignmentMessage assignment)
    {
        var agentConfig = assignment.ProviderConfigs
            .FirstOrDefault(c => c.Id == assignment.AgentProviderConfigId);

        return agentConfig?.Settings.GetValueOrDefault(ProviderSettingKeys.Model);
    }

    /// <summary>
    /// Resolves the repository name as "{Owner}/{Repo}" from the repo provider config.
    /// Returns null if the config is not found or is missing Owner/Repo settings.
    /// </summary>
    private static string? ResolveRepositoryName(JobAssignmentMessage assignment)
    {
        var repoConfig = assignment.ProviderConfigs
            .FirstOrDefault(c => c.Id == assignment.RepoProviderConfigId);

        if (repoConfig is null)
            return null;

        var owner = repoConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Owner);
        var repo = repoConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Repo);

        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            return null;

        return $"{owner}/{repo}";
    }
}

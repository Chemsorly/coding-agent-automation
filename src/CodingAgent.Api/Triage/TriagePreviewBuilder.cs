using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Api.Triage;

/// <summary>
/// What an operator triage of a project will use (<c>GET /api/ui/triages/preview</c>), shown on the new-triage
/// form so the operator can judge whether the agent can see what it needs. Built with the resolvers the API
/// uses to dispatch the run.
/// </summary>
public static class TriagePreviewBuilder
{
    /// <summary>
    /// The executor is the start-in template when it is an enabled template of the project, else the first
    /// enabled template by name, as the API chooses it. Null when the project has no enabled template.
    /// </summary>
    public static PipelineJobTemplate? SelectExecutor(IReadOnlyList<PipelineJobTemplate> projectTemplates, string? startInTemplateId)
    {
        ArgumentNullException.ThrowIfNull(projectTemplates);
        var enabled = projectTemplates.Where(t => t.Enabled).ToList();
        return enabled.FirstOrDefault(t => t.Id == startInTemplateId)
            ?? TemplateOrder.ByName(enabled, t => t.Name, t => t.Id).FirstOrDefault();
    }

    public static TriagePreview Build(
        PipelineProject project,
        IReadOnlyList<PipelineJobTemplate> projectTemplates,
        string? startInTemplateId,
        IReadOnlyList<ProviderConfig> repoConfigs,
        IReadOnlyList<AgentProfile> profiles,
        PipelineConfiguration globalConfig)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(projectTemplates);
        ArgumentNullException.ThrowIfNull(repoConfigs);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(globalConfig);

        var executor = SelectExecutor(projectTemplates, startInTemplateId);
        var config = PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project);
        if (executor is null)
            return new TriagePreview { TimeLimit = config.AgentTimeout, Problem = "The project has no enabled template, so no agent can run a triage." };

        var enabled = TemplateOrder.ByName(projectTemplates.Where(t => t.Enabled), t => t.Name, t => t.Id).ToList();
        var repositories = enabled
            .Select(t => new TriagePreviewRepository(t.Id, t.Name, t.Id == executor.Id))
            .OrderByDescending(r => r.IsExecutor)
            .ToList();

        var trackers = enabled.Select(t => t.IssueProviderId)
            .Append(project.EpicIssueProviderId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .Count();

        var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == executor.RepoProviderId);
        var enrichedRepoConfig = repoConfig is not null
            ? RepositorySettings.WithTemplateSettings(repoConfig, executor)
            : null;
        var labels = LabelResolver.ResolveRequiredLabels(enrichedRepoConfig, globalConfig);
        var profile = ProfileResolver.ResolveByRequiredLabels(profiles, labels);

        return new TriagePreview
        {
            Repositories = repositories,
            McpServers = profile is null ? [] : Servers(profile.McpServers, project.McpServers),
            TrackerCount = trackers,
            MaxOpenIssuesPerTracker = config.MaxOpenIssuesForContext,
            HasBrain = executor.BrainProviderId is not null,
            HasProjectSteering = !string.IsNullOrWhiteSpace(project.SteeringContent),
            TimeLimit = config.AgentTimeout,
            Problem = profile is null
                ? $"No agent profile matches the labels of '{executor.Name}' ({string.Join(", ", labels)}), so the triage cannot start."
                : null,
        };
    }

    /// <summary>The merged servers with where each comes from; disabled servers are left out.</summary>
    private static List<TriagePreviewServer> Servers(IReadOnlyList<McpServerConfig> profileServers, IReadOnlyList<McpServerConfig>? projectServers)
    {
        var fromProject = new HashSet<string>((projectServers ?? []).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        return McpServerMerge.Merge(profileServers, projectServers)
            .Where(s => !s.Disabled)
            .Select(s => new TriagePreviewServer(s.Name, fromProject.Contains(s.Name) ? "project" : "profile"))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

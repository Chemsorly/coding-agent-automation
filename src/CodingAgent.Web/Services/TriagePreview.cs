using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Web.Services;

/// <summary>
/// What an operator triage of a project will use, shown on the new-triage form so the operator can judge
/// whether the agent can see what it needs. Built with the resolvers the API uses to dispatch the run.
/// </summary>
public sealed record TriagePreview
{
    /// <summary>The project's enabled repositories; the executor runs the job, the others are read-only.</summary>
    public IReadOnlyList<TriagePreviewRepository> Repositories { get; init; } = [];

    /// <summary>The MCP servers merged into the run (agent profile, then project), by name.</summary>
    public IReadOnlyList<TriagePreviewServer> McpServers { get; init; } = [];

    /// <summary>Trackers whose open issues the agent downloads.</summary>
    public int TrackerCount { get; init; }

    public int MaxOpenIssuesPerTracker { get; init; }
    public bool HasBrain { get; init; }
    public bool HasProjectSteering { get; init; }
    public TimeSpan TimeLimit { get; init; }

    /// <summary>Why the run cannot be dispatched as configured, or null.</summary>
    public string? Problem { get; init; }

    public TriagePreviewRepository? Executor => Repositories.FirstOrDefault(r => r.IsExecutor);
}

public sealed record TriagePreviewRepository(string TemplateId, string Name, bool IsExecutor);

/// <param name="Origin"><c>profile</c> or <c>project</c>.</param>
public sealed record TriagePreviewServer(string Name, string Origin);

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
        var labels = LabelResolver.ResolveRequiredLabels(repoConfig, globalConfig);
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

using CodingAgent.Pipeline.Models;

namespace CodingAgent.Contracts.Ui;

/// <summary>
/// A project as every role's pages list it (<c>GET /api/ui/projects</c>). It carries no settings,
/// prompts, secrets or MCP servers; those stay behind Settings.
/// </summary>
public sealed record UiProject(string Id, string Name, bool Enabled);

/// <summary>A job template as every role's pages list it (<c>GET /api/ui/projects/{id}/templates</c>).</summary>
public sealed record UiTemplate(string Id, string Name, bool Enabled);

/// <summary>
/// The pipeline settings that pages outside Settings read (<c>GET /api/ui/pipeline-settings</c>).
/// Global values; project overrides are not applied.
/// </summary>
public sealed record UiPipelineSettings(int MaxRetries, int TriageRetentionDays);

/// <summary>Body of <c>POST /api/ui/settings/templates/move</c>.</summary>
public sealed record UiMoveTemplateRequest(string SourceProjectId, string TargetProjectId, string TemplateId);

/// <summary>What the first-run banner needs (<c>GET /api/ui/settings/first-run</c>, admin).</summary>
public sealed record UiFirstRunState(bool HasEnabledTemplates, bool BannerDismissed);

/// <summary>
/// What the Pipelines page shows (<c>GET /api/ui/pipelines</c>, spec 051 2.7): the templates of the projects
/// the user can read, grouped by those projects, and the configuration the template table names and matches
/// labels against. Projects carry only their id, name, enabled flag and templates; providers and agent profiles
/// come with their secrets masked. A project-scoped user gets only the providers their templates use, and no
/// quality gates or reviewers (global configuration). Agent profiles go to the global admin only (spec 053 D17);
/// every reader gets the resolved routes of the templates they see.
/// </summary>
/// <param name="RepoProviders">Repository providers of every role, brain repositories included.</param>
/// <param name="Routes">Per template Id, the resolved route of each task type.</param>
public sealed record UiPipelinesPage(
    IReadOnlyList<PipelineProject> Projects,
    IReadOnlyList<PipelineJobTemplate> Templates,
    IReadOnlyList<ProviderConfig> IssueProviders,
    IReadOnlyList<ProviderConfig> RepoProviders,
    IReadOnlyList<ProviderConfig> PipelineProviders,
    IReadOnlyList<QualityGateConfiguration> QualityGateConfigs,
    IReadOnlyList<ReviewerConfiguration> ReviewerConfigs,
    IReadOnlyList<AgentProfile> AgentProfiles,
    IReadOnlyDictionary<string, IReadOnlyList<UiResolvedRoute>> Routes,
    int MaxRetries);

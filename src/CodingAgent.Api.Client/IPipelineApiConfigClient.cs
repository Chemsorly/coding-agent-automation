using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/config endpoint group.
/// Method names are consumed verbatim by Specs 043 and 045.
/// Spec 051: Settings writes (agent profiles, quality gates, reviewers, projects, import and export,
/// key-value) are not here; the web host makes them as the signed-in admin through /api/ui/settings.
/// </summary>
public interface IPipelineApiConfigClient
{
    // Pipeline config
    Task<PipelineConfiguration> GetPipelineConfigAsync(CancellationToken ct = default);
    Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Atomically loads, transforms, and saves the pipeline configuration.
    /// Client-side read-modify-write: calls GetPipelineConfigAsync, applies transform, then SavePipelineConfigAsync.
    /// </summary>
    Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct = default);

    // Provider configs
    /// <summary>
    /// Provider configs with Settings and Secrets values masked as "****". The safe default —
    /// use this for anything that renders configs in the UI.
    /// </summary>
    Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsAsync(ProviderKind kind, CancellationToken ct = default);

    /// <summary>
    /// Provider configs with live Settings and Secrets values.
    ///
    /// Only the config-store adapters feeding the dispatch path need this: the configs they load
    /// are embedded in the job payload an agent executes with, where
    /// <c>RunEnvironmentSetupStep</c> writes Secrets into the run environment and the provider
    /// resolvers read tokens and base URLs out of Settings. Serving that path the redacted form
    /// ships every job with "****" in place of its credentials.
    ///
    /// Kept as a separate method rather than a flag on <see cref="GetProviderConfigsAsync"/> so
    /// that reaching for live credentials is visible at the call site and greppable. Both forms
    /// require the operator key.
    /// </summary>
    Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsWithSecretsAsync(ProviderKind kind, CancellationToken ct = default);
    Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct = default);
    Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct = default);

    // Agent profiles
    Task<IReadOnlyList<AgentProfile>> GetAgentProfilesAsync(CancellationToken ct = default);

    // Quality gate configs
    Task<IReadOnlyList<QualityGateConfiguration>> GetQualityGateConfigsAsync(CancellationToken ct = default);

    // Reviewer configs
    Task<IReadOnlyList<ReviewerConfiguration>> GetReviewerConfigsAsync(CancellationToken ct = default);

    // Projects
    Task<IReadOnlyList<PipelineProject>> GetProjectsAsync(CancellationToken ct = default);
    Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct = default);

    // Templates
    Task<IReadOnlyList<PipelineJobTemplate>> GetAllTemplatesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PipelineJobTemplate>> GetTemplatesForProjectAsync(string projectId, CancellationToken ct = default);
    Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct = default);
    Task DeleteTemplateAsync(string projectId, string templateId, CancellationToken ct = default);
    Task MoveTemplateAsync(ProjectId sourceProjectId, ProjectId targetProjectId, string templateId, CancellationToken ct = default);

    // Model fetch — Option A passthrough via GET /api/config/models
    Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> GetModelsAsync(CancellationToken ct = default);
}

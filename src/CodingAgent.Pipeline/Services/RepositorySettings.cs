using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Helpers for reading repository settings from a <see cref="PipelineJobTemplate"/> and
/// projecting them onto a <see cref="ProviderConfig"/> before any reader consumes the config.
/// The five fields (RequiredLabels, Secrets, SetupSteps, SteeringContent, BlacklistedPaths)
/// are stored on the template; the provider config carries them only as a runtime copy.
/// </summary>
public static class RepositorySettings
{
    /// <summary>
    /// Returns the template that owns <paramref name="repoProviderId"/>.
    /// Among all templates with a matching <see cref="PipelineJobTemplate.RepoProviderId"/>,
    /// the enabled template is preferred; when none is enabled, any disabled one is returned.
    /// Returns null when no template references the provider.
    /// </summary>
    public static PipelineJobTemplate? TemplateFor(
        string repoProviderId,
        IReadOnlyList<PipelineJobTemplate> templates)
    {
        // TODO: [WARNING] ArgumentNullException.ThrowIfNull(templates) is missing; a null argument
        // causes NullReferenceException with no indication of which parameter was null.
        // Add: ArgumentNullException.ThrowIfNull(templates);
        return templates
            .Where(t => t.RepoProviderId == repoProviderId)
            .OrderByDescending(t => t.Enabled)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns a copy of <paramref name="repoConfig"/> with the five repository-settings
    /// fields populated from <paramref name="template"/>.
    /// When <paramref name="template"/> is null, all five fields are set to null.
    /// A null or empty value on the template produces a null field on the result.
    /// All other <see cref="ProviderConfig"/> fields are copied unchanged.
    /// </summary>
    public static ProviderConfig WithTemplateSettings(
        ProviderConfig repoConfig,
        PipelineJobTemplate? template)
    {
        // TODO: [WARNING] ArgumentNullException.ThrowIfNull(repoConfig) is missing; a null argument
        // causes NullReferenceException accessing repoConfig.DisplayName below.
        // Add: ArgumentNullException.ThrowIfNull(repoConfig);
        return new ProviderConfig
        {
            // Connection fields — copied unchanged
            DisplayName = repoConfig.DisplayName,
            Id = repoConfig.Id,
            Kind = repoConfig.Kind,
            ProviderType = repoConfig.ProviderType,
            RepositoryRole = repoConfig.RepositoryRole,
            Settings = repoConfig.Settings,

            // Repository settings — taken from the template
            RequiredLabels = template?.RequiredLabels is { Count: > 0 } rl ? rl : null,
            Secrets = template?.Secrets is { Count: > 0 } sec ? sec : null,
            SetupSteps = template?.SetupSteps is { Count: > 0 } ss ? ss : null,
            SteeringContent = !string.IsNullOrEmpty(template?.SteeringContent) ? template.SteeringContent : null,
            BlacklistedPaths = template?.BlacklistedPaths is { Count: > 0 } bp ? bp : null,
        };
    }
}

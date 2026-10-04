using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Static helper that determines which <see cref="PipelineJobTemplate"/> instances
/// support each <see cref="ConsolidationRunType"/> based on their provider configuration.
/// Reusable by both the <see cref="IConsolidationService"/> and the Consolidation UI page.
/// </summary>
public static class ConsolidationTemplateFilter
{
    /// <summary>
    /// Returns <c>true</c> if the template has a configured brain provider,
    /// which is required for brain consolidation.
    /// </summary>
    /// <param name="template">The pipeline job template to check.</param>
    /// <returns><c>true</c> if brain consolidation is supported; otherwise <c>false</c>.</returns>
    public static bool SupportsBrainConsolidation(PipelineJobTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return !string.IsNullOrWhiteSpace(template.BrainProviderId);
    }

    /// <summary>
    /// Whether the brain is read-only for the template's runs. Brain consolidation writes to the brain, so it
    /// does not run from a template whose brain is read-only. The template's own flag can only make the brain
    /// read-only; otherwise the global setting with the project's override decides, as for the runs themselves.
    /// </summary>
    /// <param name="template">The template the brain consolidation would run from.</param>
    /// <param name="project">The template's project, or <c>null</c> when it has none.</param>
    /// <param name="globalConfig">The current global configuration.</param>
    public static bool IsBrainReadOnly(PipelineJobTemplate template, PipelineProject? project, PipelineConfiguration globalConfig)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(globalConfig);
        return template.BrainReadOnly
            || PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project).BrainReadOnly;
    }

    /// <summary>
    /// Returns <c>true</c> if the template has both a repo provider and an issue provider configured,
    /// which are required for refactoring detection (clone repo + create issues).
    /// </summary>
    /// <param name="template">The pipeline job template to check.</param>
    /// <returns><c>true</c> if refactoring detection is supported; otherwise <c>false</c>.</returns>
    public static bool SupportsRefactoringDetection(PipelineJobTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return !string.IsNullOrWhiteSpace(template.RepoProviderId)
            && !string.IsNullOrWhiteSpace(template.IssueProviderId);
    }
}

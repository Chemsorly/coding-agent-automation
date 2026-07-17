using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

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
        return !string.IsNullOrWhiteSpace(template.BrainProviderId?.Value);
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
        return !string.IsNullOrWhiteSpace(template.RepoProviderId.Value)
            && !string.IsNullOrWhiteSpace(template.IssueProviderId.Value);
    }

    /// <summary>
    /// Returns the list of consolidation types supported by the given template.
    /// Harness suggestions are global (not template-scoped) and are never included here.
    /// </summary>
    /// <param name="template">The pipeline job template to check.</param>
    /// <returns>A list of supported consolidation run types for this template.</returns>
    public static IReadOnlyList<ConsolidationRunType> GetSupportedTypes(PipelineJobTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var types = new List<ConsolidationRunType>();

        if (SupportsBrainConsolidation(template))
            types.Add(ConsolidationRunType.BrainConsolidation);

        if (SupportsRefactoringDetection(template))
            types.Add(ConsolidationRunType.RefactoringDetection);

        return types;
    }

    /// <summary>
    /// Filters a collection of templates to only those that support the specified consolidation type.
    /// For <see cref="ConsolidationRunType.HarnessSuggestions"/>, returns an empty list
    /// (harness suggestions are global and not template-scoped).
    /// </summary>
    /// <param name="templates">The templates to filter.</param>
    /// <param name="type">The consolidation run type to filter by.</param>
    /// <returns>Templates that support the specified consolidation type.</returns>
    public static IReadOnlyList<PipelineJobTemplate> FilterByType(
        IEnumerable<PipelineJobTemplate> templates,
        ConsolidationRunType type)
    {
        ArgumentNullException.ThrowIfNull(templates);

        return type switch
        {
            ConsolidationRunType.BrainConsolidation => templates
                .Where(SupportsBrainConsolidation)
                .ToList(),
            ConsolidationRunType.RefactoringDetection => templates
                .Where(SupportsRefactoringDetection)
                .ToList(),
            ConsolidationRunType.HarnessSuggestions => [],
            _ => []
        };
    }
}

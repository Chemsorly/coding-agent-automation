using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Static helper for resolving required agent labels from repository and pipeline configuration.
/// Extracted from JobDeduplicationGuardService to allow usage from both the WebUI project (dispatch path)
/// and the Pipeline project without circular dependencies.
/// </summary>
public static class LabelResolver
{
    /// <summary>
    /// Resolves the required agent labels for a repository provider config.
    /// Resolution order: <see cref="ProviderConfig.RequiredLabels"/> property →
    /// <see cref="PipelineConfiguration.DefaultRequiredAgentLabels"/> → empty (any agent).
    /// </summary>
    public static IReadOnlyList<string> ResolveRequiredLabels(
        ProviderConfig? repoConfig,
        PipelineConfiguration pipelineConfig)
    {
        // 0. Check the explicit RequiredLabels property first
        if (repoConfig?.RequiredLabels is { Count: > 0 } explicitLabels)
        {
            return explicitLabels;
        }

        // 1. Fall back to pipeline-level default
        if (!string.IsNullOrWhiteSpace(pipelineConfig?.DefaultRequiredAgentLabels))
        {
            return pipelineConfig.DefaultRequiredAgentLabels
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
                .AsReadOnly();
        }

        // 2. No labels required — any agent matches
        return [];
    }
}

using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Static helper for resolving required agent labels from repository and pipeline configuration.
/// Extracted from JobDispatcherService to allow usage from both the WebUI project (dispatch path)
/// and the Pipeline project (local execution path) without circular dependencies.
/// </summary>
public static class LabelResolver
{
    /// <summary>
    /// Resolves the required agent labels for a repository provider config.
    /// Resolution order: <see cref="ProviderConfig.RequiredLabels"/> property →
    /// repo <c>requiredAgentLabels</c> setting →
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

        // 1. Check repo-level setting (legacy dictionary approach)
        if (repoConfig?.Settings.TryGetValue(ProviderSettingsKeys.RequiredAgentLabels, out var repoLabels) == true
            && !string.IsNullOrWhiteSpace(repoLabels))
        {
            return repoLabels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
                .AsReadOnly();
        }

        // 2. Fall back to pipeline-level default
        if (!string.IsNullOrWhiteSpace(pipelineConfig?.Agent.DefaultRequiredAgentLabels))
        {
            return pipelineConfig.Agent.DefaultRequiredAgentLabels
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
                .AsReadOnly();
        }

        // 3. No labels required — any agent matches
        return [];
    }
}

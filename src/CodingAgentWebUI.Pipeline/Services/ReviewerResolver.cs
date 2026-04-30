using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Stateless service responsible for resolving which Reviewer Configurations apply to a job.
/// Matching uses label intersection with case-insensitive comparison. Configurations with empty
/// MatchLabels act as global fallbacks and always match.
/// </summary>
public sealed class ReviewerResolver
{
    /// <summary>
    /// Resolves all matching ReviewerConfigurations for a job's required labels, returned in execution order.
    /// </summary>
    /// <param name="allConfigs">All available reviewer configurations.</param>
    /// <param name="jobRequiredLabels">The labels required by the job being dispatched.</param>
    /// <returns>Matching configurations ordered by <see cref="ReviewerConfiguration.ExecutionOrder"/> ascending,
    /// then <see cref="ReviewerConfiguration.DisplayName"/> alphabetically (case-insensitive).</returns>
    public IReadOnlyList<ReviewerConfiguration> Resolve(
        IReadOnlyList<ReviewerConfiguration> allConfigs,
        IReadOnlyList<string> jobRequiredLabels)
    {
        ArgumentNullException.ThrowIfNull(allConfigs);
        ArgumentNullException.ThrowIfNull(jobRequiredLabels);

        var jobLabelSet = new HashSet<string>(jobRequiredLabels, StringComparer.OrdinalIgnoreCase);

        return allConfigs
            .Where(rc => rc.Enabled)
            .Where(rc => IsMatch(rc.MatchLabels, jobLabelSet))
            .OrderBy(rc => rc.ExecutionOrder)
            .ThenBy(rc => rc.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Flattens all ReviewAgents from the given configurations into a single list of
    /// <see cref="ReviewAgentConfig"/> instances, preserving configuration order then agent order.
    /// </summary>
    /// <param name="configs">The resolved reviewer configurations to flatten.</param>
    /// <returns>A flat list of ReviewAgentConfig mapped from all ReviewAgents, or empty if input is null or empty.</returns>
    public static IReadOnlyList<ReviewAgentConfig> FlattenAgents(IReadOnlyList<ReviewerConfiguration>? configs)
    {
        if (configs is null or { Count: 0 })
            return [];

        return configs
            .SelectMany(rc => rc.Agents)
            .Select(ra => new ReviewAgentConfig { Name = ra.Name, Prompt = ra.Prompt })
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// A configuration matches if its MatchLabels is empty (global fallback) or has at least one label
    /// in common with the job's required labels.
    /// </summary>
    private static bool IsMatch(IReadOnlyList<string> matchLabels, HashSet<string> jobLabelSet)
    {
        if (matchLabels.Count == 0)
            return true;

        return matchLabels.Any(label => jobLabelSet.Contains(label));
    }
}

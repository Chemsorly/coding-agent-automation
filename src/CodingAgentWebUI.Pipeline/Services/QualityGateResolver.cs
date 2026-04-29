using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Stateless service responsible for resolving which Quality Gate Configurations apply to a job.
/// Matching uses label intersection with case-insensitive comparison. QGCs with empty MatchLabels
/// act as global fallbacks and always match.
/// </summary>
public sealed class QualityGateResolver
{
    /// <summary>
    /// Resolves all matching QGCs for a job's required labels, returned in execution order.
    /// </summary>
    /// <param name="allConfigs">All available quality gate configurations.</param>
    /// <param name="jobRequiredLabels">The labels required by the job being dispatched.</param>
    /// <returns>Matching QGCs ordered by <see cref="QualityGateConfiguration.ExecutionOrder"/> ascending,
    /// then <see cref="QualityGateConfiguration.DisplayName"/> alphabetically (case-insensitive).</returns>
    public IReadOnlyList<QualityGateConfiguration> Resolve(
        IReadOnlyList<QualityGateConfiguration> allConfigs,
        IReadOnlyList<string> jobRequiredLabels)
    {
        ArgumentNullException.ThrowIfNull(allConfigs);
        ArgumentNullException.ThrowIfNull(jobRequiredLabels);

        var jobLabelSet = new HashSet<string>(jobRequiredLabels, StringComparer.OrdinalIgnoreCase);

        return allConfigs
            .Where(qgc => qgc.Enabled)
            .Where(qgc => IsMatch(qgc.MatchLabels, jobLabelSet))
            .OrderBy(qgc => qgc.ExecutionOrder)
            .ThenBy(qgc => qgc.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// A QGC matches if its MatchLabels is empty (global fallback) or has at least one label
    /// in common with the job's required labels.
    /// </summary>
    private static bool IsMatch(IReadOnlyList<string> qgcMatchLabels, HashSet<string> jobLabelSet)
    {
        if (qgcMatchLabels.Count == 0)
            return true;

        return qgcMatchLabels.Any(label => jobLabelSet.Contains(label));
    }
}

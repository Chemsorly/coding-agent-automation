using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Stateless service responsible for resolving which Quality Gate Configurations apply to a job.
/// Matching uses label intersection with case-insensitive comparison. QGCs with empty MatchLabels
/// always apply unconditionally (they match every job).
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

        return LabelMatchResolver.Resolve(
            allConfigs,
            jobRequiredLabels,
            enabledPredicate: qgc => qgc.Enabled,
            labelSelector: qgc => qgc.MatchLabels,
            matchStrategy: LabelMatchStrategies.Intersection,
            orderBy: items => items
                .OrderBy(qgc => qgc.ExecutionOrder)
                .ThenBy(qgc => qgc.DisplayName, StringComparer.OrdinalIgnoreCase));
    }
}

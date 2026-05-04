using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Stateless service responsible for matching an agent's labels to the best Agent Profile.
/// Resolution uses subset matching with case-insensitive label comparison, then sorts by
/// specificity (descending), priority (descending), and Id (ascending) to break ties.
/// </summary>
public sealed class ProfileResolver
{
    /// <summary>
    /// Resolves the best matching profile for an agent based on its labels.
    /// Returns <c>null</c> if no enabled profile matches (caller decides error handling).
    /// </summary>
    /// <param name="profiles">All available profiles to evaluate.</param>
    /// <param name="agentLabels">The labels reported by the agent.</param>
    /// <returns>The highest-priority matching profile, or <c>null</c> if none match.</returns>
    public AgentProfile? Resolve(IReadOnlyList<AgentProfile> profiles, IReadOnlyList<string> agentLabels)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(agentLabels);

        return LabelMatchResolver.Resolve(
            profiles,
            agentLabels,
            enabledPredicate: p => p.Enabled,
            labelSelector: p => p.MatchLabels,
            matchStrategy: LabelMatchStrategies.Subset,
            orderBy: items => items
                .OrderByDescending(p => p.MatchLabels.Count)
                .ThenByDescending(p => p.Priority)
                .ThenBy(p => p.Id, StringComparer.Ordinal))
            .FirstOrDefault();
    }
}

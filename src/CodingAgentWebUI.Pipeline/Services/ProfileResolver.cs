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

    /// <summary>
    /// Resolves the best profile whose MatchLabels COVER all required labels.
    /// Used for dispatch: given a set of required labels (from repo config or pipeline defaults),
    /// find the profile whose MatchLabels are a superset — that profile's labels form the
    /// template key in K8s mode. Picks the most specific match (highest MatchLabels count),
    /// then by priority, then by Id for determinism.
    /// Returns <c>null</c> if no enabled profile covers all required labels.
    /// </summary>
    /// <param name="profiles">All available profiles to evaluate.</param>
    /// <param name="requiredLabels">Labels that must ALL be present in the profile's MatchLabels.</param>
    /// <returns>The best matching profile, or <c>null</c> if none cover all required labels.</returns>
    public AgentProfile? ResolveByRequiredLabels(IReadOnlyList<AgentProfile> profiles, IReadOnlyList<string> requiredLabels)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(requiredLabels);

        return LabelMatchResolver.Resolve(
            profiles,
            requiredLabels,
            enabledPredicate: p => p.Enabled,
            labelSelector: p => p.MatchLabels,
            matchStrategy: LabelMatchStrategies.Superset,
            orderBy: items => items
                .OrderByDescending(p => p.MatchLabels.Count)
                .ThenByDescending(p => p.Priority)
                .ThenBy(p => p.Id, StringComparer.Ordinal))
            .FirstOrDefault();
    }
}

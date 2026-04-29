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

        var agentLabelSet = new HashSet<string>(agentLabels, StringComparer.OrdinalIgnoreCase);

        return profiles
            .Where(p => p.Enabled)
            .Where(p => IsSubset(p.MatchLabels, agentLabelSet))
            .OrderByDescending(p => p.MatchLabels.Count)
            .ThenByDescending(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// Checks whether all items in <paramref name="subset"/> exist in <paramref name="superset"/>.
    /// An empty subset matches any superset (default/catch-all profile).
    /// </summary>
    private static bool IsSubset(IReadOnlyList<string> subset, HashSet<string> superset)
    {
        if (subset.Count == 0)
            return true;

        return subset.All(label => superset.Contains(label));
    }
}

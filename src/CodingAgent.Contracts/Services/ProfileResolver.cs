using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Stateless service responsible for matching required labels to the best Agent Profile.
/// <para>
/// <see cref="ResolveByRequiredLabels"/> uses superset (coverage) matching: the profile must
/// cover every required label. Among covering profiles the closest fit wins — fewest labels first
/// (ascending), then priority (descending), then Id (ascending).
/// </para>
/// </summary>
public static class ProfileResolver
{
    /// <summary>
    /// Resolves the best profile whose MatchLabels COVER all required labels.
    /// Used for dispatch: given a set of required labels (from repo config or pipeline defaults),
    /// find the profile whose MatchLabels are a superset of the required labels — that profile's
    /// labels form the template key in K8s mode. Picks the closest fit (fewest MatchLabels),
    /// then higher Priority, then lower Id for determinism.
    /// A profile with more labels than required only wins repositories that carry those extra
    /// labels too, so polyglot and variant profiles do not take over simpler repositories.
    /// Returns <c>null</c> if no enabled profile covers all required labels.
    /// </summary>
    /// <param name="profiles">All available profiles to evaluate.</param>
    /// <param name="requiredLabels">Labels that must ALL be present in the profile's MatchLabels.</param>
    /// <returns>The closest-fit covering profile, or <c>null</c> if none cover all required labels.</returns>
    public static AgentProfile? ResolveByRequiredLabels(IReadOnlyList<AgentProfile> profiles, IReadOnlyList<string> requiredLabels)
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
                .OrderBy(p => p.MatchLabels.Count)
                .ThenByDescending(p => p.Priority)
                .ThenBy(p => p.Id, StringComparer.Ordinal))
            .FirstOrDefault();
    }
}

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Provides reusable label-matching strategy functions for use with <see cref="LabelMatchResolver"/>.
/// Each strategy defines how a configuration's MatchLabels relate to a target label set.
/// </summary>
public static class LabelMatchStrategies
{
    /// <summary>
    /// ANY label in common (intersection). Empty match-labels = global fallback (always matches).
    /// Used by QualityGateResolver and ReviewerResolver.
    /// </summary>
    /// <param name="matchLabels">The configuration's match labels.</param>
    /// <param name="targetSet">The target label set (job labels or agent labels).</param>
    /// <returns><c>true</c> if matchLabels is empty or has at least one label in common with targetSet.</returns>
    public static bool Intersection(IReadOnlyList<string> matchLabels, HashSet<string> targetSet)
        => matchLabels.Count == 0 || matchLabels.Any(l => targetSet.Contains(l));

    /// <summary>
    /// ALL match-labels must exist in target (subset). Empty match-labels = catch-all (always matches).
    /// Used by ProfileResolver.
    /// </summary>
    /// <param name="matchLabels">The configuration's match labels.</param>
    /// <param name="targetSet">The target label set (agent labels).</param>
    /// <returns><c>true</c> if matchLabels is empty or all labels exist in targetSet.</returns>
    public static bool Subset(IReadOnlyList<string> matchLabels, HashSet<string> targetSet)
        => matchLabels.Count == 0 || matchLabels.All(l => targetSet.Contains(l));
}

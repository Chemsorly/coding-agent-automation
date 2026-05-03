namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Generic label-matching resolver that encapsulates the shared resolution pattern used by
/// QualityGateResolver, ReviewerResolver, and ProfileResolver. Filters items by an enabled
/// predicate and a label-matching strategy, then applies ordering.
/// </summary>
public static class LabelMatchResolver
{
    /// <summary>
    /// Resolves matching items from a collection based on label matching.
    /// </summary>
    /// <typeparam name="T">The type of configuration item being resolved.</typeparam>
    /// <param name="items">All available items to evaluate.</param>
    /// <param name="targetLabels">The target labels to match against (job labels or agent labels).</param>
    /// <param name="enabledPredicate">Predicate to filter only enabled items.</param>
    /// <param name="labelSelector">Function to extract match labels from an item.</param>
    /// <param name="matchStrategy">Strategy function determining how labels are matched
    /// (e.g., <see cref="LabelMatchStrategies.Intersection"/> or <see cref="LabelMatchStrategies.Subset"/>).</param>
    /// <param name="orderBy">Function to apply ordering to the filtered results.</param>
    /// <returns>A read-only list of matching items in the specified order.</returns>
    public static IReadOnlyList<T> Resolve<T>(
        IReadOnlyList<T> items,
        IReadOnlyList<string> targetLabels,
        Func<T, bool> enabledPredicate,
        Func<T, IReadOnlyList<string>> labelSelector,
        Func<IReadOnlyList<string>, HashSet<string>, bool> matchStrategy,
        Func<IEnumerable<T>, IOrderedEnumerable<T>> orderBy)
    {
        var targetLabelSet = new HashSet<string>(targetLabels, StringComparer.OrdinalIgnoreCase);

        var filtered = items
            .Where(enabledPredicate)
            .Where(item => matchStrategy(labelSelector(item), targetLabelSet));

        return orderBy(filtered)
            .ToList()
            .AsReadOnly();
    }
}

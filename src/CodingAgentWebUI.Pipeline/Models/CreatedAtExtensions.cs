namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Extension methods for sorting collections by <see cref="IHasCreatedAt.CreatedAt"/>.
/// </summary>
public static class CreatedAtExtensions
{
    /// <summary>
    /// Sorts the list in-place by <see cref="IHasCreatedAt.CreatedAt"/> ascending (FIFO).
    /// Items with a null CreatedAt are sorted last (treated as <see cref="DateTime.MaxValue"/>).
    /// </summary>
    public static void SortByCreatedAtFifo<T>(this List<T> items) where T : IHasCreatedAt
    {
        items.Sort((a, b) => (a.CreatedAt ?? DateTime.MaxValue).CompareTo(b.CreatedAt ?? DateTime.MaxValue));
    }
}

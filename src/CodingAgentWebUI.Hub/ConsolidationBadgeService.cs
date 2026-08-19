namespace CodingAgentWebUI.Hub;

/// <summary>
/// Tracks the badge count for the Consolidation nav item. The count represents
/// refactoring issues created + harness suggestions generated since the user last
/// visited the Consolidation page. Resets to zero on page open.
/// </summary>
/// <remarks>
/// Registered as a singleton in DI. Thread-safe via <see langword="lock"/>.
/// </remarks>
public sealed class ConsolidationBadgeService
{
    private readonly object _lock = new();
    private int _badgeCount;
    private bool _hasEverBeenIncremented;

    /// <summary>
    /// Current badge count (refactoring issues created + harness suggestions since last visit).
    /// </summary>
    public int BadgeCount
    {
        get { lock (_lock) { return _badgeCount; } }
    }

    /// <summary>
    /// Returns <see langword="true"/> if <see cref="IncrementBy"/> has been called with a
    /// positive count at least once since this service instance was created. Used by the UI
    /// to distinguish "zero because nothing ran" from "zero because the user visited the page"
    /// versus "stale — this instance never received any events (e.g. after agents moved to the
    /// API hub)".
    /// </summary>
    public bool HasEverBeenIncremented
    {
        get { lock (_lock) { return _hasEverBeenIncremented; } }
    }

    /// <summary>
    /// Increments badge count when new suggestions or issues are created.
    /// Fires <see cref="OnBadgeChanged"/> after incrementing.
    /// </summary>
    /// <param name="count">The number to add to the badge count. Must be non-negative.</param>
    public void IncrementBy(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative.");

        if (count == 0)
            return;

        lock (_lock)
        {
            _badgeCount += count;
            _hasEverBeenIncremented = true;
        }

        OnBadgeChanged?.Invoke();
    }

    /// <summary>
    /// Resets badge count to zero (called when user opens Consolidation page).
    /// Fires <see cref="OnBadgeChanged"/> after resetting.
    /// Note: <see cref="HasEverBeenIncremented"/> is NOT reset — it persists for the process
    /// lifetime so the UI can distinguish "visited page" from "stale instance".
    /// </summary>
    public void Reset()
    {
        bool changed;
        lock (_lock)
        {
            changed = _badgeCount != 0;
            _badgeCount = 0;
        }

        if (changed)
            OnBadgeChanged?.Invoke();
    }

    /// <summary>
    /// Fired when the badge count changes (increment or reset).
    /// </summary>
    public event Action? OnBadgeChanged;
}

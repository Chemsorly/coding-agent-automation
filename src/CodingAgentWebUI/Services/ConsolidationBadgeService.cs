namespace CodingAgentWebUI.Services;

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

    /// <summary>
    /// Current badge count (refactoring issues created + harness suggestions since last visit).
    /// </summary>
    public int BadgeCount
    {
        get { lock (_lock) { return _badgeCount; } }
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
        }

        OnBadgeChanged?.Invoke();
    }

    /// <summary>
    /// Resets badge count to zero (called when user opens Consolidation page).
    /// Fires <see cref="OnBadgeChanged"/> after resetting.
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

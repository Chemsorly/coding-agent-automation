namespace CodingAgentWebUI.Services;

/// <summary>
/// Lightweight notification service that broadcasts when projects are modified
/// (created, renamed, deleted, enabled/disabled). Subscribers (e.g., MainLayout)
/// can re-read project state to keep the UI in sync.
/// </summary>
public sealed class ProjectChangeNotifier
{
    /// <summary>
    /// Fired when any project is saved, deleted, or toggled.
    /// </summary>
    public event Action? OnChange;

    /// <summary>
    /// Notifies subscribers that projects have changed.
    /// Call this after any project mutation (save, delete, enable/disable).
    /// </summary>
    public void NotifyChanged() => OnChange?.Invoke();
}

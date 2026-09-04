namespace CodingAgentWebUI.Services;

/// <summary>
/// Circuit-scoped state shared across the cockpit shell (redesigned frontend):
/// the selected project scope and the current "needs attention" count shown on the
/// top-bar Attention pill. Components subscribe to <see cref="OnChange"/> to re-render.
/// </summary>
public sealed class CockpitState
{
    /// <summary>Selected project id, or "" for "All projects".</summary>
    public string SelectedProjectId { get; private set; } = "";

    /// <summary>Display name of the selected project, or null for "All projects".</summary>
    public string? SelectedProjectName { get; private set; }

    /// <summary>Aggregate count of items needing a human (see the Attention screen).</summary>
    public int AttentionCount { get; private set; }

    /// <summary>
    /// The selected time window in hours for "recent" metrics on Overview and Insights.
    /// Default is 24 hours. Scoped per-circuit (per user session).
    /// </summary>
    public int RecentWindowHours { get; private set; } = 24;

    /// <summary>Raised on any state change (project or attention count) — the shell subscribes to re-render.</summary>
    public event Action? OnChange;

    /// <summary>
    /// Raised only when the selected project changes. Project-scoped pages subscribe to this to re-query
    /// their data. It is deliberately separate from <see cref="OnChange"/>: <see cref="SetAttentionCount"/>
    /// does NOT raise it, so a page that both re-queries here and updates the attention count cannot loop.
    /// </summary>
    public event Action? OnProjectChanged;

    /// <summary>
    /// Raised when the selected recent-window changes. Pages subscribe to re-filter already-loaded data
    /// without triggering an API re-fetch. Deliberately separate from <see cref="OnProjectChanged"/> so
    /// changing the window does NOT cause a full data reload.
    /// </summary>
    public event Action? OnRecentWindowChanged;

    public void SetProject(string? id, string? name)
    {
        id ??= "";
        if (SelectedProjectId == id) return;
        SelectedProjectId = id;
        SelectedProjectName = name;
        OnChange?.Invoke();
        OnProjectChanged?.Invoke();
    }

    public void SetAttentionCount(int count)
    {
        if (AttentionCount == count) return;
        AttentionCount = count;
        OnChange?.Invoke();
    }

    /// <summary>
    /// Updates the time-window for "recent" metrics and notifies subscribers to re-filter.
    /// No-op when the value is unchanged to avoid spurious re-renders.
    /// </summary>
    public void SetRecentWindowHours(int hours)
    {
        if (RecentWindowHours == hours) return;
        RecentWindowHours = hours;
        OnRecentWindowChanged?.Invoke();
    }
}

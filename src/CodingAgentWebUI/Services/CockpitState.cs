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

    /// <summary>Raised on any state change (project or attention count) — the shell subscribes to re-render.</summary>
    public event Action? OnChange;

    /// <summary>
    /// Raised only when the selected project changes. Project-scoped pages subscribe to this to re-query
    /// their data. It is deliberately separate from <see cref="OnChange"/>: <see cref="SetAttentionCount"/>
    /// does NOT raise it, so a page that both re-queries here and updates the attention count cannot loop.
    /// </summary>
    public event Action? OnProjectChanged;

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
}

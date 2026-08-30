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

    public event Action? OnChange;

    public void SetProject(string? id, string? name)
    {
        id ??= "";
        if (SelectedProjectId == id) return;
        SelectedProjectId = id;
        SelectedProjectName = name;
        OnChange?.Invoke();
    }

    public void SetAttentionCount(int count)
    {
        if (AttentionCount == count) return;
        AttentionCount = count;
        OnChange?.Invoke();
    }
}

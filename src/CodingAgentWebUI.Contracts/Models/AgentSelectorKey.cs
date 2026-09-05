namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Canonical agent-selector key: labels sorted with <see cref="StringComparer.Ordinal"/> and joined
/// with a comma. Used as <see cref="WorkItemEntity.AgentSelector"/> / <see cref="JobDistributionRequest.AgentSelector"/>.
/// Extracted in T22 (arch-audit 2026-08-22) — two co-changing callers had identical logic;
/// divergence would fail silently rather than at compile time.
/// </summary>
public static class AgentSelectorKey
{
    /// <summary>
    /// Normalises <paramref name="labels"/> into the canonical comma-separated, ordinally-sorted
    /// string used for agent routing lookups. An empty or null sequence returns <see cref="string.Empty"/>.
    /// </summary>
    public static string From(IEnumerable<string>? labels)
    {
        if (labels is null)
            return string.Empty;
        return string.Join(",", labels.OrderBy(l => l, StringComparer.Ordinal));
    }
}

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Shared label matching logic used by <see cref="JobDispatcherService"/> and <see cref="ConsolidationQueueService"/>.
/// </summary>
internal static class LabelMatchHelper
{
    /// <summary>
    /// Returns <c>true</c> if the agent's labels are a superset of the required labels (case-insensitive).
    /// An empty required labels list matches any agent.
    /// </summary>
    internal static bool IsLabelMatch(IReadOnlyList<string> agentLabels, IReadOnlyList<string> requiredLabels)
    {
        if (requiredLabels.Count == 0)
            return true;

        var agentLabelSet = new HashSet<string>(agentLabels, StringComparer.OrdinalIgnoreCase);
        return requiredLabels.All(label => agentLabelSet.Contains(label));
    }
}

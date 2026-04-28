namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Narrow interface for issue operations needed by pipeline orchestrators.
/// Implemented by <c>IssueProviderAdapter</c> (wraps <see cref="IIssueProvider"/>) on the orchestrator
/// and by <c>OrchestratorProxy</c> (wraps SignalR hub calls) on the agent.
/// This abstraction enables reuse of <c>AgentExecutionOrchestrator</c> and <c>QualityGateOrchestrator</c>
/// in both deployment contexts without depending on <see cref="IIssueProvider"/> directly.
/// </summary>
public interface IAgentIssueOperations
{
    /// <summary>
    /// Posts a comment on the specified issue.
    /// </summary>
    Task PostCommentAsync(string issueIdentifier, string body, CancellationToken ct);

    /// <summary>
    /// Swaps the current agent label on the specified issue to <paramref name="newLabel"/>.
    /// Removes all existing agent labels before adding the new one.
    /// </summary>
    Task SwapLabelAsync(string issueIdentifier, string newLabel, CancellationToken ct);
}

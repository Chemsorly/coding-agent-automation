namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Downloads open issues and writes them as markdown files to the workspace
/// for agent deduplication context. Reusable across pipeline phases.
/// Accepts <see cref="IAgentIssueOperations"/> (proxied through orchestrator) rather than
/// IIssueProvider directly, keeping the agent credential-free.
/// </summary>
public interface IOpenIssueContextWriter
{
    /// <summary>
    /// Fetches open issues via the orchestrator proxy and writes each as a markdown file at
    /// {workspacePath}/.agent/open-issues/{identifier}.md with YAML front-matter.
    /// </summary>
    /// <param name="issueOps">The issue operations proxy (SignalR to orchestrator).</param>
    /// <param name="workspacePath">Absolute workspace path.</param>
    /// <param name="maxIssues">Maximum issues to download (default 50).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of issues successfully written.</returns>
    Task<int> WriteOpenIssueContextAsync(
        IAgentIssueOperations issueOps,
        string workspacePath,
        int maxIssues,
        CancellationToken ct);

    /// <summary>
    /// Fetches open issues and optionally closed sibling issues (for epic decomposition runs).
    /// Closed issues are included when <paramref name="includeClosedSiblings"/> is true,
    /// sharing the total <paramref name="maxIssues"/> budget with open issues.
    /// </summary>
    /// <param name="issueOps">The issue operations proxy (SignalR to orchestrator).</param>
    /// <param name="workspacePath">Absolute workspace path.</param>
    /// <param name="maxIssues">Maximum total issues to download (open + closed combined).</param>
    /// <param name="includeClosedSiblings">Whether to include recently-closed issues (epic runs).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of issues successfully written.</returns>
    Task<int> WriteOpenIssueContextAsync(
        IAgentIssueOperations issueOps,
        string workspacePath,
        int maxIssues,
        bool includeClosedSiblings,
        CancellationToken ct)
        => WriteOpenIssueContextAsync(issueOps, workspacePath, maxIssues, ct);
}

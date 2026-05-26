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
}

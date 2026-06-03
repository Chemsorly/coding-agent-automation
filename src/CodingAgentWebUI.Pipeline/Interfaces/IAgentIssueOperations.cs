using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Narrow interface for issue operations needed by pipeline orchestrators.
/// Implemented on the orchestrator side (wraps <see cref="IIssueProvider"/>)
/// and by <c>OrchestratorProxy</c> (wraps SignalR hub calls) on the agent.
/// This abstraction enables reuse of <c>AgentPhaseExecutor</c> and <c>QualityGateExecutor</c>
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

    // --- Decomposition-specific operations ---
    // All calls are proxied through SignalR to the orchestrator, which resolves
    // the IIssueProvider from the template config.

    /// <summary>
    /// Creates a new issue with the given title, body, and labels.
    /// Returns the created issue's identifier and URL.
    /// </summary>
    Task<CreatedIssueResult> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
        => throw new NotSupportedException("CreateIssueAsync is not implemented by this provider.");

    /// <summary>
    /// Creates a new issue via a specific issue provider (identified by config ID) for cross-repo routing.
    /// Used by <c>CreateSubIssuesStep</c> when a decomposed issue's <c>targetRepository</c> resolves
    /// to a different template's issue provider.
    /// Falls back to the default <see cref="CreateIssueAsync"/> behavior when not overridden.
    /// </summary>
    Task<CreatedIssueResult> CreateIssueForProviderAsync(
        string issueProviderConfigId, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
        => CreateIssueAsync(title, body, labels, ct);

    /// <summary>
    /// Lists open issues with optional label filtering. Returns paginated results.
    /// </summary>
    Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
        => throw new NotSupportedException("ListOpenIssuesAsync is not implemented by this provider.");

    /// <summary>
    /// Gets full issue details by identifier.
    /// </summary>
    Task<IssueDetail> GetIssueAsync(string identifier, CancellationToken ct)
        => throw new NotSupportedException("GetIssueAsync is not implemented by this provider.");

    /// <summary>
    /// Lists all comments on an issue.
    /// </summary>
    Task<IReadOnlyList<IssueComment>> ListCommentsAsync(string identifier, CancellationToken ct)
        => throw new NotSupportedException("ListCommentsAsync is not implemented by this provider.");

    /// <summary>
    /// Updates an existing comment by ID.
    /// </summary>
    Task UpdateCommentAsync(string issueIdentifier, string commentId, string body, CancellationToken ct)
        => throw new NotSupportedException("UpdateCommentAsync is not implemented by this provider.");
}

using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

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
    /// Posts a comment on the specified issue. Returns the comment URL if available.
    /// </summary>
    Task<string?> PostCommentAsync(IssueIdentifier issueIdentifier, string body, CancellationToken ct);

    /// <summary>
    /// Swaps the current agent label on the specified issue to <paramref name="newLabel"/>.
    /// Removes all existing agent labels before adding the new one.
    /// </summary>
    Task SwapLabelAsync(IssueIdentifier issueIdentifier, string newLabel, CancellationToken ct);

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
    Task<IssueDetail> GetIssueAsync(IssueIdentifier identifier, CancellationToken ct)
        => throw new NotSupportedException("GetIssueAsync is not implemented by this provider.");

    /// <summary>
    /// Lists all comments on an issue.
    /// </summary>
    Task<IReadOnlyList<IssueComment>> ListCommentsAsync(IssueIdentifier identifier, CancellationToken ct)
        => throw new NotSupportedException("ListCommentsAsync is not implemented by this provider.");

    /// <summary>
    /// Updates an existing comment by ID.
    /// </summary>
    Task UpdateCommentAsync(IssueIdentifier issueIdentifier, long commentId, string body, CancellationToken ct)
        => throw new NotSupportedException("UpdateCommentAsync is not implemented by this provider.");

    /// <summary>
    /// Lists closed issues with optional label filtering and date cutoff.
    /// Used by <see cref="CodingAgent.Pipeline.Services.Steps.WriteOpenIssueContextStep"/> to include
    /// recently-closed sibling issues in epic decomposition runs.
    /// </summary>
    // TODO: Default implementation throws NotSupportedException. Consider returning an empty
    // PagedResult instead (matching IIssueProvider pattern) for resilience with providers
    // that don't implement this method.
    Task<PagedResult<IssueSummary>> ListClosedIssuesAsync(int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since, CancellationToken ct)
        => throw new NotSupportedException("ListClosedIssuesAsync is not implemented by this provider.");

    /// <summary>
    /// Lists open issues via a specific issue provider (identified by config ID) for cross-repo routing.
    /// Used by <c>DecompositionStep</c> on reruns to read already-created sub-issues from template trackers.
    /// Falls back to <see cref="ListOpenIssuesAsync"/> (own tracker) when not overridden, which is safe
    /// for repo epics and non-decomposition contexts where <c>projectContext</c> is always null.
    /// </summary>
    // TODO: The default implementation silently ignores issueProviderConfigId and delegates to
    // ListOpenIssuesAsync (own tracker). Any IAgentIssueOperations implementor that does not
    // override this method will silently query the wrong tracker rather than failing fast.
    // Consider a throw new NotSupportedException(...) default consistent with ListClosedIssuesAsync
    // to surface misconfiguration early. Safe to change because DecompositionStep only calls
    // this via OrchestratorProxy, which always overrides it. See review finding: IAgentIssueOperations — silent fallback default.
    Task<PagedResult<IssueSummary>> ListOpenIssuesForProviderAsync(
        string issueProviderConfigId, int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
        => ListOpenIssuesAsync(page, pageSize, labels, ct);

    /// <summary>
    /// Lists closed issues via a specific issue provider (identified by config ID) for cross-repo routing.
    /// Falls back to <see cref="ListClosedIssuesAsync"/> (own tracker) when not overridden.
    /// </summary>
    // TODO: Same silent-fallback concern as ListOpenIssuesForProviderAsync above — issueProviderConfigId
    // is silently ignored by the default. See review finding: IAgentIssueOperations — silent fallback default.
    Task<PagedResult<IssueSummary>> ListClosedIssuesForProviderAsync(
        string issueProviderConfigId, int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since, CancellationToken ct)
        => ListClosedIssuesAsync(page, pageSize, labels, since, ct);

    /// <summary>
    /// Reports the result of the caller's triage run (JSON of a <c>TriageResult</c>) to the API, which records it
    /// on the run's triage. Only the agent-side proxy implements it.
    /// </summary>
    Task ReportTriageResultAsync(string resultJson, CancellationToken ct)
        => throw new NotSupportedException("ReportTriageResultAsync is not implemented by this provider");
}

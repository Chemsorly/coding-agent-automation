using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public enum RepositoryProviderType { GitHub, GitLab }

/// <summary>
/// Full repository provider abstraction. Combines all slice interfaces into a single
/// injectable type that both concrete implementations satisfy.
/// <para>
/// Callers that need only a narrow surface should inject the appropriate slice instead:
/// <list type="bullet">
///   <item><see cref="IGitWorkspaceProvider"/> — clone, branch, commit, push, diff, validate</item>
///   <item><see cref="IPullRequestProvider"/> — create, update, query, and list PRs</item>
///   <item><see cref="IPullRequestHousekeepingProvider"/> — mergeability polling, server-side updates, branch cleanup</item>
///   <item><see cref="IPullRequestReviewProvider"/> — submit, dismiss, and update review comments</item>
///   <item><see cref="IPullRequestLabelProvider"/> — add/remove/ensure PR labels</item>
///   <item><see cref="IRepositoryAnalyticsProvider"/> — commit-count staleness detection</item>
/// </list>
/// </para>
/// </summary>
public interface IRepositoryProvider
    : IGitWorkspaceProvider,
      IPullRequestProvider,
      IPullRequestHousekeepingProvider,
      IPullRequestReviewProvider,
      IPullRequestLabelProvider,
      IRepositoryAnalyticsProvider
{
    /// <summary>Backward-compatible overload with no blacklist.</summary>
    Task CommitAllAsync(WorkspacePath workspacePath, string message, CancellationToken ct) =>
        CommitAllAsync(workspacePath, message, null, ct);
}

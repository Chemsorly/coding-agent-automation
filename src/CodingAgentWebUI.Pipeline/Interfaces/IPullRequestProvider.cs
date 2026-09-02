using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Pull request lifecycle operations: create, update, query, and list PRs.
/// Consumed by steps and services that manage pull request state
/// (PR orchestration, rework detection, drawer UI, finalization).
/// </summary>
public interface IPullRequestProvider : IAsyncDisposable
{
    Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct);

    /// <summary>
    /// Updates the body of an existing pull request and optionally changes its draft state.
    /// <para>
    /// <paramref name="markReady"/> semantics:
    /// <list type="bullet">
    ///   <item><description><c>true</c>  — promote to ready-for-review (un-draft)</description></item>
    ///   <item><description><c>false</c> — convert to draft</description></item>
    ///   <item><description><c>null</c>  — body-only update; draft state is left unchanged</description></item>
    /// </list>
    /// </para>
    /// Default throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task UpdatePullRequestAsync(int pullRequestNumber, string body, bool? markReady, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support UpdatePullRequestAsync.");

    /// <summary>
    /// Fetches the current body of a pull request. Returns null if unsupported.
    /// Used to avoid stale-state overwrites when appending to PR bodies.
    /// </summary>
    Task<string?> GetPullRequestBodyAsync(int pullRequestNumber, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Searches for open pull requests whose branch name matches the agent branch pattern
    /// for the given issue. Returns metadata including draft state, mergeable state, and review comments.
    /// </summary>
    Task<IReadOnlyList<LinkedPullRequest>> GetAgentPullRequestsAsync(
        IssueIdentifier issueIdentifier, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<LinkedPullRequest>>(Array.Empty<LinkedPullRequest>());

    /// <summary>Closes an open pull request/merge request by number. Default is a no-op.</summary>
    Task ClosePullRequestAsync(int pullRequestNumber, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Lists open pull requests with optional label filtering.
    /// When labels is null or empty, returns all open PRs.
    /// Default throws <see cref="NotSupportedException"/>.
    /// </summary>
    Task<PagedResult<PullRequestSummary>> ListOpenPullRequestsAsync(
        int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support ListOpenPullRequestsAsync.");
}

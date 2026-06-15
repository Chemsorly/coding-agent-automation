using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public enum RepositoryProviderType { GitHub, GitLab }

public interface IRepositoryProvider : IAsyncDisposable
{
    RepositoryProviderType ProviderType { get; }

    /// <summary>The base branch name (e.g. "main") configured for this repository.</summary>
    string BaseBranch { get; }

    /// <summary>The full repository name in "owner/repo" format.</summary>
    string RepositoryFullName { get; }

    Task CloneAsync(string workspacePath, CancellationToken ct);
    Task<string> CreateBranchAsync(string workspacePath, string branchName, CancellationToken ct);
    /// <summary>
    /// Stages all changes, unstages any files matching <paramref name="blacklistedPaths"/>
    /// and hardcoded pipeline paths, then commits the remaining staged files.
    /// Returns the list of file paths that were unstaged due to blacklist matches.
    /// </summary>
    Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null);

    /// <summary>
    /// Stages all changes, unstages blacklisted paths, and commits.
    /// When <paramref name="allowEmpty"/> is true, creates an empty commit if no files changed
    /// (useful for triggering CI re-runs after retry fixes that didn't change files).
    /// </summary>
    Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null);

    /// <summary>Backward-compatible overload with no blacklist.</summary>
    Task CommitAllAsync(string workspacePath, string message, CancellationToken ct) =>
        CommitAllAsync(workspacePath, message, null, ct);
    Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct);

    /// <summary>
    /// Pushes the branch to origin with optional force-push (required after rebase rewrites history).
    /// </summary>
    Task PushBranchAsync(string workspacePath, string branchName, bool forcePush, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, ct);

    Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct);

    /// <summary>
    /// Returns the SHA of the HEAD commit in the given workspace repository.
    /// </summary>
    Task<string> GetHeadCommitShaAsync(string workspacePath, CancellationToken ct);

    /// <summary>
    /// Checks whether the current branch has any commits ahead of the base branch.
    /// Returns false if the branch tip equals the merge base (no new commits).
    /// </summary>
    Task<bool> HasCommitsAheadAsync(string workspacePath, CancellationToken ct);

    /// <summary>
    /// Builds a list of file changes by comparing the current branch HEAD against the base branch.
    /// If the committed diff is empty, falls back to comparing the base branch against the
    /// working directory to capture uncommitted changes.
    /// Returns an empty list if the diff cannot be computed.
    /// </summary>
    Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(string workspacePath, CancellationToken ct);

    /// <summary>
    /// Validates that the provider is correctly configured and can communicate with its
    /// backing service. Called at pipeline start before any work begins.
    /// </summary>
    Task ValidateAsync(CancellationToken ct);

    /// <summary>
    /// Pulls latest changes into an existing clone at the given workspace path.
    /// The existing code repository workflow clones fresh each run and does not use pull,
    /// so the default implementation throws NotSupportedException.
    /// </summary>
    Task PullAsync(string workspacePath, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support PullAsync. " +
            "Override this method to enable pull operations.");

    /// <summary>
    /// Searches for open pull requests whose branch name matches the agent branch pattern
    /// for the given issue. Returns metadata including draft state, mergeable state, and review comments.
    /// </summary>
    Task<IReadOnlyList<LinkedPullRequest>> GetAgentPullRequestsAsync(
        string issueIdentifier, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<LinkedPullRequest>>(Array.Empty<LinkedPullRequest>());

    /// <summary>
    /// Closes an open pull request/merge request by number.
    /// Default is a no-op for providers that don't support it.
    /// </summary>
    Task ClosePullRequestAsync(int pullRequestNumber, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Checks out an existing remote branch after clone, creating a local tracking branch.
    /// </summary>
    Task CheckoutRemoteBranchAsync(string workspacePath, string branchName, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support CheckoutRemoteBranchAsync.");

    /// <summary>
    /// Rebases the current branch onto the latest base branch (fetches first to ensure freshness).
    /// If conflicts occur, aborts the rebase and returns the list of conflicting files.
    /// </summary>
    Task<MergeResult> MergeFromBaseAsync(string workspacePath, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support MergeFromBaseAsync.");

    /// <summary>
    /// Updates the body of an existing pull request and optionally marks it as ready for review.
    /// </summary>
    Task UpdatePullRequestAsync(int pullRequestNumber, string body, bool markReady, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support UpdatePullRequestAsync.");

    /// <summary>
    /// Lists open pull requests with optional label filtering.
    /// When labels is null or empty, returns all open PRs.
    /// </summary>
    Task<PagedResult<PullRequestSummary>> ListOpenPullRequestsAsync(
        int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support ListOpenPullRequestsAsync.");

    /// <summary>Adds a label to a pull request.</summary>
    Task AddPrLabelAsync(int prNumber, string label, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support AddPrLabelAsync.");

    /// <summary>Removes a label from a pull request.</summary>
    Task RemovePrLabelAsync(int prNumber, string label, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support RemovePrLabelAsync.");

    /// <summary>
    /// Ensures the agent status labels exist for pull requests. Creates any that are missing.
    /// On GitHub this is a no-op (PRs share labels with issues).
    /// </summary>
    Task<bool> EnsureAgentLabelsForPullRequestsAsync(CancellationToken ct)
        => Task.FromResult(true);

    /// <summary>
    /// Submits a review on a pull request using the platform's native review API.
    /// Falls back to a regular comment for providers that lack native review support.
    /// </summary>
    // TODO: The default throws NotSupportedException, which PostReviewFindingsStep catches gracefully
    // (the run completes without posting findings). Per the spec, unsupported providers should fall back
    // to posting a regular comment on the PR so findings are still visible. The challenge is that this
    // interface method can't call another interface method in its default implementation (no access to
    // a comment-posting API here). Options:
    //   (a) Require all providers to implement this method (remove the default, add to each provider).
    //   (b) Move the fallback logic into PostReviewFindingsStep: catch NotSupportedException, then call
    //       a separate PostCommentAsync method on IIssueProvider or IRepositoryProvider.
    //   (c) Add a SupportsNativeReviews property so PostReviewFindingsStep can choose the right path.
    // Currently the GitHub provider uses issue comments (not the Reviews API), so this only matters
    // when adding non-GitHub providers (GitLab, Bitbucket, etc.).
    Task SubmitPullRequestReviewAsync(
        int prNumber, string body, PullRequestReviewType type, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support SubmitPullRequestReviewAsync.");

    /// <summary>
    /// Extracts linked issue references from a pull request.
    /// Provider-dependent: uses platform API, then falls back to title/body parsing.
    /// Returns issue identifiers (e.g., "42", "PROJ-123").
    /// </summary>
    Task<IReadOnlyList<string>> ExtractLinkedIssuesAsync(
        int prNumber, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>
    /// Lists all comments on a pull request/merge request, including discussion comments
    /// and review thread comments, for building PR conversation context.
    /// Returns comments in chronological order with author attribution.
    /// </summary>
    /// <param name="prNumber">The PR/MR number.</param>
    /// <param name="prAuthor">The PR author username, used to flag author comments.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<PrConversationComment>> ListPullRequestCommentsAsync(
        int prNumber, string prAuthor, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PrConversationComment>>(Array.Empty<PrConversationComment>());

    /// <summary>
    /// Whether this provider's platform supports native inline review comments
    /// attached to specific file and line positions in the diff.
    /// Default: false (conservative for providers that have not opted in).
    /// </summary>
    bool SupportsInlineReviewComments => false;

    /// <summary>
    /// Submits a review with optional inline comments. When <see cref="ReviewSubmission.Comments"/>
    /// is empty, produces the same result as the body-only overload.
    /// Default implementation falls back to the existing body-only overload.
    /// </summary>
    Task SubmitPullRequestReviewAsync(int prNumber, ReviewSubmission submission, CancellationToken ct)
        => SubmitPullRequestReviewAsync(prNumber, submission.Body, submission.Type, ct);

    /// <summary>
    /// Finds and dismisses/resolves previous automated reviews identified by the marker string.
    /// Default implementation is a no-op (returns <see cref="Task.CompletedTask"/>).
    /// </summary>
    Task DismissPreviousReviewAsync(int prNumber, string marker, string reason, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Searches for an existing review comment containing the specified marker text.
    /// Returns the comment ID if found, null otherwise.
    /// </summary>
    Task<long?> FindExistingReviewCommentAsync(int prNumber, string marker, CancellationToken ct)
        => Task.FromResult<long?>(null);

    /// <summary>
    /// Updates an existing review comment body by its ID.
    /// </summary>
    Task UpdateReviewCommentAsync(int prNumber, long commentId, string body, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support UpdateReviewCommentAsync.");

    /// <summary>
    /// Formats a "close" reference for an issue identifier, using the repository host's keyword syntax.
    /// Default: <c>Closes #{issueIdentifier}</c> (GitHub/GitLab).
    /// Returns null when cross-platform auto-close is not supported (e.g., GitHub repo + Jira issues).
    /// </summary>
    string? FormatCloseReference(string issueIdentifier) => $"Closes #{issueIdentifier}";
}

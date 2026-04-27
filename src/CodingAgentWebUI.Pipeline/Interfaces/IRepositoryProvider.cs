using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public enum RepositoryProviderType { GitHub }

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
    /// Stages all changes, unstages any files matching <paramref name="blacklistedPaths"/>,
    /// then commits the remaining staged files.
    /// Returns the list of file paths that were unstaged due to blacklist matches.
    /// </summary>
    Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct);

    /// <summary>
    /// Stages all changes, unstages blacklisted paths, and commits.
    /// When <paramref name="allowEmpty"/> is true, creates an empty commit if no files changed
    /// (useful for triggering CI re-runs after retry fixes that didn't change files).
    /// </summary>
    Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct);

    /// <summary>Backward-compatible overload with no blacklist.</summary>
    Task CommitAllAsync(string workspacePath, string message, CancellationToken ct) =>
        CommitAllAsync(workspacePath, message, null, ct);
    Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct);
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
    /// Checks out an existing remote branch after clone, creating a local tracking branch.
    /// </summary>
    Task CheckoutRemoteBranchAsync(string workspacePath, string branchName, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support CheckoutRemoteBranchAsync.");

    /// <summary>
    /// Merges the base branch into the current branch. If conflicts occur, leaves conflict
    /// markers in the working tree and returns the list of conflicting files.
    /// </summary>
    Task<MergeResult> MergeFromBaseAsync(string workspacePath, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support MergeFromBaseAsync.");

    /// <summary>
    /// Updates the body of an existing pull request, preserving title and other metadata.
    /// </summary>
    Task UpdatePullRequestAsync(int pullRequestNumber, string body, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support UpdatePullRequestAsync.");
}

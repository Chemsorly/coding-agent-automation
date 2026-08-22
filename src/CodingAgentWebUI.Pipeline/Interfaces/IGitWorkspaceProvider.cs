using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Git and workspace operations: clone, branch, commit, push, diff, validate.
/// Consumed by steps that operate on a local workspace (clone, branch creation,
/// commit/push, quality-gate CI, PR orchestration, brain sync).
/// </summary>
public interface IGitWorkspaceProvider : IAsyncDisposable
{
    /// <summary>The repository provider type (GitHub, GitLab, etc.).</summary>
    RepositoryProviderType ProviderType { get; }

    /// <summary>The base branch name (e.g. "main") configured for this repository.</summary>
    string BaseBranch { get; }

    /// <summary>The full repository name in "owner/repo" format.</summary>
    string RepositoryFullName { get; }

    Task CloneAsync(WorkspacePath workspacePath, CancellationToken ct);

    Task<string> CreateBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct);

    /// <summary>
    /// Stages all changes, unstages any files matching <paramref name="blacklistedPaths"/>
    /// and hardcoded pipeline paths, then commits the remaining staged files.
    /// Returns the list of file paths that were unstaged due to blacklist matches.
    /// </summary>
    Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null);

    /// <summary>
    /// Stages all changes, unstages blacklisted paths, and commits.
    /// When <paramref name="allowEmpty"/> is true, creates an empty commit if no files changed.
    /// </summary>
    Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null);

    Task PushBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct);

    /// <summary>Pushes the branch with optional force-push (required after rebase rewrites history).</summary>
    Task PushBranchAsync(WorkspacePath workspacePath, string branchName, bool forcePush, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, ct);

    /// <summary>Returns the SHA of the HEAD commit in the given workspace repository.</summary>
    Task<string> GetHeadCommitShaAsync(WorkspacePath workspacePath, CancellationToken ct);

    /// <summary>Checks whether the current branch has any commits ahead of the base branch.</summary>
    Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, CancellationToken ct);

    /// <summary>
    /// Builds a list of file changes by comparing the current branch HEAD against the base branch.
    /// Falls back to uncommitted changes if the diff is empty. Returns empty if diff cannot be computed.
    /// </summary>
    Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, CancellationToken ct);

    /// <summary>
    /// Validates that the provider is correctly configured and can communicate with its backing service.
    /// Called at pipeline start before any work begins.
    /// </summary>
    Task ValidateAsync(CancellationToken ct);

    /// <summary>Pulls latest changes into an existing clone. Default throws <see cref="NotSupportedException"/>.</summary>
    Task PullAsync(WorkspacePath workspacePath, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support PullAsync. Override to enable pull operations.");

    /// <summary>Checks out an existing remote branch after clone, creating a local tracking branch.</summary>
    Task CheckoutRemoteBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support CheckoutRemoteBranchAsync.");

    /// <summary>
    /// Rebases the current branch onto the latest base branch. If conflicts occur, aborts and returns
    /// the list of conflicting files.
    /// </summary>
    Task<MergeResult> MergeFromBaseAsync(WorkspacePath workspacePath, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support MergeFromBaseAsync.");

    /// <summary>
    /// Formats a close reference for an issue using the host's keyword syntax.
    /// Default: <c>Closes #{issueIdentifier}</c>. Returns null for cross-platform scenarios where
    /// auto-close is not supported.
    /// </summary>
    string? FormatCloseReference(IssueIdentifier issueIdentifier) => $"Closes #{issueIdentifier}";
}

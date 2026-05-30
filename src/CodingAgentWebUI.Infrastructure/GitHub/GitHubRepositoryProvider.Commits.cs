using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.GitHub;

public partial class GitHubRepositoryProvider
{
    public Task CommitAllAsync(string workspacePath, string message, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, null, ct);

    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, blacklistedPaths, allowEmpty: false, ct);

    /// <summary>
    /// Stages all changes, unstages blacklisted paths, and commits.
    /// </summary>
    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(message);

        return Task.Run(() => RepositoryGitOperations.CommitAll(workspacePath, message, blacklistedPaths, allowEmpty), ct);
    }

    public Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

    public Task PushBranchAsync(string workspacePath, string branchName, bool forcePush, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);
            await RepositoryGitOperations.Push(workspacePath, branchName, forcePush, GitConstants.TokenUsername, token, _gitPipeline, ct);
        }, ct);
    }

    /// <inheritdoc />
    public Task<string> GetHeadCommitShaAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(() => RepositoryGitOperations.GetHeadCommitSha(workspacePath), ct);
    }

    /// <inheritdoc />
    public async Task<bool> HasCommitsAheadAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return await RepositoryGitOperations.HasCommitsAhead(workspacePath, _baseBranch, _gitPipeline, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(() => RepositoryGitOperations.GetFileChanges(workspacePath, _baseBranch), ct);
    }
}

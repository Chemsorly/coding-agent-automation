using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.GitHub;

public partial class GitHubRepositoryProvider
{
    public Task CommitAllAsync(WorkspacePath workspacePath, string message, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, null, ct);

    public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null)
        => CommitAllAsync(workspacePath, message, blacklistedPaths, allowEmpty: false, ct, pipelineInjectedPaths);

    /// <summary>
    /// Stages all changes, unstages blacklisted paths, and commits.
    /// </summary>
    public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentNullException.ThrowIfNull(message);

        return Task.Run(() => RepositoryGitOperations.CommitAll(workspacePath, message, blacklistedPaths, allowEmpty, pipelineInjectedPaths), ct);
    }

    public Task PushBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

    public Task PushBranchAsync(WorkspacePath workspacePath, string branchName, bool forcePush, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentNullException.ThrowIfNull(branchName);

        // Pass a token factory rather than a pre-fetched string so each Polly retry attempt
        // can obtain a fresh GitHub App installation token. Tokens expire after 1 hour; long
        // pipeline runs (>1h) would otherwise fail on the first push retry with a stale 403.
        return Task.Run(() =>
            RepositoryGitOperations.Push(workspacePath, branchName, forcePush,
                GitConstants.TokenUsername, tokenFactory: GetTokenAsync, _gitPipeline, ct), ct);
    }

    /// <inheritdoc />
    public Task<string> GetHeadCommitShaAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

        return Task.Run(() => RepositoryGitOperations.GetHeadCommitSha(workspacePath), ct);
    }

    /// <inheritdoc />
    public async Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

        return await RepositoryGitOperations.HasCommitsAhead(workspacePath, _baseBranch, _gitPipeline, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

        return Task.Run(() => RepositoryGitOperations.GetFileChanges(workspacePath, _baseBranch), ct);
    }

    /// <inheritdoc />
    public async Task<int> GetCommitCountSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        var request = new Octokit.CommitRequest { Since = since };
        // TODO: Unbounded pagination may consume significant rate limit budget and memory on high-velocity repos.
        // Consider setting PageCount = 10 (matching the 1000-commit config cap) or implementing manual pagination
        // with early exit once the count exceeds the configured AnalysisCommitThreshold.
        var options = new Octokit.ApiOptions { PageSize = 100 };
        var commits = await ExecuteWithResilienceAsync(
            client => client.Repository.Commit.GetAll(Owner, Repo, request, options),
            "GetCommitCountSince", ct);
        return commits.Count;
    }
}

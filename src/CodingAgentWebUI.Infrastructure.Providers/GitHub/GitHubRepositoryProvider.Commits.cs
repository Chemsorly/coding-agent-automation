using System.Diagnostics.CodeAnalysis;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.GitHub;

public partial class GitHubRepositoryProvider
{
    [ExcludeFromCodeCoverage]
    public Task CommitAllAsync(WorkspacePath workspacePath, string message, CancellationToken ct)
        => SharedRepositoryOperations.CommitAllAsync(workspacePath, message, ct);

    [ExcludeFromCodeCoverage]
    public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null)
        => SharedRepositoryOperations.CommitAllAsync(workspacePath, message, blacklistedPaths, ct, pipelineInjectedPaths);

    /// <summary>
    /// Stages all changes, unstages blacklisted paths, and commits.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null)
        => SharedRepositoryOperations.CommitAllAsync(workspacePath, message, blacklistedPaths, allowEmpty, ct, pipelineInjectedPaths);

    // Requires a live git remote — not unit-testable; core retry logic covered via PushWithTokenFactory tests.
    [ExcludeFromCodeCoverage]
    public Task PushBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

    // Token factory passed so each Polly retry fetches a fresh GitHub App installation token
    // (tokens expire after 1h; long pipeline runs exceed that window).
    [ExcludeFromCodeCoverage]
    public Task PushBranchAsync(WorkspacePath workspacePath, string branchName, bool forcePush, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() =>
            RepositoryGitOperations.Push(workspacePath, branchName, forcePush,
                GitConstants.TokenUsername, tokenFactory: GetTokenAsync, _gitPipeline, ct), ct);
    }

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public Task<string> GetHeadCommitShaAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.GetHeadCommitShaAsync(workspacePath, ct);

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.HasCommitsAheadAsync(workspacePath, _baseBranch, _gitPipeline, ct);

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.GetFileChangesAsync(workspacePath, _baseBranch, ct);

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

using System.Diagnostics.CodeAnalysis;
using System.Net;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Models;
using Octokit;

namespace CodingAgent.Infrastructure.GitHub;

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
    public Task PushBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

    // Token factory passed so each Polly retry fetches a fresh GitHub App installation token
    // (tokens expire after 1h; long pipeline runs exceed that window).
    [ExcludeFromCodeCoverage]
    public Task PushBranchAsync(WorkspacePath workspacePath, BranchName branchName, bool forcePush, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentException.ThrowIfNullOrEmpty(branchName.Value);

        return Task.Run(() =>
            RepositoryGitOperations.Push(workspacePath, CanonicalCloneUrl, branchName, forcePush,
                GitConstants.TokenUsername, tokenFactory: GetTokenAsync, _gitPipeline, ct), ct);
    }

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public Task<string> GetHeadCommitShaAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.GetHeadCommitShaAsync(workspacePath, ct);

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public Task<IReadOnlyDictionary<string, string>?> GetWorkingTreeFingerprintAsync(
        WorkspacePath workspacePath, IReadOnlyList<string> excludedPaths, CancellationToken ct)
        => SharedRepositoryOperations.GetWorkingTreeFingerprintAsync(workspacePath, excludedPaths, ct);

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.HasCommitsAheadAsync(workspacePath, _baseBranch, _gitPipeline, ct);

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.GetFileChangesAsync(workspacePath, _baseBranch, ct);

    /// <inheritdoc />
    public async Task<RemoteFileWriteResult> WriteFileToBaseBranchAsync(
        string relativePath, string content, string commitMessage, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(commitMessage);

        var identity = new Committer(GitConstants.CommitAuthorName, GitConstants.CommitAuthorEmail, DateTimeOffset.UtcNow);
        try
        {
            var existingSha = await GetFileShaOnBaseBranchAsync(relativePath, ct);
            var changeSet = await ExecuteWithResilienceAsync(
                client => existingSha is null
                    ? client.Repository.Content.CreateFile(Owner, Repo, relativePath,
                        new CreateFileRequest(commitMessage, content, BaseBranch) { Committer = identity, Author = identity })
                    : client.Repository.Content.UpdateFile(Owner, Repo, relativePath,
                        new UpdateFileRequest(commitMessage, content, existingSha, BaseBranch) { Committer = identity, Author = identity }),
                "WriteFileToBaseBranch", ct);
            return new RemoteFileWriteResult(changeSet.Commit.Sha);
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            // 409: the sha or the branch moved. 422: the file appeared since the read, so a create lacks its sha.
            throw new RemoteFileConflictException($"{relativePath} changed on {BaseBranch} during the write.", ex);
        }
    }

    private async Task<string?> GetFileShaOnBaseBranchAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            var contents = await ExecuteWithResilienceAsync(
                client => client.Repository.Content.GetAllContentsByRef(Owner, Repo, relativePath, BaseBranch),
                "GetFileOnBaseBranch", ct);
            return contents.Count == 1 ? contents[0].Sha : null;
        }
        catch (Octokit.NotFoundException)
        {
            return null;
        }
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

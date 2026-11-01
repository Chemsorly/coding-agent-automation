using LibGit2Sharp;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using NGitLab;
using NGitLab.Models;
using Polly;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Serilog;
using MergeResult = CodingAgent.Pipeline.Models.MergeResult;

namespace CodingAgent.Infrastructure.GitLab;

/// <summary>
/// GitLab implementation of <see cref="IRepositoryProvider"/> for Git operations.
/// Uses LibGit2Sharp for local git operations and NGitLab for merge request management.
/// This is a partial class — merge request and review operations are in a separate file (task 6.2).
/// </summary>
public partial class GitLabRepositoryProvider : GitLabProviderBase, IRepositoryProvider
{
    private readonly BranchName _baseBranch;
    private readonly ResiliencePipeline _gitPipeline;

    /// <inheritdoc />
    public RepositoryProviderType ProviderType => RepositoryProviderType.GitLab;

    /// <inheritdoc />
    public string BaseBranch => _baseBranch;

    /// <inheritdoc />
    public string RepositoryFullName => PathWithNamespace ?? $"project/{ProjectId}";

    /// <inheritdoc />
    public bool SupportsInlineReviewComments => true;

    /// <inheritdoc />
    public bool SupportsServerSideBranchUpdate => true;

    /// <summary>
    /// Creates a provider with a static access token.
    /// </summary>
    public GitLabRepositoryProvider(string apiUrl, string accessToken, int projectId, BranchName baseBranch)
        : base(apiUrl, accessToken, projectId)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseBranch.Value);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Creates a provider with a dynamic token provider delegate (for OrchestratorProxy token refresh).
    /// </summary>
    public GitLabRepositoryProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, int projectId, BranchName baseBranch)
        : base(apiUrl, tokenProvider, projectId)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseBranch.Value);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitLabClient.
    /// </summary>
    internal GitLabRepositoryProvider(IGitLabClient client, int projectId, BranchName baseBranch)
        : base(client, projectId)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseBranch.Value);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    // ─── Git Operations ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    // Requires a live git remote and network access — not unit-testable.
    [ExcludeFromCodeCoverage]
    public Task CloneAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);
            var cloneUrl = await GetCanonicalCloneUrlAsync(ct);

            try
            {
                await RepositoryGitOperations.Clone(workspacePath, cloneUrl, _baseBranch, GitConstants.GitLabTokenUsername, token, _gitPipeline, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw RedactTokenFromException(ex, token);
            }
        }, ct);
    }

    /// <inheritdoc />
    // Requires a live git remote and network access — not unit-testable.
    [ExcludeFromCodeCoverage]
    public Task PullAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

        return Task.Run(async () =>
        {
            var canonicalCloneUrl = await GetCanonicalCloneUrlAsync(ct);
            var token = await GetTokenAsync(ct);

            try
            {
                await RepositoryGitOperations.Pull(workspacePath, canonicalCloneUrl, _baseBranch, GitConstants.GitLabTokenUsername, token, _gitPipeline, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw RedactTokenFromException(ex, token);
            }
        }, ct);
    }

    /// <inheritdoc />
    // Requires a live git repository on disk — not unit-testable without filesystem scaffolding.
    [ExcludeFromCodeCoverage]
    public Task<string> CreateBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentException.ThrowIfNullOrEmpty(branchName.Value);

        return Task.Run(() => RepositoryGitOperations.CreateBranch(workspacePath, branchName), ct);
    }

    /// <inheritdoc />
    // Requires a live git repository on disk — not unit-testable without filesystem scaffolding.
    [ExcludeFromCodeCoverage]
    public Task CheckoutRemoteBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentException.ThrowIfNullOrEmpty(branchName.Value);

        return Task.Run(() => RepositoryGitOperations.CheckoutRemoteBranch(workspacePath, branchName), ct);
    }

    /// <inheritdoc />
    public Task CommitAllAsync(WorkspacePath workspacePath, string message, CancellationToken ct)
        => SharedRepositoryOperations.CommitAllAsync(workspacePath, message, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null)
        => SharedRepositoryOperations.CommitAllAsync(workspacePath, message, blacklistedPaths, ct, pipelineInjectedPaths);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null)
        => SharedRepositoryOperations.CommitAllAsync(workspacePath, message, blacklistedPaths, allowEmpty, ct, pipelineInjectedPaths);

    /// <inheritdoc />
    // Requires a live git remote — not unit-testable; core retry logic covered via PushWithTokenFactory tests.
    [ExcludeFromCodeCoverage]
    public Task PushBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

    /// <inheritdoc />
    // Token factory passed so each Polly retry fetches a fresh token (GitLab tokens can also
    // be dynamically vended; stale tokens cause 403s in long pipeline runs).
    [ExcludeFromCodeCoverage]
    public Task PushBranchAsync(WorkspacePath workspacePath, BranchName branchName, bool forcePush, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentException.ThrowIfNullOrEmpty(branchName.Value);

        return Task.Run(async () =>
        {
            var canonicalCloneUrl = await GetCanonicalCloneUrlAsync(ct);
            try
            {
                await RepositoryGitOperations.Push(workspacePath, canonicalCloneUrl, branchName, forcePush,
                    GitConstants.GitLabTokenUsername, tokenFactory: GetTokenAsync, _gitPipeline, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            {
                // Redact any token value that may have leaked into the exception message.
                var token = await GetTokenAsync(ct);
                throw RedactTokenFromException(ex, token);
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<string> GetHeadCommitShaAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.GetHeadCommitShaAsync(workspacePath, ct);

    public Task<IReadOnlyDictionary<string, string>?> GetWorkingTreeFingerprintAsync(
        WorkspacePath workspacePath, IReadOnlyList<string> excludedPaths, CancellationToken ct)
        => SharedRepositoryOperations.GetWorkingTreeFingerprintAsync(workspacePath, excludedPaths, ct);

    /// <inheritdoc />
    public string? GetCommitWebUrl(string commitSha)
    {
        if (string.IsNullOrEmpty(HttpUrlToRepo) || string.IsNullOrEmpty(commitSha))
            return null;
        var webUrl = HttpUrlToRepo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? HttpUrlToRepo[..^".git".Length]
            : HttpUrlToRepo;
        return $"{webUrl}/-/commit/{commitSha}";
    }

    /// <inheritdoc />
    // The Commits API, not the Repository Files API: only it returns the commit and takes an author.
    public async Task<RemoteFileWriteResult> WriteFileToBaseBranchAsync(
        string relativePath, string content, string commitMessage, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(commitMessage);

        try
        {
            var exists = await ExecuteWithResilienceAsync(
                client => client.GetRepository(ProjectId).Files.FileExistsAsync(relativePath, BaseBranch, ct),
                "FileExistsOnBaseBranch", ct);
            var commit = await ExecuteWriteWithResilienceAsync(
                client => client.GetCommits(ProjectId).Create(new CommitCreate
                {
                    Branch = BaseBranch,
                    CommitMessage = commitMessage,
                    AuthorName = GitConstants.CommitAuthorName,
                    AuthorEmail = GitConstants.CommitAuthorEmail,
                    Actions =
                    [
                        new CreateCommitAction
                        {
                            Action = exists ? "update" : "create",
                            FilePath = relativePath,
                            Content = content,
                            Encoding = "text"
                        }
                    ]
                }),
                "WriteFileToBaseBranch", ct);
            return new RemoteFileWriteResult(commit.Id.ToString());
        }
        catch (GitLabException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            // 400: "A file with this name already exists" or "doesn't exist", when another write came between.
            throw new RemoteFileConflictException($"{relativePath} changed on {BaseBranch} during the write.", ex);
        }
    }

    /// <inheritdoc />
    public Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.HasCommitsAheadAsync(workspacePath, _baseBranch, _gitPipeline, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.GetFileChangesAsync(workspacePath, _baseBranch, ct);

    /// <inheritdoc />
    // Requires a live git remote and network access — not unit-testable.
    [ExcludeFromCodeCoverage]
    public Task<MergeResult> MergeFromBaseAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

        return Task.Run(async () =>
        {
            var canonicalCloneUrl = await GetCanonicalCloneUrlAsync(ct);
            var token = await GetTokenAsync(ct);
            return await RepositoryGitOperations.MergeFromBase(workspacePath, canonicalCloneUrl, _baseBranch, GitConstants.GitLabTokenUsername, token, _gitPipeline, ct);
        }, ct);
    }

    /// <inheritdoc />
    public Task<bool> EnsureAgentLabelsForPullRequestsAsync(CancellationToken ct)
    {
        // GitLab labels are project-scoped — issues and MRs share the same namespace.
        // No additional label creation needed for MRs.
        return Task.FromResult(true);
    }

    // ─── MR/Review Operations (implemented in GitLabRepositoryProvider.MergeRequests.cs) ───

    // ─── Private Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// This project's clone URL, from the GitLab API's project metadata, loaded here when validation has not run.
    /// Never read from a workspace.
    /// </summary>
    internal async Task<string> GetCanonicalCloneUrlAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(HttpUrlToRepo))
            await ValidateAsync(ct);

        if (string.IsNullOrEmpty(HttpUrlToRepo))
            throw new InvalidOperationException($"GitLab project {ProjectId} reported no clone URL.");

        return ToCanonicalCloneUrl(HttpUrlToRepo);
    }

    /// <summary>
    /// <paramref name="httpUrl"/> as HTTPS with its port and without credentials. GitLab can report <c>http://</c>
    /// behind a TLS proxy, and the token must not go over plain HTTP.
    /// </summary>
    internal static string ToCanonicalCloneUrl(string httpUrl)
    {
        var uri = new Uri(httpUrl);
        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"Clone URL has unsupported scheme '{uri.Scheme}'. Only HTTPS is supported for authenticated clone.");

        return $"https://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{uri.AbsolutePath}";
    }

    /// <summary>
    /// Redacts the access token from an exception message to prevent token leakage via logs or error handlers.
    /// Returns a new exception of the same type with the token replaced by "[REDACTED]".
    /// </summary>
    [ExcludeFromCodeCoverage]
    private static Exception RedactTokenFromException(Exception ex, string token)
    {
        var message = ex.Message;
        if (message.Contains(token, StringComparison.Ordinal))
        {
            message = message.Replace(token, "[REDACTED]", StringComparison.Ordinal);
        }

        return ex switch
        {
            LibGit2SharpException => new LibGit2SharpException(message, ex),
            _ => new InvalidOperationException(message, ex)
        };
    }
}

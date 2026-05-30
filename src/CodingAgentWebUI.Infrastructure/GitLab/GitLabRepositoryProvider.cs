using LibGit2Sharp;
using NGitLab;
using Polly;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using Repository = LibGit2Sharp.Repository;
using MergeResult = CodingAgentWebUI.Pipeline.Models.MergeResult;

namespace CodingAgentWebUI.Infrastructure.GitLab;

/// <summary>
/// GitLab implementation of <see cref="IRepositoryProvider"/> for Git operations.
/// Uses LibGit2Sharp for local git operations and NGitLab for merge request management.
/// This is a partial class — merge request and review operations are in a separate file (task 6.2).
/// </summary>
public partial class GitLabRepositoryProvider : GitLabProviderBase, IRepositoryProvider
{
    private readonly string _baseBranch;
    private readonly ResiliencePipeline _gitPipeline;

    /// <inheritdoc />
    public RepositoryProviderType ProviderType => RepositoryProviderType.GitLab;

    /// <inheritdoc />
    public string BaseBranch => _baseBranch;

    /// <inheritdoc />
    public string RepositoryFullName => PathWithNamespace ?? $"project/{ProjectId}";

    /// <inheritdoc />
    public bool SupportsInlineReviewComments => true;

    /// <summary>
    /// Creates a provider with a static access token.
    /// </summary>
    public GitLabRepositoryProvider(string apiUrl, string accessToken, int projectId, string baseBranch)
        : base(apiUrl, accessToken, projectId)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Creates a provider with a dynamic token provider delegate (for OrchestratorProxy token refresh).
    /// </summary>
    public GitLabRepositoryProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, int projectId, string baseBranch)
        : base(apiUrl, tokenProvider, projectId)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitLabClient.
    /// </summary>
    internal GitLabRepositoryProvider(IGitLabClient client, int projectId, string baseBranch)
        : base(client, projectId)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    // ─── Git Operations ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task CloneAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);
            var cloneUrl = BuildAuthenticatedCloneUrl(token);

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
    public Task PullAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            try
            {
                await RepositoryGitOperations.Pull(workspacePath, _baseBranch, GitConstants.GitLabTokenUsername, token, _gitPipeline, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw RedactTokenFromException(ex, token);
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<string> CreateBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() => RepositoryGitOperations.CreateBranch(workspacePath, branchName), ct);
    }

    /// <inheritdoc />
    public Task CheckoutRemoteBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() => RepositoryGitOperations.CheckoutRemoteBranch(workspacePath, branchName), ct);
    }

    /// <inheritdoc />
    public Task CommitAllAsync(string workspacePath, string message, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, null, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, blacklistedPaths, allowEmpty: false, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(message);

        return Task.Run(() => RepositoryGitOperations.CommitAll(workspacePath, message, blacklistedPaths, allowEmpty), ct);
    }

    /// <inheritdoc />
    public Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

    /// <inheritdoc />
    public Task PushBranchAsync(string workspacePath, string branchName, bool forcePush, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            try
            {
                await RepositoryGitOperations.Push(workspacePath, branchName, forcePush, GitConstants.GitLabTokenUsername, token, _gitPipeline, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            {
                throw RedactTokenFromException(ex, token);
            }
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

    /// <inheritdoc />
    public Task<MergeResult> MergeFromBaseAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);
            return await RepositoryGitOperations.MergeFromBase(workspacePath, _baseBranch, GitConstants.GitLabTokenUsername, token, _gitPipeline, ct);
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
    /// Builds an authenticated clone URL from the cached <c>http_url_to_repo</c>.
    /// Format: <c>https://oauth2:{token}@{host}/{namespace}/{project}.git</c>
    /// Enforces HTTPS scheme regardless of what the API returns.
    /// </summary>
    private string BuildAuthenticatedCloneUrl(string token)
    {
        var httpUrl = HttpUrlToRepo;
        if (string.IsNullOrEmpty(httpUrl))
            throw new InvalidOperationException(
                "Clone URL not available. Call ValidateAsync before CloneAsync to populate project metadata.");

        // Insert oauth2:{token}@ into the URL after the scheme
        var uri = new Uri(httpUrl);

        // Enforce HTTPS scheme regardless of what the API returns
        var scheme = uri.Scheme == "http" || uri.Scheme == "https" ? "https" : throw new InvalidOperationException(
            $"Clone URL has unsupported scheme '{uri.Scheme}'. Only HTTPS is supported for authenticated clone.");

        return $"{scheme}://{GitConstants.GitLabTokenUsername}:{token}@{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}{uri.AbsolutePath}";
    }

    /// <summary>
    /// Redacts the access token from an exception message to prevent token leakage via logs or error handlers.
    /// Returns a new exception of the same type with the token replaced by "[REDACTED]".
    /// </summary>
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

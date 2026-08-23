using LibGit2Sharp;
using System.Diagnostics.CodeAnalysis;
using NGitLab;
using Polly;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
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

    /// <inheritdoc />
    public bool SupportsServerSideBranchUpdate => true;

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
    public Task CloneAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

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
    public Task PullAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

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
    public Task<string> CreateBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() => RepositoryGitOperations.CreateBranch(workspacePath, branchName), ct);
    }

    /// <inheritdoc />
    public Task CheckoutRemoteBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentNullException.ThrowIfNull(branchName);

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
    public Task PushBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct)
        => PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

    /// <inheritdoc />
    // Token factory passed so each Polly retry fetches a fresh token (GitLab tokens can also
    // be dynamically vended; stale tokens cause 403s in long pipeline runs).
    [ExcludeFromCodeCoverage]
    public Task PushBranchAsync(WorkspacePath workspacePath, string branchName, bool forcePush, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(async () =>
        {
            try
            {
                await RepositoryGitOperations.Push(workspacePath, branchName, forcePush,
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

    /// <inheritdoc />
    public Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.HasCommitsAheadAsync(workspacePath, _baseBranch, _gitPipeline, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, CancellationToken ct)
        => SharedRepositoryOperations.GetFileChangesAsync(workspacePath, _baseBranch, ct);

    /// <inheritdoc />
    public Task<MergeResult> MergeFromBaseAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

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

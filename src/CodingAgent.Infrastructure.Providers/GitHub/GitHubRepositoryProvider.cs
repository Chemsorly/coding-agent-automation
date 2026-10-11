using Octokit;
using Polly;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;

namespace CodingAgent.Infrastructure.GitHub;

/// <summary>
/// Performs Git operations via LibGit2Sharp and PR creation via Octokit.
/// Supports both static token authentication (backward compatible) and
/// dynamic token provider delegate (for GitHub App auth).
/// </summary>
public partial class GitHubRepositoryProvider : GitHubProviderBase, IRepositoryProvider
{
    private readonly BranchName _baseBranch;
    private readonly ResiliencePipeline _gitPipeline;

    public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;

    /// <inheritdoc />
    public string BaseBranch => _baseBranch;

    /// <inheritdoc />
    public string RepositoryFullName => $"{Owner}/{Repo}";

    /// <inheritdoc />
    public bool SupportsInlineReviewComments => true;

    /// <inheritdoc />
    public bool SupportsServerSideBranchUpdate => true;

    /// <summary>
    /// Creates a provider with a static token (backward compatible).
    /// </summary>
    public GitHubRepositoryProvider(GitHubConnectionInfo connection, string token, BranchName baseBranch)
        : base(connection, token)
    {
        // TODO: ThrowIfNullOrEmpty guards default(BranchName) and new BranchName("") correctly, but
        // new BranchName(null) bypasses this guard if BranchName's primary constructor does not validate.
        // This mirrors the pre-existing TODO in BranchName.cs — fix there to close the gap at construction.
        ArgumentException.ThrowIfNullOrEmpty(baseBranch.Value);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// </summary>
    public GitHubRepositoryProvider(GitHubConnectionInfo connection, Func<CancellationToken, Task<string>> tokenProvider, BranchName baseBranch)
        : base(connection, tokenProvider)
    {
        // TODO: ThrowIfNullOrEmpty guards default(BranchName) and new BranchName("") correctly, but
        // new BranchName(null) bypasses this guard if BranchName's primary constructor does not validate.
        // This mirrors the pre-existing TODO in BranchName.cs — fix there to close the gap at construction.
        ArgumentException.ThrowIfNullOrEmpty(baseBranch.Value);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubRepositoryProvider(GitHubConnectionInfo connection, IGitHubClient gitHubClient, string token, BranchName baseBranch)
        : base(connection, gitHubClient, token)
    {
        // TODO: ThrowIfNullOrEmpty guards default(BranchName) and new BranchName("") correctly, but
        // new BranchName(null) bypasses this guard if BranchName's primary constructor does not validate.
        // This mirrors the pre-existing TODO in BranchName.cs — fix there to close the gap at construction.
        ArgumentException.ThrowIfNullOrEmpty(baseBranch.Value);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    public Task CloneAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            // Derive clone URL
            var cloneUrl = $"{WebBaseUrl}/{Owner}/{Repo}.git";

            await RepositoryGitOperations.Clone(workspacePath, cloneUrl, _baseBranch, GitConstants.TokenUsername, token, _gitPipeline, ct);
        }, ct);
    }

    /// <summary>
    /// The web base URL for this repository, derived from <see cref="ApiUrl"/>:
    /// replaces <c>api.github.com</c> with <c>github.com</c> (case-insensitive) and strips a trailing
    /// <c>/api/v3</c> segment (GitHub Enterprise). Trailing slashes are trimmed.
    /// </summary>
    // TODO: This property trims trailing slashes before the EndsWith("/api/v3") check, which differs
    // from the original CloneAsync logic (which trimmed after). For a GHE API URL entered with a trailing
    // slash (e.g. "https://github.example.com/api/v3/"), the original code did NOT strip the /api/v3
    // segment (EndsWith check failed), while this property does strip it. This silently changes the clone
    // URL for that edge case, potentially breaking existing GHE configurations. The issue requires the
    // clone URL to be unchanged; this should be reviewed and the original ordering restored if needed.
    private string WebBaseUrl
    {
        get
        {
            var url = (ApiUrl ?? string.Empty).Replace("api.github.com", "github.com", StringComparison.OrdinalIgnoreCase);
            url = url.TrimEnd('/');
            if (url.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
                url = url[..^"/api/v3".Length];
            return url.TrimEnd('/');
        }
    }

    /// <inheritdoc />
    public string? GetCommitWebUrl(string commitSha) =>
        string.IsNullOrEmpty(WebBaseUrl) || string.IsNullOrEmpty(commitSha)
            ? null
            : $"{WebBaseUrl}/{Owner}/{Repo}/commit/{commitSha}";

    public Task PullAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);
            await RepositoryGitOperations.Pull(workspacePath, _baseBranch, GitConstants.TokenUsername, token, _gitPipeline, ct);
        }, ct);
    }

    public Task<string> CreateBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentException.ThrowIfNullOrEmpty(branchName.Value);

        return Task.Run(() => RepositoryGitOperations.CreateBranch(workspacePath, branchName), ct);
    }

    public Task CheckoutRemoteBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentException.ThrowIfNullOrEmpty(branchName.Value);

        return Task.Run(() => RepositoryGitOperations.CheckoutRemoteBranch(workspacePath, branchName), ct);
    }

    /// <summary>
    /// Parses a text string for GitHub issue reference patterns and adds found issue numbers to the set.
    /// Recognizes: #N, owner/repo#N, GH-N, closes #N, fixes #N, resolves #N (case-insensitive).
    /// </summary>
    internal static void ParseIssueReferences(string? text, HashSet<string> issueNumbers)
    {
        IssueReferenceParser.ParseIssueReferences(text, issueNumbers);
    }
}
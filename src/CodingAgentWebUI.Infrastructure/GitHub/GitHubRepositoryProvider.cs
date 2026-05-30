using Octokit;
using Polly;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.GitHub;

/// <summary>
/// Performs Git operations via LibGit2Sharp and PR creation via Octokit.
/// Supports both static token authentication (backward compatible) and
/// dynamic token provider delegate (for GitHub App auth).
/// </summary>
public partial class GitHubRepositoryProvider : GitHubProviderBase, IRepositoryProvider
{
    private readonly string _baseBranch;
    private readonly ResiliencePipeline _gitPipeline;

    // Static compiled regex patterns for ParseIssueReferences (avoid per-call allocation)
    private static readonly Regex ClosingKeywordPattern = new(
        @"(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+(?:#|GH-)(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CrossRepoPattern = new(
        @"[\w\-\.]+/[\w\-\.]+#(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GhPattern = new(
        @"\bGH-(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SimpleHashPattern = new(
        @"(?<![&\w/])#(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;

    /// <inheritdoc />
    public string BaseBranch => _baseBranch;

    /// <inheritdoc />
    public string RepositoryFullName => $"{Owner}/{Repo}";

    /// <inheritdoc />
    public bool SupportsInlineReviewComments => true;

    /// <summary>
    /// Creates a provider with a static token (backward compatible).
    /// </summary>
    public GitHubRepositoryProvider(GitHubConnectionInfo connection, string token, string baseBranch)
        : base(connection, token)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// </summary>
    public GitHubRepositoryProvider(GitHubConnectionInfo connection, Func<CancellationToken, Task<string>> tokenProvider, string baseBranch)
        : base(connection, tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubRepositoryProvider(GitHubConnectionInfo connection, IGitHubClient gitHubClient, string token, string baseBranch)
        : base(connection, gitHubClient, token)
    {
        ArgumentNullException.ThrowIfNull(baseBranch);
        _baseBranch = baseBranch;
        _gitPipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(Log.Logger);
    }

    public Task CloneAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            // Derive clone URL
            var cloneBaseUrl = (ApiUrl ?? string.Empty).Replace("api.github.com", "github.com", StringComparison.OrdinalIgnoreCase);
            if (cloneBaseUrl.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
                cloneBaseUrl = cloneBaseUrl[..^"/api/v3".Length];
            var cloneUrl = $"{cloneBaseUrl.TrimEnd('/')}/{Owner}/{Repo}.git";

            await RepositoryGitOperations.Clone(workspacePath, cloneUrl, _baseBranch, GitConstants.TokenUsername, token, _gitPipeline, ct);
        }, ct);
    }

    public Task PullAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);
            await RepositoryGitOperations.Pull(workspacePath, _baseBranch, GitConstants.TokenUsername, token, _gitPipeline, ct);
        }, ct);
    }

    public Task<string> CreateBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() => RepositoryGitOperations.CreateBranch(workspacePath, branchName), ct);
    }

    public Task CheckoutRemoteBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() => RepositoryGitOperations.CheckoutRemoteBranch(workspacePath, branchName), ct);
    }

    /// <summary>
    /// Parses a text string for GitHub issue reference patterns and adds found issue numbers to the set.
    /// Recognizes: #N, owner/repo#N, GH-N, closes #N, fixes #N, resolves #N (case-insensitive).
    /// </summary>
    internal static void ParseIssueReferences(string? text, HashSet<string> issueNumbers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // Extract from closing keywords first
        foreach (Match match in ClosingKeywordPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }

        // Extract from cross-repo references
        foreach (Match match in CrossRepoPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }

        // Extract from GH-N references
        foreach (Match match in GhPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }

        // Extract from simple #N references
        foreach (Match match in SimpleHashPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }
    }
}

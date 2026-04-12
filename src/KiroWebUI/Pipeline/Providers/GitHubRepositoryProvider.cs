using System.Text.RegularExpressions;
using LibGit2Sharp;
using Octokit;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using Signature = LibGit2Sharp.Signature;
using Repository = LibGit2Sharp.Repository;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Performs Git operations via LibGit2Sharp and PR creation via Octokit.
/// </summary>
public partial class GitHubRepositoryProvider : IRepositoryProvider
{
    private readonly string _apiUrl;
    private readonly string _token;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _baseBranch;
    private readonly IGitHubClient _gitHubClient;

    public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;

    public GitHubRepositoryProvider(string apiUrl, string token, string owner, string repo, string baseBranch)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(baseBranch);

        _apiUrl = apiUrl;
        _token = token;
        _owner = owner;
        _repo = repo;
        _baseBranch = baseBranch;

        _gitHubClient = new GitHubClient(
            new Octokit.ProductHeaderValue("KiroWebUI-Pipeline"),
            new Uri(apiUrl))
        {
            Credentials = new Octokit.Credentials(token)
        };
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubRepositoryProvider(IGitHubClient gitHubClient, string token, string owner, string repo, string baseBranch)
    {
        ArgumentNullException.ThrowIfNull(gitHubClient);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(baseBranch);

        _apiUrl = string.Empty;
        _token = token;
        _owner = owner;
        _repo = repo;
        _baseBranch = baseBranch;
        _gitHubClient = gitHubClient;
    }

    public Task CloneAsync(string workspacePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);

        return Task.Run(() =>
        {
            // Derive clone URL: api.github.com → github.com, or GHE api base → GHE base
            var cloneBaseUrl = _apiUrl.Replace("api.github.com", "github.com", StringComparison.OrdinalIgnoreCase);
            // For GHE: https://github.example.com/api/v3 → https://github.example.com
            if (cloneBaseUrl.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
                cloneBaseUrl = cloneBaseUrl[..^"/api/v3".Length];
            var cloneUrl = $"{cloneBaseUrl.TrimEnd('/')}/{_owner}/{_repo}.git";
            var options = new CloneOptions
            {
                FetchOptions =
                {
                    CredentialsProvider = (_, _, _) =>
                        new UsernamePasswordCredentials { Username = "x-access-token", Password = _token }
                }
            };
            Repository.Clone(cloneUrl, workspacePath, options);
        }, ct);
    }

    public Task<string> CreateBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);
            var branch = repo.CreateBranch(branchName);
            Commands.Checkout(repo, branch);
            return branch.FriendlyName;
        }, ct);
    }

    public Task CommitAllAsync(string workspacePath, string message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(message);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);
            Commands.Stage(repo, "*");
            var signature = new Signature("KiroWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);
            repo.Commit(message, signature, signature);
        }, ct);
    }

    public Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);
            var remote = repo.Network.Remotes["origin"];
            var options = new PushOptions
            {
                CredentialsProvider = (_, _, _) =>
                    new UsernamePasswordCredentials { Username = "x-access-token", Password = _token }
            };
            repo.Network.Push(remote, $"refs/heads/{branchName}", options);
        }, ct);
    }

    public async Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prInfo);

        var newPr = new NewPullRequest(prInfo.Title, prInfo.BranchName, prInfo.BaseBranch)
        {
            Body = prInfo.Body,
            Draft = prInfo.IsDraft
        };

        var pr = await _gitHubClient.PullRequest.Create(_owner, _repo, newPr);
        return pr.HtmlUrl;
    }

    // --- Static helper methods — delegate to PipelineFormatting for shared use ---

    /// <summary>
    /// Generates a branch name from issue number and title.
    /// Delegates to PipelineFormatting.GenerateBranchName.
    /// </summary>
    internal static string GenerateBranchName(string issueNumber, string title)
        => Services.PipelineFormatting.GenerateBranchName(issueNumber, title);

    /// <summary>
    /// Generates a PR title in conventional commit format.
    /// Delegates to PipelineFormatting.GeneratePrTitle.
    /// </summary>
    internal static string GeneratePrTitle(string issueTitle, string issueNumber)
        => Services.PipelineFormatting.GeneratePrTitle(issueTitle, issueNumber);

    /// <summary>
    /// Generates a PR body with all required sections.
    /// Delegates to PipelineFormatting.GeneratePrBody.
    /// </summary>
    internal static string GeneratePrBody(
        string issueNumber,
        int testsPassed,
        int testsFailed,
        int testsSkipped,
        double? coveragePercent,
        string implementationSummary,
        bool isDraft = false)
        => Services.PipelineFormatting.GeneratePrBody(
            issueNumber, testsPassed, testsFailed, testsSkipped,
            coveragePercent, implementationSummary, isDraft);

    /// <summary>
    /// Generates a commit message in conventional format.
    /// Delegates to PipelineFormatting.GenerateCommitMessage.
    /// </summary>
    internal static string GenerateCommitMessage(string title, string issueNumber)
        => Services.PipelineFormatting.GenerateCommitMessage(title, issueNumber);

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericPattern();
}

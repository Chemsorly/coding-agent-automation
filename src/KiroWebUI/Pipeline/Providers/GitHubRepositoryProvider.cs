using System.Text.RegularExpressions;
using LibGit2Sharp;
using Octokit;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;using Signature = LibGit2Sharp.Signature;
using Repository = LibGit2Sharp.Repository;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Performs Git operations via LibGit2Sharp and PR creation via Octokit.
/// Supports both static token authentication (backward compatible) and
/// dynamic token provider delegate (for GitHub App auth).
/// </summary>
public partial class GitHubRepositoryProvider : IRepositoryProvider
{
    private readonly string _apiUrl;
    private readonly string? _token;
    private readonly Func<CancellationToken, Task<string>>? _tokenProvider;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _baseBranch;
    private readonly IGitHubClient? _gitHubClient;

    public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;

    /// <summary>
    /// Creates a provider with a static token (backward compatible).
    /// </summary>
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
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// The delegate is called before each API call to obtain a fresh token.
    /// </summary>
    public GitHubRepositoryProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, string owner, string repo, string baseBranch)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(baseBranch);

        _apiUrl = apiUrl;
        _tokenProvider = tokenProvider;
        _owner = owner;
        _repo = repo;
        _baseBranch = baseBranch;
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

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            // Derive clone URL: api.github.com → github.com, or GHE api base → GHE base
            var cloneBaseUrl = _apiUrl.Replace("api.github.com", "github.com", StringComparison.OrdinalIgnoreCase);
            // For GHE: https://github.example.com/api/v3 → https://github.example.com
            if (cloneBaseUrl.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
                cloneBaseUrl = cloneBaseUrl[..^"/api/v3".Length];
            var cloneUrl = $"{cloneBaseUrl.TrimEnd('/')}/{_owner}/{_repo}.git";
            var options = new CloneOptions
            {
                BranchName = _baseBranch,
                FetchOptions =
                {
                    CredentialsProvider = (_, _, _) =>
                        new UsernamePasswordCredentials { Username = "x-access-token", Password = token }
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

    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message, IReadOnlyList<string> blacklistedPaths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(blacklistedPaths);

        return Task.Run(() =>
        {
            using var repo = new Repository(workspacePath);
            Commands.Stage(repo, "*");

            // Unstage blacklisted files
            var excluded = new List<string>();
            if (blacklistedPaths.Count > 0)
            {
                var staged = repo.RetrieveStatus()
                    .Where(e => e.State is FileStatus.NewInIndex or FileStatus.ModifiedInIndex
                        or FileStatus.DeletedFromIndex or FileStatus.RenamedInIndex or FileStatus.TypeChangeInIndex)
                    .Select(e => e.FilePath)
                    .ToList();

                var blacklisted = Services.BlacklistMatcher.FindBlacklistedPaths(staged, blacklistedPaths);
                if (blacklisted.Count > 0)
                {
                    Commands.Unstage(repo, blacklisted);
                    excluded.AddRange(blacklisted);
                }
            }

            // Check if there are any staged changes before committing
            var status = repo.RetrieveStatus();
            if (!status.Staged.Any())
                throw new InvalidOperationException("No changes to commit. The agent did not modify any files in the workspace.");

            var signature = new Signature("KiroWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);
            repo.Commit(message, signature, signature);

            return (IReadOnlyList<string>)excluded;
        }, ct);
    }

    public Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(branchName);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);

            using var repo = new Repository(workspacePath);
            var remote = repo.Network.Remotes["origin"];
            string? pushError = null;
            var options = new PushOptions
            {
                CredentialsProvider = (_, _, _) =>
                    new UsernamePasswordCredentials { Username = "x-access-token", Password = token },
                OnPushStatusError = error =>
                    pushError = $"Push failed for ref '{error.Reference}': {error.Message}"
            };
            repo.Network.Push(remote, $"refs/heads/{branchName}", options);

            if (pushError != null)
                throw new InvalidOperationException(pushError);
        }, ct);
    }

    public async Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prInfo);

        var client = await GetClientAsync(ct);

        var newPr = new NewPullRequest(prInfo.Title, prInfo.BranchName, prInfo.BaseBranch)
        {
            Body = prInfo.Body,
            Draft = prInfo.IsDraft
        };

        var pr = await client.PullRequest.Create(_owner, _repo, newPr);
        return pr.HtmlUrl;
    }

    /// <summary>
    /// Returns a current token, either from the static field or by calling the token provider.
    /// </summary>
    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_tokenProvider is not null)
            return await _tokenProvider(ct);

        return _token!;
    }

    /// <summary>
    /// Returns a GitHubClient configured with a current token.
    /// If a token provider is set, calls it to get a fresh token and creates a new client.
    /// Otherwise, returns the static client.
    /// </summary>
    private async Task<IGitHubClient> GetClientAsync(CancellationToken ct)
    {
        if (_tokenProvider is not null)
        {
            var token = await _tokenProvider(ct);
            return new GitHubClient(
                new Octokit.ProductHeaderValue("KiroWebUI-Pipeline"),
                new Uri(_apiUrl))
            {
                Credentials = new Octokit.Credentials(token)
            };
        }

        return _gitHubClient!;
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
        IReadOnlyList<FileChangeSummary> fileChanges,
        string issueTitle,
        string issueDescription,
        IReadOnlyList<string> acceptanceCriteria,
        bool isDraft = false,
        IReadOnlyList<Models.IssueComment>? comments = null,
        IReadOnlyList<string>? blacklistedFiles = null)
        => Services.PipelineFormatting.GeneratePrBody(
            issueNumber, testsPassed, testsFailed, testsSkipped,
            coveragePercent, fileChanges, issueTitle, issueDescription,
            acceptanceCriteria, isDraft, comments, blacklistedFiles);

    /// <summary>
    /// Generates a commit message in conventional format.
    /// Delegates to PipelineFormatting.GenerateCommitMessage.
    /// </summary>
    internal static string GenerateCommitMessage(string title, string issueNumber)
        => Services.PipelineFormatting.GenerateCommitMessage(title, issueNumber);

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericPattern();
}

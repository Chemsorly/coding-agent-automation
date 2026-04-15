using Octokit;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Fetches issues from GitHub using the Octokit.NET library.
/// Supports both static token authentication (backward compatible) and
/// dynamic token provider delegate (for GitHub App auth).
/// </summary>
public class GitHubIssueProvider : IIssueProvider
{
    private readonly IGitHubClient? _client;
    private readonly string? _apiUrl;
    private readonly Func<CancellationToken, Task<string>>? _tokenProvider;
    private readonly string _owner;
    private readonly string _repo;

    public IssueProviderType ProviderType => IssueProviderType.GitHub;

    /// <summary>
    /// Creates a provider with a static token (backward compatible).
    /// </summary>
    public GitHubIssueProvider(string apiUrl, string token, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);

        _owner = owner;
        _repo = repo;

        var productHeader = new ProductHeaderValue("KiroWebUI-Pipeline");
        _client = new GitHubClient(productHeader, new Uri(apiUrl))
        {
            Credentials = new Credentials(token)
        };
    }

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// The delegate is called before each API call to obtain a fresh token.
    /// </summary>
    public GitHubIssueProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);

        _apiUrl = apiUrl;
        _tokenProvider = tokenProvider;
        _owner = owner;
        _repo = repo;
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubIssueProvider(IGitHubClient client, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);

        _client = client;
        _owner = owner;
        _repo = repo;
    }

    public async Task<IssueDetail> GetIssueAsync(string identifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (!int.TryParse(identifier, out var issueNumber))
            throw new ArgumentException($"Invalid issue identifier: '{identifier}'. Expected a numeric issue number.", nameof(identifier));

        var client = await GetClientAsync(ct);
        var issue = await client.Issue.Get(_owner, _repo, issueNumber);
        return MapToIssueDetail(issue);
    }

    public async Task<IReadOnlyList<IssueSummary>> ListOpenIssuesAsync(CancellationToken ct)
    {
        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.Open
        };

        var client = await GetClientAsync(ct);
        var issues = await client.Issue.GetAllForRepository(_owner, _repo, request);

        return issues
            .Where(i => i.PullRequest == null) // PRs show up as issues in GitHub API — filter them out
            .Select(MapToIssueSummary)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Returns a GitHubClient configured with a current token.
    /// If a token provider is set, calls it to get a fresh token.
    /// Otherwise, returns the static client.
    /// </summary>
    private async Task<IGitHubClient> GetClientAsync(CancellationToken ct)
    {
        if (_tokenProvider is not null)
        {
            var token = await _tokenProvider(ct);
            return new GitHubClient(
                new ProductHeaderValue("KiroWebUI-Pipeline"),
                new Uri(_apiUrl!))
            {
                Credentials = new Credentials(token)
            };
        }

        return _client!;
    }

    private static IssueDetail MapToIssueDetail(Issue issue)
    {
        return new IssueDetail
        {
            Identifier = issue.Number.ToString(),
            Title = issue.Title ?? string.Empty,
            Description = issue.Body ?? string.Empty,
            Labels = issue.Labels?.Select(l => l.Name).ToList().AsReadOnly()
                ?? (IReadOnlyList<string>)Array.Empty<string>(),
            AcceptanceCriteria = Array.Empty<string>()
        };
    }

    public async Task PostCommentAsync(string identifier, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(body);

        if (!int.TryParse(identifier, out var issueNumber))
            throw new ArgumentException($"Invalid issue identifier: '{identifier}'. Expected a numeric issue number.", nameof(identifier));

        var client = await GetClientAsync(ct);
        await client.Issue.Comment.Create(_owner, _repo, issueNumber, body);
    }

    public async Task<IReadOnlyList<Models.IssueComment>> ListCommentsAsync(string identifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (!int.TryParse(identifier, out var issueNumber))
            throw new ArgumentException($"Invalid issue identifier: '{identifier}'. Expected a numeric issue number.", nameof(identifier));

        var client = await GetClientAsync(ct);
        var comments = await client.Issue.Comment.GetAllForIssue(_owner, _repo, issueNumber);

        return comments
            .Select(c => new Models.IssueComment
            {
                Id = c.Id.ToString(),
                Body = c.Body ?? string.Empty,
                Author = c.User?.Login ?? string.Empty,
                CreatedAt = c.CreatedAt.UtcDateTime
            })
            .ToList()
            .AsReadOnly();
    }

    private static IssueSummary MapToIssueSummary(Issue issue)
    {
        return new IssueSummary
        {
            Identifier = issue.Number.ToString(),
            Title = issue.Title ?? string.Empty,
            Labels = issue.Labels?.Select(l => l.Name).ToList().AsReadOnly()
                ?? (IReadOnlyList<string>)Array.Empty<string>()
        };
    }
}

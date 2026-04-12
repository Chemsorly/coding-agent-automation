using Octokit;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Fetches issues from GitHub using the Octokit.NET library.
/// Configured with API URL, authentication token, owner, and repository.
/// </summary>
public class GitHubIssueProvider : IIssueProvider
{
    private readonly IGitHubClient _client;
    private readonly string _owner;
    private readonly string _repo;

    public IssueProviderType ProviderType => IssueProviderType.GitHub;

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

        var issue = await _client.Issue.Get(_owner, _repo, issueNumber);
        return MapToIssueDetail(issue);
    }

    public async Task<IReadOnlyList<IssueSummary>> ListOpenIssuesAsync(CancellationToken ct)
    {
        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.Open
        };

        var issues = await _client.Issue.GetAllForRepository(_owner, _repo, request);

        return issues
            .Where(i => i.PullRequest == null) // PRs show up as issues in GitHub API — filter them out
            .Select(MapToIssueSummary)
            .ToList()
            .AsReadOnly();
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

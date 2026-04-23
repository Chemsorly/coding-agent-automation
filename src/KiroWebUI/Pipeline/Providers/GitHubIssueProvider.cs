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
    private readonly GitHubClientProvider _clientProvider;
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

        _clientProvider = new GitHubClientProvider(apiUrl, token);
        _owner = owner;
        _repo = repo;
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

        _clientProvider = new GitHubClientProvider(apiUrl, tokenProvider);
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

        _clientProvider = new GitHubClientProvider(client);
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

    public async Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize,
        IReadOnlyList<string>? labels, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);

        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.Open
        };

        if (labels is { Count: > 0 })
        {
            foreach (var label in labels)
                request.Labels.Add(label);
        }

        var apiOptions = new ApiOptions
        {
            PageSize = pageSize + 1, // fetch one extra to detect HasMore
            StartPage = page,
            PageCount = 1
        };

        var client = await GetClientAsync(ct);
        var issues = await client.Issue.GetAllForRepository(_owner, _repo, request, apiOptions);

        var items = issues
            .Where(i => i.PullRequest == null)
            .Select(MapToIssueSummary)
            .ToList();

        // HasMore is approximate: PR filtering may cause false negatives when the
        // extra item fetched for detection is a pull request that gets filtered out.
        var hasMore = items.Count > pageSize;
        if (hasMore)
            items = items.Take(pageSize).ToList();

        return new PagedResult<IssueSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        };
    }

    public Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize, CancellationToken ct)
        => ListOpenIssuesAsync(page, pageSize, labels: null, ct);

    private Task<IGitHubClient> GetClientAsync(CancellationToken ct)
        => _clientProvider.GetClientAsync(ct);

    private static IssueDetail MapToIssueDetail(Issue issue)
    {
        return new IssueDetail
        {
            Identifier = issue.Number.ToString(),
            Title = issue.Title ?? string.Empty,
            Description = issue.Body ?? string.Empty,
            Labels = issue.Labels?.Select(l => l.Name).ToList().AsReadOnly()
                ?? (IReadOnlyList<string>)Array.Empty<string>()
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

    public async Task UpdateCommentAsync(string issueIdentifier, string commentId, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(commentId);
        ArgumentNullException.ThrowIfNull(body);

        if (!int.TryParse(issueIdentifier, out _))
            throw new ArgumentException($"Invalid issue identifier: '{issueIdentifier}'. Expected a numeric issue number.", nameof(issueIdentifier));

        if (!int.TryParse(commentId, out var commentIdParsed))
            throw new ArgumentException($"Invalid comment identifier: '{commentId}'. Expected a numeric comment ID.", nameof(commentId));

        var client = await GetClientAsync(ct);
        await client.Issue.Comment.Update(_owner, _repo, commentIdParsed, body);
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

    /// <inheritdoc />
    public async Task AddLabelsAsync(string identifier, IReadOnlyList<string> labels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(labels);

        if (!int.TryParse(identifier, out var issueNumber))
            throw new ArgumentException($"Invalid issue identifier: '{identifier}'. Expected a numeric issue number.", nameof(identifier));

        var client = await GetClientAsync(ct);
        await client.Issue.Labels.AddToIssue(_owner, _repo, issueNumber, labels.ToArray());
    }

    /// <inheritdoc />
    public async Task RemoveLabelAsync(string identifier, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(label);

        if (!int.TryParse(identifier, out var issueNumber))
            throw new ArgumentException($"Invalid issue identifier: '{identifier}'. Expected a numeric issue number.", nameof(identifier));

        var client = await GetClientAsync(ct);
        try
        {
            await client.Issue.Labels.RemoveFromIssue(_owner, _repo, issueNumber, label);
        }
        catch (NotFoundException)
        {
            // Label not present on issue — no-op
        }
    }

    /// <inheritdoc />
    public async Task EnsureAgentLabelsAsync(CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        foreach (var (name, color) in AgentLabels.Definitions)
        {
            try
            {
                await client.Issue.Labels.Create(_owner, _repo, new NewLabel(name, color));
            }
            catch (ApiValidationException)
            {
                // Label already exists — skip
            }
        }
    }

    /// <inheritdoc />
    public async Task CloseIssueAsync(string identifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (!int.TryParse(identifier, out var issueNumber))
            throw new ArgumentException($"Invalid issue identifier: '{identifier}'. Expected a numeric issue number.", nameof(identifier));

        var client = await GetClientAsync(ct);
        await client.Issue.Update(_owner, _repo, issueNumber, new IssueUpdate { State = ItemState.Closed });
    }

    /// <inheritdoc />
    public async Task ValidateAsync(CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        await client.Repository.Get(_owner, _repo);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
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

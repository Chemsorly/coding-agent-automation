using Octokit;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using PipelineIssueComment = CodingAgentWebUI.Pipeline.Models.IssueComment;

namespace CodingAgentWebUI.Infrastructure.GitHub;

/// <summary>
/// Fetches issues from GitHub using the Octokit.NET library.
/// Supports both static token authentication (backward compatible) and
/// dynamic token provider delegate (for GitHub App auth).
/// </summary>
public class GitHubIssueProvider : GitHubProviderBase, IIssueProvider
{
    public IssueProviderType ProviderType => IssueProviderType.GitHub;

    /// <summary>
    /// Creates a provider with a static token (backward compatible).
    /// </summary>
    public GitHubIssueProvider(string apiUrl, string token, string owner, string repo)
        : base(apiUrl, token, owner, repo) { }

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// The delegate is called before each API call to obtain a fresh token.
    /// </summary>
    public GitHubIssueProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, string owner, string repo)
        : base(apiUrl, tokenProvider, owner, repo) { }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubIssueProvider(IGitHubClient client, string owner, string repo)
        : base(client, owner, repo) { }

    public async Task<IssueDetail> GetIssueAsync(string identifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var issueNumber = ParseIssueIdentifier(identifier);

        var client = await GetClientAsync(ct);
        var issue = await ExecuteWithRateLimitHandlingAsync(
            () => client.Issue.Get(Owner, Repo, issueNumber));
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
        var issues = await ExecuteWithRateLimitHandlingAsync(
            () => client.Issue.GetAllForRepository(Owner, Repo, request, apiOptions));

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
        var issueNumber = ParseIssueIdentifier(identifier);

        var client = await GetClientAsync(ct);
        await ExecuteWithRateLimitHandlingAsync(
            () => client.Issue.Comment.Create(Owner, Repo, issueNumber, body));
    }

    public async Task UpdateCommentAsync(string issueIdentifier, string commentId, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(commentId);
        ArgumentNullException.ThrowIfNull(body);
        ParseIssueIdentifier(issueIdentifier);

        if (!int.TryParse(commentId, out var commentIdParsed))
            throw new ArgumentException($"Invalid comment identifier: '{commentId}'. Expected a numeric comment ID.", nameof(commentId));

        var client = await GetClientAsync(ct);
        await ExecuteWithRateLimitHandlingAsync(
            () => client.Issue.Comment.Update(Owner, Repo, commentIdParsed, body));
    }

    public async Task<IReadOnlyList<PipelineIssueComment>> ListCommentsAsync(string identifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var issueNumber = ParseIssueIdentifier(identifier);

        var client = await GetClientAsync(ct);
        var comments = await ExecuteWithRateLimitHandlingAsync(
            () => client.Issue.Comment.GetAllForIssue(Owner, Repo, issueNumber));

        return comments
            .Select(c => new PipelineIssueComment
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
        var issueNumber = ParseIssueIdentifier(identifier);

        var client = await GetClientAsync(ct);
        await ExecuteWithRateLimitHandlingAsync(
            () => client.Issue.Labels.AddToIssue(Owner, Repo, issueNumber, labels.ToArray()));
    }

    /// <inheritdoc />
    public async Task RemoveLabelAsync(string identifier, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(label);
        var issueNumber = ParseIssueIdentifier(identifier);

        var client = await GetClientAsync(ct);
        try
        {
            await ExecuteWithRateLimitHandlingAsync(
                () => client.Issue.Labels.RemoveFromIssue(Owner, Repo, issueNumber, label));
        }
        catch (NotFoundException)
        {
            // Label not present on issue — no-op
        }
    }

    /// <inheritdoc />
    public async Task<bool> HasAgentLabelsAsync(CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var repoLabels = await ExecuteWithRateLimitHandlingAsync(
            () => client.Issue.Labels.GetAllForRepository(Owner, Repo));
        var repoLabelNames = repoLabels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AgentLabels.All.All(name => repoLabelNames.Contains(name));
    }

    /// <inheritdoc />
    public override async Task ValidateAsync(CancellationToken ct)
    {
        try
        {
            await base.ValidateAsync(ct);
        }
        catch (GitHubAuthException ex) when (ex.ErrorKind == GitHubAuthErrorKind.PrivateKeyDecodeFailure)
        {
            throw new InvalidOperationException("Invalid private key: could not decode from base64", ex);
        }
        catch (GitHubAuthException ex) when (ex.ErrorKind == GitHubAuthErrorKind.TokenExchangeFailure)
        {
            throw new InvalidOperationException($"Authentication failed: {ex.InnerException?.Message ?? ex.Message}", ex);
        }
        catch (AuthorizationException ex)
        {
            throw new InvalidOperationException("Authentication failed: installation token was rejected", ex);
        }
        catch (NotFoundException ex)
        {
            throw new InvalidOperationException("Repository not found or app lacks access", ex);
        }
    }

    public async Task<bool> EnsureAgentLabelsAsync(CancellationToken ct)
    {
        var allSucceeded = true;
        var client = await GetClientAsync(ct);
        foreach (var (name, color) in AgentLabels.Definitions)
        {
            try
            {
                await ExecuteWithRateLimitHandlingAsync(
                    () => client.Issue.Labels.Create(Owner, Repo, new NewLabel(name, color)));
            }
            catch (ApiValidationException)
            {
                // Label already exists — skip
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                allSucceeded = false;
            }
        }
        return allSucceeded;
    }

    /// <inheritdoc />
    public async Task CloseIssueAsync(string identifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var issueNumber = ParseIssueIdentifier(identifier);

        var client = await GetClientAsync(ct);
        await ExecuteWithRateLimitHandlingAsync(
            () => client.Issue.Update(Owner, Repo, issueNumber, new IssueUpdate { State = ItemState.Closed }));
    }

    private static IssueSummary MapToIssueSummary(Issue issue)
    {
        return new IssueSummary
        {
            Identifier = issue.Number.ToString(),
            Title = issue.Title ?? string.Empty,
            Labels = issue.Labels?.Select(l => l.Name).ToList().AsReadOnly()
                ?? (IReadOnlyList<string>)Array.Empty<string>(),
            CreatedAt = issue.CreatedAt.UtcDateTime
        };
    }
}

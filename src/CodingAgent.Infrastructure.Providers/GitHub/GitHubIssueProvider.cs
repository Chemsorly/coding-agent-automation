using System.Diagnostics.CodeAnalysis;
using Octokit;
using Serilog;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using PipelineIssueComment = CodingAgent.Pipeline.Models.IssueComment;

namespace CodingAgent.Infrastructure.GitHub;

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
    public GitHubIssueProvider(GitHubConnectionInfo connection, string token)
        : base(connection, token) { }

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// The delegate is called before each API call to obtain a fresh token.
    /// </summary>
    public GitHubIssueProvider(GitHubConnectionInfo connection, Func<CancellationToken, Task<string>> tokenProvider)
        : base(connection, tokenProvider) { }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubIssueProvider(GitHubConnectionInfo connection, IGitHubClient client)
        : base(connection, client) { }

    public async Task<IssueDetail> GetIssueAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        var issueNumber = ParseIdentifier(identifier);

        var issue = await ExecuteWithResilienceAsync(
            async client =>
            {
                var uri = new Uri(client.Connection.BaseAddress, $"repos/{Owner}/{Repo}/issues/{issueNumber}");
                var response = await client.Connection.Get<GitHubIssueDto>(uri, null, "application/vnd.github+json", ct);
                return response.Body;
            },
            "GetIssue", ct);
        return MapToIssueDetail(issue, await IsAuthorVerifiedAsync(issue, ct));
    }

    public async Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize,
        IReadOnlyList<string>? labels, CancellationToken ct)
    {
        SharedPrOperations.ValidatePaginationArgs(page, pageSize);

        var request = new RepositoryIssueRequest { State = ItemStateFilter.Open };
        if (labels is { Count: > 0 })
            foreach (var label in labels)
                request.Labels.Add(label);

        return await EnumerateIssuesAsync(request, page, pageSize, "ListOpenIssues", ct);
    }

    public Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize, CancellationToken ct)
        => ListOpenIssuesAsync(page, pageSize, labels: null, ct);

    public async Task<PagedResult<IssueSummary>> ListClosedIssuesAsync(int page, int pageSize,
        IReadOnlyList<string>? labels, DateTime? since, CancellationToken ct)
    {
        SharedPrOperations.ValidatePaginationArgs(page, pageSize);

        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.Closed,
            Since = since?.ToUniversalTime()
        };
        if (labels is { Count: > 0 })
            foreach (var label in labels)
                request.Labels.Add(label);

        return await EnumerateIssuesAsync(request, page, pageSize, "ListClosedIssues", ct);
    }

    private static IssueDetail MapToIssueDetail(GitHubIssueDto issue, bool isAuthorVerified)
    {
        return new IssueDetail
        {
            Identifier = issue.Number.ToString(),
            Title = issue.Title ?? string.Empty,
            Description = issue.Body ?? string.Empty,
            Labels = issue.Labels?.Select(l => l.Name ?? string.Empty).ToList().AsReadOnly()
                ?? (IReadOnlyList<string>)Array.Empty<string>(),
            Url = issue.HtmlUrl,
            IsAuthorVerified = isAuthorVerified
        };
    }

    public async Task<string?> PostCommentAsync(IssueIdentifier identifier, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(body);
        var issueNumber = ParseIdentifier(identifier);

        var comment = await ExecuteWithResilienceAsync(
            client => client.Issue.Comment.Create(Owner, Repo, issueNumber, body),
            "PostComment", ct);
        return comment?.HtmlUrl?.ToString();
    }

    public async Task UpdateCommentAsync(IssueIdentifier issueIdentifier, long commentId, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value);
        ArgumentNullException.ThrowIfNull(body);
        ParseIdentifier(issueIdentifier);

        // GitHub comment IDs exceed int.MaxValue; Octokit's Update takes the ID as long, so pass it through.
        await ExecuteWithResilienceAsync(
            client => client.Issue.Comment.Update(Owner, Repo, commentId, body),
            "UpdateComment", ct);
    }

    public async Task<IReadOnlyList<PipelineIssueComment>> ListCommentsAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        var issueNumber = ParseIdentifier(identifier);

        var comments = await ExecuteWithResilienceAsync(
            client => client.Issue.Comment.GetAllForIssue(Owner, Repo, issueNumber,
                new ApiOptions { PageSize = PipelineConstants.DefaultPageSize, PageCount = PipelineConstants.MaxCommentPages }),
            "ListComments", ct);

        var result = new List<PipelineIssueComment>(comments.Count);
        foreach (var c in comments)
        {
            result.Add(new PipelineIssueComment
            {
                Id = c.Id.ToString(),
                Body = c.Body ?? string.Empty,
                Author = c.User?.Login ?? string.Empty,
                CreatedAt = c.CreatedAt.UtcDateTime,
                IsVerified = await IsVerifiedAuthorAsync(
                    c.AuthorAssociation.StringValue, c.User?.Type == AccountType.Bot, c.User?.Login, ct)
            });
        }

        return result.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task AddLabelsAsync(IssueIdentifier identifier, IReadOnlyList<string> labels, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(labels);
        var issueNumber = ParseIdentifier(identifier);

        await ExecuteWithResilienceAsync(
            client => client.Issue.Labels.AddToIssue(Owner, Repo, issueNumber, labels.ToArray()),
            "AddLabels", ct);
    }

    /// <inheritdoc />
    public async Task RemoveLabelAsync(IssueIdentifier identifier, string label, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(label);
        var issueNumber = ParseIdentifier(identifier);

        try
        {
            await ExecuteWithResilienceAsync(
                async client => { await client.Issue.Labels.RemoveFromIssue(Owner, Repo, issueNumber, label); return true; },
                "RemoveLabel", ct);
        }
        catch (NotFoundException)
        {
            // Label not present on issue — no-op
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListRepositoryLabelsAsync(CancellationToken ct)
    {
        var repoLabels = await ExecuteWithResilienceAsync(
            client => client.Issue.Labels.GetAllForRepository(Owner, Repo),
            "ListRepositoryLabels", ct);
        return repoLabels.Select(l => l.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> HasAgentLabelsAsync(CancellationToken ct)
    {
        var repoLabels = await ExecuteWithResilienceAsync(
            client => client.Issue.Labels.GetAllForRepository(Owner, Repo),
            "HasAgentLabels", ct);
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
            Log.Error(ex, "Invalid private key: could not decode from base64 for {Owner}/{Repo}", Owner, Repo);
            throw new InvalidOperationException("Invalid private key: could not decode from base64", ex);
        }
        catch (GitHubAuthException ex) when (ex.ErrorKind == GitHubAuthErrorKind.TokenExchangeFailure)
        {
            Log.Error(ex, "Authentication failed during token exchange for {Owner}/{Repo}: {Reason}", Owner, Repo, ex.InnerException?.Message ?? ex.Message);
            throw new InvalidOperationException($"Authentication failed: {ex.InnerException?.Message ?? ex.Message}", ex);
        }
        catch (AuthorizationException ex)
        {
            Log.Error(ex, "Authentication failed: installation token was rejected for {Owner}/{Repo}", Owner, Repo);
            throw new InvalidOperationException("Authentication failed: installation token was rejected", ex);
        }
        catch (NotFoundException ex)
        {
            Log.Error(ex, "Repository not found or app lacks access: {Owner}/{Repo}", Owner, Repo);
            throw new InvalidOperationException("Repository not found or app lacks access", ex);
        }
    }

    public async Task<bool> EnsureAgentLabelsAsync(CancellationToken ct)
    {
        var allSucceeded = true;
        foreach (var (name, color) in AgentLabels.Definitions)
        {
            try
            {
                await ExecuteWithResilienceAsync(
                    c => c.Issue.Labels.Create(Owner, Repo, new NewLabel(name, color)),
                    "EnsureAgentLabels", ct);
            }
            catch (ApiValidationException)
            {
                // Label already exists — skip
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warning(ex, "Failed to create agent label {LabelName} on {Owner}/{Repo}", name, Owner, Repo);
                allSucceeded = false;
            }
        }
        return allSucceeded;
    }

    /// <inheritdoc />
    public async Task<bool> IsIssueClosedAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        var issueNumber = ParseIdentifier(identifier);

        try
        {
            var issue = await ExecuteWithResilienceAsync(
                client => client.Issue.Get(Owner, Repo, issueNumber),
                "IsIssueClosed", ct);
            return issue.State.Value == ItemState.Closed;
        }
        catch (NotFoundException)
        {
            Log.Warning("Issue #{IssueNumber} not found when checking dependency state", issueNumber); // NOSONAR S6667 — expected 404; the message says so
            return false;
        }
    }

    /// <inheritdoc />
    public async Task CloseIssueAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        var issueNumber = ParseIdentifier(identifier);

        await ExecuteWithResilienceAsync(
            client => client.Issue.Update(Owner, Repo, issueNumber, new IssueUpdate { State = ItemState.Closed }),
            "CloseIssue", ct);
    }

    /// <inheritdoc />
    public async Task<CreatedIssueResult> CreateIssueAsync(
        string title, string body, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        var newIssue = new NewIssue(title) { Body = body };

        if (labels is { Count: > 0 })
        {
            foreach (var label in labels)
                newIssue.Labels.Add(label);
        }

        var created = await ExecuteWithResilienceAsync(
            client => client.Issue.Create(Owner, Repo, newIssue),
            "CreateIssue", ct);

        return new CreatedIssueResult
        {
            Identifier = created.Number.ToString(),
            Url = created.HtmlUrl?.ToString() ?? string.Empty
        };
    }

    #region Private Helpers

    /// <summary>
    /// Fetches a single page of issues using the overfetch-by-one pattern to determine HasMore,
    /// then filters out pull requests and assembles the <see cref="PagedResult{T}"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="request"/> must already have State (and optionally Since/Labels) set by the caller.
    /// <c>hasMore</c> is computed from the raw count before PR filtering — this is intentional so that
    /// a page full of PRs still signals that more pages may exist.
    /// </remarks>
    private async Task<PagedResult<IssueSummary>> EnumerateIssuesAsync(
        RepositoryIssueRequest request, int page, int pageSize, string resilienceLabel, CancellationToken ct)
    {
        var parameters = request.ToParametersDictionary();
        parameters["per_page"] = (pageSize + 1).ToString(); // fetch one extra to detect HasMore
        parameters["page"] = page.ToString();

        var issues = await ExecuteWithResilienceAsync(
            async client =>
            {
                var uri = new Uri(client.Connection.BaseAddress, $"repos/{Owner}/{Repo}/issues");
                var response = await client.Connection.Get<List<GitHubIssueDto>>(uri, parameters, "application/vnd.github+json", ct);
                return response.Body;
            },
            resilienceLabel, ct);

        var hasMore = issues.Count > pageSize;

        var items = new List<IssueSummary>();
        foreach (var issue in issues.Where(i => i.PullRequest == null).Take(pageSize))
            items.Add(MapToIssueSummary(issue, await IsAuthorVerifiedAsync(issue, ct)));

        return new PagedResult<IssueSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        };
    }

    private static IssueSummary MapToIssueSummary(GitHubIssueDto issue, bool isAuthorVerified)
    {
        var labels = issue.Labels?.ToList() ?? [];
        return new IssueSummary
        {
            Identifier = issue.Number.ToString(),
            Title = issue.Title ?? string.Empty,
            Description = issue.Body,
            Labels = labels.Select(l => l.Name ?? string.Empty).ToList().AsReadOnly(),
            LabelColors = labels.Count > 0
                ? labels
                    .Where(l => !string.IsNullOrEmpty(l.Color))
                    .ToDictionary(l => l.Name ?? string.Empty, l => l.Color!, StringComparer.OrdinalIgnoreCase)
                : null,
            CreatedAt = issue.CreatedAt.UtcDateTime,
            Url = issue.HtmlUrl,
            IsAuthorVerified = isAuthorVerified
        };
    }

    private Task<bool> IsAuthorVerifiedAsync(GitHubIssueDto issue, CancellationToken ct) =>
        IsVerifiedAuthorAsync(
            issue.AuthorAssociation,
            string.Equals(issue.User?.Type, "Bot", StringComparison.OrdinalIgnoreCase),
            issue.User?.Login,
            ct);

    #endregion

#pragma warning disable S1144 // Setters are called by Octokit's SimpleJsonSerializer via reflection; Sonar's S1144 ignores [DynamicallyAccessedMembers].
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    private sealed class GitHubIssueDto
    {
        public int Number { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? HtmlUrl { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? AuthorAssociation { get; set; }
        /// <summary>Non-null when the Issues API returned a pull request.</summary>
        public object? PullRequest { get; set; }
        public GitHubIssueUserDto? User { get; set; }
        public GitHubIssueLabelDto[]? Labels { get; set; }
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    private sealed class GitHubIssueUserDto
    {
        public string? Login { get; set; }
        public string? Type { get; set; }
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    private sealed class GitHubIssueLabelDto
    {
        public string? Name { get; set; }
        public string? Color { get; set; }
    }
#pragma warning restore S1144
}

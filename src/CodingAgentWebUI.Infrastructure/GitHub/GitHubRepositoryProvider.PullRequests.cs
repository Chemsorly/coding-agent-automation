using Octokit;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.GitHub;

public partial class GitHubRepositoryProvider
{
    public async Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prInfo);

        var newPr = new NewPullRequest(prInfo.Title, prInfo.BranchName, prInfo.BaseBranch)
        {
            Body = prInfo.Body,
            Draft = prInfo.IsDraft
        };

        var pr = await ExecuteWithResilienceAsync(
            client => client.PullRequest.Create(Owner, Repo, newPr),
            "CreatePullRequest", ct);
        return pr.HtmlUrl;
    }

    public async Task<IReadOnlyList<LinkedPullRequest>> GetAgentPullRequestsAsync(
        string issueIdentifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        var branchPrefix = $"{PipelineConstants.BranchPrefix}{issueIdentifier}-";

        // 1. List all open PRs for the repository
        var allPrs = await ExecuteWithResilienceAsync(
            client => client.PullRequest.GetAllForRepository(
                Owner, Repo,
                new PullRequestRequest { State = ItemStateFilter.Open }),
            "GetAgentPullRequests.List", ct);

        // 2. Filter by branch name prefix (client-side)
        var matching = allPrs
            .Where(pr => pr.Head.Ref.StartsWith(branchPrefix, StringComparison.Ordinal))
            .ToList();

        if (matching.Count == 0)
            return Array.Empty<LinkedPullRequest>();

        // 3. For each match, fetch individual PR to get Mergeable field
        var results = new List<LinkedPullRequest>();
        foreach (var pr in matching)
        {
            var detailed = await ExecuteWithResilienceAsync(
                client => client.PullRequest.Get(Owner, Repo, pr.Number),
                "GetAgentPullRequests.Get", ct);

            // 4. Fetch review comments
            var reviewComments = await ExecuteWithResilienceAsync(
                client => client.PullRequest.ReviewComment.GetAll(Owner, Repo, pr.Number),
                "GetAgentPullRequests.ReviewComments", ct);
            var conversationComments = await ExecuteWithResilienceAsync(
                client => client.Issue.Comment.GetAllForIssue(Owner, Repo, pr.Number),
                "GetAgentPullRequests.ConversationComments", ct);

            var allComments = reviewComments
                .Where(c => !IsPipelineGeneratedComment(c.Body))
                .Select(c => new Pipeline.Models.PullRequestReviewComment
                {
                    Id = c.Id.ToString(),
                    Body = c.Body ?? string.Empty,
                    Author = c.User?.Login ?? string.Empty,
                    CreatedAt = c.CreatedAt.UtcDateTime,
                    Path = c.Path
                })
                .Concat(conversationComments
                    .Where(c => !IsPipelineGeneratedComment(c.Body))
                    .Select(c => new Pipeline.Models.PullRequestReviewComment
                    {
                        Id = c.Id.ToString(),
                        Body = c.Body ?? string.Empty,
                        Author = c.User?.Login ?? string.Empty,
                        CreatedAt = c.CreatedAt.UtcDateTime,
                        Path = null
                    }))
                .OrderBy(c => c.CreatedAt)
                .Take(50)
                .ToList();

            results.Add(new LinkedPullRequest
            {
                Number = detailed.Number,
                BranchName = detailed.Head.Ref,
                Url = detailed.HtmlUrl,
                IsDraft = detailed.Draft,
                IsMergeable = detailed.Mergeable,
                ReviewComments = allComments
            });
        }

        return results;
    }

    private static bool IsPipelineGeneratedComment(string? body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        return body.StartsWith(CommentMarkers.PipelinePrefix, StringComparison.Ordinal)
            || body.Contains(CommentMarkers.AgentCommentPrefix);
    }

    public async Task UpdatePullRequestAsync(int pullRequestNumber, string body, bool markReady, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            await ExecuteWithResilienceAsync(
                client => client.PullRequest.Update(Owner, Repo, pullRequestNumber,
                    new PullRequestUpdate { Body = body }),
                "UpdatePullRequest", ct);

            // Mark as ready for review if requested.
            // GitHub REST API doesn't support changing draft status — requires GraphQL mutation.
            if (markReady)
            {
                try
                {
                    var pr = await ExecuteWithResilienceAsync(
                        client => client.PullRequest.Get(Owner, Repo, pullRequestNumber),
                        "GetPullRequestForDraftCheck", ct);

                    if (pr.Draft)
                    {
                        var client = await GetClientAsync(ct);
                        var graphqlBody = $"{{\"query\":\"mutation {{ markPullRequestReadyForReview(input: {{pullRequestId: \\\"{pr.NodeId}\\\"}}) {{ pullRequest {{ isDraft }} }} }}\"}}";
                        await client.Connection.Post<object>(
                            new Uri("https://api.github.com/graphql"),
                            graphqlBody,
                            "application/json",
                            "application/json");
                        Log.Information("Marked PR #{PrNumber} as ready for review", pullRequestNumber);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "Failed to mark PR #{PrNumber} as ready for review (non-fatal)", pullRequestNumber);
                }
            }
        }
        catch (Octokit.NotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Pull request #{pullRequestNumber} not found in {Owner}/{Repo}.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<PagedResult<PullRequestSummary>> ListOpenPullRequestsAsync(
        int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);

        // GitHub's PR list API doesn't support label filtering directly.
        // Use the Issues API (PRs are issues on GitHub) with label filtering,
        // then fetch full PR details for items that are pull requests.
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

        var issues = await ExecuteWithResilienceAsync(
            client => client.Issue.GetAllForRepository(Owner, Repo, request, apiOptions),
            "ListOpenPullRequests", ct);

        // Base HasMore on raw issues count (before PR filtering) to avoid early termination
        var hasMore = issues.Count > pageSize;

        // Filter to only pull requests (opposite of issue provider which filters them out)
        var prIssues = issues
            .Where(i => i.PullRequest != null)
            .Take(pageSize)
            .ToList();

        // Fetch full PR details for each matching issue to get Draft, Head, Base info
        var items = new List<PullRequestSummary>();
        foreach (var issue in prIssues)
        {
            var pr = await ExecuteWithResilienceAsync(
                client => client.PullRequest.Get(Owner, Repo, issue.Number),
                "ListOpenPullRequests.GetDetail", ct);

            items.Add(new PullRequestSummary
            {
                Number = pr.Number,
                Identifier = pr.Number.ToString(),
                Title = pr.Title,
                Description = pr.Body ?? string.Empty,
                Labels = pr.Labels.Select(l => l.Name).ToArray(),
                BranchName = pr.Head.Ref,
                TargetBranch = pr.Base.Ref,
                Url = pr.HtmlUrl,
                IsDraft = pr.Draft,
                CreatedAt = pr.CreatedAt.UtcDateTime
            });
        }

        return new PagedResult<PullRequestSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        };
    }

    /// <inheritdoc />
    public async Task AddPrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(label);

        await ExecuteWithResilienceAsync(
            client => client.Issue.Labels.AddToIssue(Owner, Repo, prNumber, new[] { label }),
            "AddPrLabel", ct);
    }

    /// <inheritdoc />
    public async Task RemovePrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(label);

        try
        {
            await ExecuteWithResilienceAsync(
                async client => { await client.Issue.Labels.RemoveFromIssue(Owner, Repo, prNumber, label); return true; },
                "RemovePrLabel", ct);
        }
        catch (Octokit.NotFoundException)
        {
            // Label not present on PR — no-op
        }
    }

    /// <inheritdoc />
    public Task<bool> EnsureAgentLabelsForPullRequestsAsync(CancellationToken ct)
    {
        // On GitHub, PRs share the issues label namespace — labels created for issues
        // are already available for PRs. No additional setup needed.
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ExtractLinkedIssuesAsync(int prNumber, CancellationToken ct)
    {
        var issueNumbers = new HashSet<string>(StringComparer.Ordinal);

        // Priority (a): Try GitHub timeline events API for closing references
        try
        {
            var events = await ExecuteWithResilienceAsync(
                client => client.Issue.Timeline.GetAllForIssue(Owner, Repo, prNumber),
                "ExtractLinkedIssues.Timeline", ct);

            foreach (var evt in events)
            {
                // Look for cross-referenced events that indicate closing references
                if (evt.Event == EventInfoState.Crossreferenced && evt.Source?.Issue != null)
                {
                    issueNumbers.Add(evt.Source.Issue.Number.ToString());
                }
            }

            if (issueNumbers.Count > 0)
            {
                // API found results — still parse title/body for additional references
                // that may not appear in timeline events (e.g., "Related to #42" without closing keyword).
                // The HashSet deduplicates across all sources.
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Failed to extract linked issues via timeline API for PR #{PrNumber}, falling back to parsing", prNumber);
        }

        // Priority (b) and (c): Parse PR title and body for issue references
        var pr = await ExecuteWithResilienceAsync(
            client => client.PullRequest.Get(Owner, Repo, prNumber),
            "ExtractLinkedIssues.GetPr", ct);

        // Parse title first (priority b), then body (priority c)
        ParseIssueReferences(pr.Title, issueNumbers);
        ParseIssueReferences(pr.Body, issueNumbers);

        return issueNumbers.ToList().AsReadOnly();
    }
}

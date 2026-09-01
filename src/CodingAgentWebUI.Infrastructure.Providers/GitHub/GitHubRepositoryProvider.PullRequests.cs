using Octokit;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.GitHub;

public partial class GitHubRepositoryProvider
{
    // ── Auto-branch-update (spec 040) ─────────────────────────────────────────

    /// <inheritdoc />
    public async Task<PrMergeabilityStatus> IsPullRequestBehindBaseAsync(int prNumber, CancellationToken ct)
    {
        var pr = await ExecuteWithResilienceAsync(
            client => client.PullRequest.Get(Owner, Repo, prNumber),
            "IsPullRequestBehindBase", ct);

        // IMPORTANT: Blocked MUST map to PrMergeabilityStatus.Blocked, not UpToDate or Conflicted.
        // GitHub returns "blocked" for the full CI run duration (5–30+ min) when required checks
        // are configured. Any other mapping would prematurely free the concurrency slot.
        // Unstable = non-required checks pending/failing; required CI may still be running.
        //
        // pr.MergeableState is StringEnum<MergeableState>; switch on the string value.
        return pr.MergeableState?.StringValue switch
        {
            "behind"    => PrMergeabilityStatus.Behind,
            "clean"     => PrMergeabilityStatus.UpToDate,
            "dirty"     => PrMergeabilityStatus.Conflicted, // merge conflict — trigger rework
            "draft"     => PrMergeabilityStatus.UpToDate,
            "has_hooks" => PrMergeabilityStatus.UpToDate,
            "unstable"  => PrMergeabilityStatus.UpToDate,   // non-required checks only; not a conflict
            "blocked"   => PrMergeabilityStatus.Blocked,    // required checks pending/failing — CI still running
            "unknown"   => PrMergeabilityStatus.Unknown,    // initial async computation (lasts seconds)
            null        => PrMergeabilityStatus.Unknown,
            _           => PrMergeabilityStatus.Unknown     // unknown future values: conservative
        };
    }

    /// <inheritdoc />
    public async Task UpdatePullRequestBranchAsync(int prNumber, CancellationToken ct)
    {
        // Octokit has no typed method for PUT /repos/{owner}/{repo}/pulls/{number}/update-branch.
        // Use the raw IConnection — note: IConnection.Put<T> has no CancellationToken overload.
        // Use client.Connection.BaseAddress (includes /api/v3 for non-github.com hosts)
        // so the URL is consistent with all other Octokit API calls.
        var client = await GetClientAsync(ct);
        var baseAddress = client.Connection.BaseAddress;
        var uri = new Uri(baseAddress, $"repos/{Owner}/{Repo}/pulls/{prNumber}/update-branch");
        await client.Connection.Put<object>(uri, new { }); // NOSONAR S8949 — no ct overload on IConnection.Put
        Log.Information("Triggered server-side branch update for PR #{PrNumber} in {Owner}/{Repo}",
            prNumber, Owner, Repo);
    }

    // ── Branch cleanup (spec 040) ─────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAgentBranchesAsync(CancellationToken ct)
    {
        // Fetch all branches with pagination — GitHub returns 30 per page by default.
        var branches = await ExecuteWithResilienceAsync(
            client => client.Repository.Branch.GetAll(Owner, Repo,
                new ApiOptions { PageSize = 100 }),
            "ListAgentBranches", ct);

        return branches
            .Select(b => b.Name)
            .Where(name => name.StartsWith(PipelineConstants.BranchPrefix, StringComparison.Ordinal))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task DeleteBranchAsync(string branchName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(branchName);
        try
        {
            await ExecuteWithResilienceAsync(
                client => client.Git.Reference.Delete(Owner, Repo, $"refs/heads/{branchName}"),
                "DeleteBranch", ct);
            Log.Information("Housekeeping: deleted stale branch {BranchName} in {Owner}/{Repo}",
                branchName, Owner, Repo);
        }
        catch (NotFoundException)
        {
            // 404 — branch already gone (no-op)
            Log.Debug("Housekeeping: branch {BranchName} not found in {Owner}/{Repo} — already deleted",
                branchName, Owner, Repo);
        }
        catch (ApiValidationException ex) when (ex.Message.Contains("Reference does not exist",
            StringComparison.OrdinalIgnoreCase))
        {
            // 422 — GitHub returns this when the ref doesn't exist (no-op)
            Log.Debug("Housekeeping: branch {BranchName} does not exist in {Owner}/{Repo} — skipping",
                branchName, Owner, Repo);
        }
    }

    // ── PR CRUD ───────────────────────────────────────────────────────────────

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
        IssueIdentifier issueIdentifier, CancellationToken ct)
    {
        if (issueIdentifier.Value is null)
            return Array.Empty<LinkedPullRequest>();

        var branchPrefix = $"{PipelineConstants.BranchPrefix}{issueIdentifier}-";

        // 1. Server-side search for matching PRs (head: qualifier does prefix matching)
        var searchRequest = new SearchIssuesRequest
        {
            Type = IssueTypeQualifier.PullRequest,
            State = ItemState.Open,
            Head = branchPrefix,
            Repos = new RepositoryCollection { { Owner, Repo } }
        };
        var searchResult = await ExecuteWithResilienceAsync(
            client => client.Search.SearchIssues(searchRequest),
            "GetAgentPullRequests.Search", ct);

        if (searchResult.Items.Count == 0)
            return Array.Empty<LinkedPullRequest>();

        // 2. Parallel detail fetch with bounded concurrency (≤3 concurrent)
        using var semaphore = new SemaphoreSlim(3, 3);
        var tasks = searchResult.Items.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var detailed = await ExecuteWithResilienceAsync(
                    client => client.PullRequest.Get(Owner, Repo, item.Number),
                    "GetAgentPullRequests.Get", ct);

                var reviewComments = await ExecuteWithResilienceAsync(
                    client => client.PullRequest.ReviewComment.GetAll(Owner, Repo, item.Number,
                        new ApiOptions { PageSize = PipelineConstants.DefaultPageSize, PageCount = PipelineConstants.MaxPrCommentPages }),
                    "GetAgentPullRequests.ReviewComments", ct);
                var conversationComments = await ExecuteWithResilienceAsync(
                    client => client.Issue.Comment.GetAllForIssue(Owner, Repo, item.Number,
                        new ApiOptions { PageSize = PipelineConstants.DefaultPageSize, PageCount = PipelineConstants.MaxPrCommentPages }),
                    "GetAgentPullRequests.ConversationComments", ct);
                var reviews = await ExecuteWithResilienceAsync(
                    client => client.PullRequest.Review.GetAll(Owner, Repo, item.Number),
                    "GetAgentPullRequests.Reviews", ct);

                var allComments = reviewComments
                    .Where(c => !CommentMarkers.IsPipelineGeneratedComment(c.Body))
                    .Select(c => new Pipeline.Models.PullRequestReviewComment
                    {
                        Id = c.Id.ToString(),
                        Body = c.Body ?? string.Empty,
                        Author = c.User?.Login ?? string.Empty,
                        CreatedAt = c.CreatedAt.UtcDateTime,
                        Path = c.Path
                    })
                    .Concat(conversationComments
                        .Where(c => !CommentMarkers.IsPipelineGeneratedComment(c.Body))
                        .Select(c => new Pipeline.Models.PullRequestReviewComment
                        {
                            Id = c.Id.ToString(),
                            Body = c.Body ?? string.Empty,
                            Author = c.User?.Login ?? string.Empty,
                            CreatedAt = c.CreatedAt.UtcDateTime,
                            Path = null
                        }))
                    .Concat(reviews
                        .Where(r => !string.IsNullOrWhiteSpace(r.Body) && !CommentMarkers.IsPipelineGeneratedComment(r.Body))
                        .Select(r => new Pipeline.Models.PullRequestReviewComment
                        {
                            Id = r.Id.ToString(),
                            Body = r.Body,
                            Author = r.User?.Login ?? string.Empty,
                            CreatedAt = r.SubmittedAt.UtcDateTime,
                            Path = null
                        }))
                    .OrderBy(c => c.CreatedAt)
                    .Take(50)
                    .ToList();

                return new LinkedPullRequest
                {
                    Number = detailed.Number,
                    BranchName = detailed.Head.Ref,
                    Url = detailed.HtmlUrl,
                    IsDraft = detailed.Draft,
                    IsMergeable = detailed.Mergeable,
                    ReviewComments = allComments
                };
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
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

            // Change draft status if requested.
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
                        await client.Connection.Post<object>(DeriveGraphQlUri(), graphqlBody, "application/json", "application/json"); // NOSONAR S8949 — Octokit IConnection.Post has no CancellationToken overload
                        Log.Information("Marked PR #{PrNumber} as ready for review", pullRequestNumber);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "Failed to mark PR #{PrNumber} as ready for review (non-fatal)", pullRequestNumber);
                }
            }
            else
            {
                // Convert to draft if currently ready-for-review.
                // GitHub REST API does not support changing draft status — requires GraphQL mutation.
                try
                {
                    // TODO: The GET below is always issued for every markReady=false call, even when the PR is already
                    // draft. A GET failure is silently swallowed by the catch below, collapsing "already-draft" and
                    // "GET failed" into the same no-op outcome with no way to distinguish them from the caller.
                    // Consider returning the PR object from the preceding PATCH (if the Octokit client exposes it)
                    // to avoid the extra round-trip and make the two failure modes distinguishable.
                    var pr = await ExecuteWithResilienceAsync(
                        client => client.PullRequest.Get(Owner, Repo, pullRequestNumber),
                        "GetPullRequestForDraftConversion", ct);

                    if (!pr.Draft)
                    {
                        var client = await GetClientAsync(ct);
                        // TODO: pr.NodeId is embedded via string interpolation without JSON escaping. GitHub-issued
                        // node IDs are safe in practice, but a proper JSON serializer or GraphQL variable binding
                        // should be used here (and in the markPullRequestReadyForReview branch above) to eliminate
                        // the theoretical risk of a malformed mutation if NodeId ever contains '"' or '\'.
                        var graphqlBody = $"{{\"query\":\"mutation {{ convertPullRequestToDraft(input: {{pullRequestId: \\\"{pr.NodeId}\\\"}}) {{ pullRequest {{ isDraft }} }} }}\"}}";
                        // TODO: CancellationToken is not propagated to IConnection.Post — the Octokit overload used
                        // here has no CT parameter. Track for a future Octokit upgrade that adds CT support.
                        await client.Connection.Post<object>(DeriveGraphQlUri(), graphqlBody, "application/json", "application/json"); // NOSONAR S8949 — Octokit IConnection.Post has no CancellationToken overload
                        Log.Information("Converted PR #{PrNumber} to draft", pullRequestNumber);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "Failed to convert PR #{PrNumber} to draft (non-fatal)", pullRequestNumber);
                }
            }
        }
        catch (Octokit.NotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Pull request #{pullRequestNumber} not found in {Owner}/{Repo}.", ex);
        }
    }

    public async Task<string?> GetPullRequestBodyAsync(int pullRequestNumber, CancellationToken ct)
    {
        try
        {
            var pr = await ExecuteWithResilienceAsync(
                client => client.PullRequest.Get(Owner, Repo, pullRequestNumber),
                "GetPullRequestBody", ct);
            return pr?.Body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Failed to fetch PR #{PrNumber} body, falling back to in-memory state", pullRequestNumber);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task ClosePullRequestAsync(int pullRequestNumber, CancellationToken ct)
    {
        await ExecuteWithResilienceAsync(
            client => client.PullRequest.Update(Owner, Repo, pullRequestNumber,
                new PullRequestUpdate { State = ItemState.Closed }),
            "ClosePullRequest", ct);
        Log.Information("Closed PR #{PrNumber} in {Owner}/{Repo}", pullRequestNumber, Owner, Repo);
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

        // apiOptions are no longer used — fetching is done in the loop below.
        // The PageSize+1 overfetch is now handled per-PR, not per-issue.

        // GitHub's Issues API mixes PRs and plain issues. We need pageSize+1 PRs to detect
        // HasMore, but fetching pageSize+1 issues is insufficient when many issues are not PRs.
        // Strategy: fetch batches of issues (up to 100 per GitHub page) and collect PRs until
        // we have pageSize+1 or exhaust all issues. Skip (page-1)*pageSize PRs to implement
        // server-side pagination entirely client-side over the sequential issue stream.
        const int FetchMultiplier = 3;
        var fetchSize = Math.Min(pageSize * FetchMultiplier, 100);
        var target = pageSize + 1; // one extra to detect HasMore
        var prsToSkip = (page - 1) * pageSize;
        var prsSkipped = 0;
        var prsFetched = new List<Octokit.Issue>();

        for (var attempt = 0; attempt < 10 && prsFetched.Count < target; attempt++)
        {
            var batch = await ExecuteWithResilienceAsync(
                client => client.Issue.GetAllForRepository(Owner, Repo, request,
                    new ApiOptions { PageSize = fetchSize, StartPage = attempt + 1, PageCount = 1 }),
                "ListOpenPullRequests", ct);

            if (batch.Count == 0) break;

            foreach (var issue in batch)
            {
                if (issue.PullRequest == null) continue;
                if (prsSkipped < prsToSkip) { prsSkipped++; continue; }
                prsFetched.Add(issue);
                if (prsFetched.Count >= target) break;
            }

            if (batch.Count < fetchSize) break; // last page of issues from GitHub
        }

        var hasMore = prsFetched.Count > pageSize;
        var prIssues = prsFetched.Take(pageSize).ToList();

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
                Author = pr.User?.Login,
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
                client => client.Issue.Timeline.GetAllForIssue(Owner, Repo, prNumber,
                    new ApiOptions { PageSize = PipelineConstants.DefaultPageSize, PageCount = PipelineConstants.MaxTimelineEventPages }),
                "ExtractLinkedIssues.Timeline", ct);

            foreach (var evt in events)
            {
                // Look for cross-referenced events that indicate closing references.
                // Filter out events where the source is a PR (evt.Source.Issue.PullRequest != null):
                // GitHub fires these when another PR mentions this one, but we only want linked
                // *issues*, not back-references from other PRs (which can include the PR's own number).
                if (evt.Event == EventInfoState.Crossreferenced
                    && evt.Source?.Issue != null
                    && evt.Source.Issue.PullRequest == null)
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

        // Priority (b) and (c): Parse PR title and body for issue references.
        // Title uses full ParseIssueReferences (short, unambiguous, always contains (#N)).
        // Body uses ParseClosingKeywords only (Closes/Fixes/Resolves #N) — the broad SimpleHashPattern
        // picks up any bare #N in the body prose (e.g., "PR #2194" in AI review text), which can
        // return spurious numbers including the PR's own number.
        var pr = await ExecuteWithResilienceAsync(
            client => client.PullRequest.Get(Owner, Repo, prNumber),
            "ExtractLinkedIssues.GetPr", ct);

        // Title first (priority b) — full patterns including (#N) parenthetical convention
        ParseIssueReferences(pr.Title, issueNumbers);
        // Body (priority c) — closing keywords only to avoid false positives from prose mentions
        IssueReferenceParser.ParseClosingKeywords(pr.Body, issueNumbers);

        return issueNumbers.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Pipeline.Models.PrConversationComment>> ListPullRequestCommentsAsync(
        int prNumber, string prAuthor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prAuthor);

        var results = new List<Pipeline.Models.PrConversationComment>();

        // Fetch general discussion comments (issue comments on the PR)
        var issueComments = await ExecuteWithResilienceAsync(
            client => client.Issue.Comment.GetAllForIssue(Owner, Repo, prNumber,
                new ApiOptions { PageSize = PipelineConstants.DefaultPageSize, PageCount = PipelineConstants.MaxPrCommentPages }),
            "ListPrComments.IssueComments", ct);

        foreach (var c in issueComments)
        {
            var author = c.User?.Login ?? "";
            results.Add(new Pipeline.Models.PrConversationComment
            {
                Author = author,
                CreatedAt = c.CreatedAt.UtcDateTime,
                Body = c.Body ?? string.Empty,
                IsBot = c.User?.Type == AccountType.Bot || author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase),
                IsAuthor = string.Equals(author, prAuthor, StringComparison.OrdinalIgnoreCase),
                FilePath = null,
                Line = null,
                IsResolved = null
            });
        }

        // Fetch review comments (inline comments on specific code lines)
        var reviewComments = await ExecuteWithResilienceAsync(
            client => client.PullRequest.ReviewComment.GetAll(Owner, Repo, prNumber,
                new ApiOptions { PageSize = PipelineConstants.DefaultPageSize, PageCount = PipelineConstants.MaxPrCommentPages }),
            "ListPrComments.ReviewComments", ct);

        foreach (var c in reviewComments)
        {
            var author = c.User?.Login ?? "";
            results.Add(new Pipeline.Models.PrConversationComment
            {
                Author = author,
                CreatedAt = c.CreatedAt.UtcDateTime,
                Body = c.Body ?? string.Empty,
                IsBot = c.User?.Type == AccountType.Bot || author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase),
                IsAuthor = string.Equals(author, prAuthor, StringComparison.OrdinalIgnoreCase),
                FilePath = c.Path,
                Line = c.OriginalPosition,
                IsResolved = null // GitHub review comments don't have individual resolution status via Octokit
            });
        }

        // Fetch PR review bodies (REQUEST_CHANGES / APPROVE / COMMENT reviews with body text).
        // These are distinct from inline review comments — they are the top-level review summary
        // submitted via GitHub's review workflow and are not returned by the other two APIs.
        var reviews = await ExecuteWithResilienceAsync(
            client => client.PullRequest.Review.GetAll(Owner, Repo, prNumber),
            "ListPrComments.Reviews", ct);

        foreach (var r in reviews)
        {
            if (string.IsNullOrWhiteSpace(r.Body)) continue;
            var author = r.User?.Login ?? "";
            results.Add(new Pipeline.Models.PrConversationComment
            {
                Author = author,
                CreatedAt = r.SubmittedAt.UtcDateTime,
                Body = r.Body,
                IsBot = r.User?.Type == AccountType.Bot || author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase),
                IsAuthor = string.Equals(author, prAuthor, StringComparison.OrdinalIgnoreCase),
                FilePath = null,
                Line = null,
                IsResolved = null
            });
        }

        return results.OrderBy(c => c.CreatedAt).ToList().AsReadOnly();
    }
}

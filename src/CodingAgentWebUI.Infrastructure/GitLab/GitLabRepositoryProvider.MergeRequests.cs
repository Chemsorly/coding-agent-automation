using System.Text.RegularExpressions;
using NGitLab;
using NGitLab.Models;
using Serilog;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.GitLab;

/// <summary>
/// Partial class containing merge request and review operations for the GitLab repository provider.
/// Handles MR CRUD, label management, closing keyword extraction, inline review comments,
/// and discussion thread resolution.
/// </summary>
public partial class GitLabRepositoryProvider
{
    /// <summary>
    /// Regex for extracting closing keyword references from MR title/description.
    /// Matches: Closes #N, Fixes #N, Resolves #N (case-insensitive).
    /// </summary>
    private static readonly Regex ClosingKeywordPattern = new(
        @"(?:Closes|Fixes|Resolves)\s+#(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ─── Merge Request CRUD ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prInfo);

        var title = prInfo.IsDraft ? $"Draft: {prInfo.Title}" : prInfo.Title;

        try
        {
            var mr = await ExecuteWriteWithResilienceAsync(
                client =>
                {
                    var mrClient = client.GetMergeRequest(ProjectId);
                    return mrClient.Create(new MergeRequestCreate
                    {
                        Title = title,
                        Description = prInfo.Body,
                        SourceBranch = prInfo.BranchName,
                        TargetBranch = prInfo.BaseBranch
                    });
                },
                "CreateMergeRequest", ct);

            return mr.WebUrl;
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 409)
        {
            throw new InvalidOperationException(
                $"A merge request already exists for source branch '{prInfo.BranchName}' " +
                $"targeting '{prInfo.BaseBranch}' in project {ProjectId}.", ex);
        }
    }

    /// <inheritdoc />
    public async Task UpdatePullRequestAsync(int pullRequestNumber, string body, bool markReady, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            var client = await GetClientAsync(ct);
            var mrClient = client.GetMergeRequest(ProjectId);

            // Get current MR to check title for Draft prefix
            var mr = await ExecuteWithResilienceAsync(
                _ => Task.Run(() => mrClient[pullRequestNumber], ct),
                "UpdateMergeRequest.Get", ct);

            var update = new MergeRequestUpdate
            {
                Description = body
            };

            // Remove "Draft: " prefix from title when marking ready
            if (markReady && mr.Title.StartsWith("Draft: ", StringComparison.Ordinal))
            {
                update.Title = mr.Title["Draft: ".Length..];
            }

            await ExecuteWriteWithResilienceAsync(
                _ => mrClient.Update(pullRequestNumber, update),
                "UpdateMergeRequest.Update", ct);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            throw new InvalidOperationException(
                $"Merge request !{pullRequestNumber} not found in project {ProjectId}.", ex);
        }
    }

    // ─── Agent MR Discovery ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkedPullRequest>> GetAgentPullRequestsAsync(
        string issueIdentifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        var branchPrefix = $"{PipelineConstants.BranchPrefix}{issueIdentifier}-";

        var client = await GetClientAsync(ct);
        var mrClient = client.GetMergeRequest(ProjectId);

        // List all open MRs and filter by branch prefix (client-side)
        var allMrs = await ExecuteWithResilienceAsync(
            _ => mrClient.Get(new MergeRequestQuery
            {
                State = MergeRequestState.opened,
                PerPage = 100
            }).ToList(),
            "GetAgentMergeRequests.List", ct);

        var matching = allMrs
            .Where(mr => mr.SourceBranch.StartsWith(branchPrefix, StringComparison.Ordinal))
            .ToList();

        if (matching.Count == 0)
            return Array.Empty<LinkedPullRequest>();

        var results = new List<LinkedPullRequest>();
        foreach (var mr in matching)
        {
            // Fetch discussion notes for review comments (filter pipeline-generated)
            var reviewComments = await GetMergeRequestReviewCommentsAsync(client, mr.Iid, ct);

            results.Add(new LinkedPullRequest
            {
                Number = (int)mr.Iid,
                BranchName = mr.SourceBranch,
                Url = mr.WebUrl,
                IsDraft = mr.Draft,
                IsMergeable = mr.HasConflicts ? false : null,
                ReviewComments = reviewComments
            });
        }

        return results;
    }

    // ─── MR Listing with Pagination ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<PagedResult<PullRequestSummary>> ListOpenPullRequestsAsync(
        int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);

        var client = await GetClientAsync(ct);
        var mrClient = client.GetMergeRequest(ProjectId);

        var query = new MergeRequestQuery
        {
            State = MergeRequestState.opened,
            Labels = labels is { Count: > 0 } ? string.Join(",", labels) : null,
            PerPage = pageSize
        };

        // Use overfetch-by-one pattern to detect HasMore
        var allItems = new List<MergeRequest>();
        var skipCount = (page - 1) * pageSize;
        var takeCount = pageSize + 1;

        var mrs = await ExecuteWithResilienceAsync(
            _ => mrClient.Get(query).ToList(),
            "ListOpenMergeRequests", ct);

        // Apply skip/take manually since NGitLab auto-paginates
        foreach (var mr in mrs)
        {
            if (skipCount > 0) { skipCount--; continue; }
            allItems.Add(mr);
            if (allItems.Count >= takeCount) break;
        }

        var hasMore = allItems.Count > pageSize;
        var items = allItems.Take(pageSize).Select(mr => new PullRequestSummary
        {
            Number = (int)mr.Iid,
            Identifier = mr.Iid.ToString(),
            Title = mr.Title,
            Description = mr.Description ?? string.Empty,
            Labels = mr.Labels ?? Array.Empty<string>(),
            BranchName = mr.SourceBranch,
            TargetBranch = mr.TargetBranch,
            Url = mr.WebUrl,
            IsDraft = mr.Draft,
            CreatedAt = mr.CreatedAt
        }).ToList();

        return new PagedResult<PullRequestSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        };
    }

    // ─── Label Management ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task AddPrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(label);

        await ExecuteWriteWithResilienceAsync(
            client =>
            {
                var mrClient = client.GetMergeRequest(ProjectId);
                return mrClient.Update(prNumber, new MergeRequestUpdate
                {
                    AddLabels = label
                });
            },
            "AddPrLabel", ct);
    }

    /// <inheritdoc />
    public async Task RemovePrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(label);

        try
        {
            await ExecuteWriteWithResilienceAsync(
                client =>
                {
                    var mrClient = client.GetMergeRequest(ProjectId);
                    return mrClient.Update(prNumber, new MergeRequestUpdate
                    {
                        RemoveLabels = label
                    });
                },
                "RemovePrLabel", ct);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            // MR not found — treat as no-op
            Log.Warning(ex, "RemovePrLabel: MR !{PrNumber} not found, treating as no-op", prNumber);
        }
    }

    // ─── Closing Keyword Extraction ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ExtractLinkedIssuesAsync(int prNumber, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        var mrClient = client.GetMergeRequest(ProjectId);

        var mr = await ExecuteWithResilienceAsync(
            _ => Task.Run(() => mrClient[prNumber], ct),
            "ExtractLinkedIssues.Get", ct);

        var issueNumbers = new HashSet<string>(StringComparer.Ordinal);

        // Parse title and description for closing keywords
        ParseClosingKeywords(mr.Title, issueNumbers);
        ParseClosingKeywords(mr.Description, issueNumbers);

        return issueNumbers.ToList().AsReadOnly();
    }

    // ─── Review Operations ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task SubmitPullRequestReviewAsync(
        int prNumber, string body, PullRequestReviewType type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            // Create a simple note on the MR with the review body
            await ExecuteWriteWithResilienceAsync(
                client =>
                {
                    var commentClient = client.GetMergeRequest(ProjectId).Comments(prNumber);
                    return commentClient.Add(new MergeRequestCommentCreate
                    {
                        Body = body
                    });
                },
                "SubmitReview.CreateNote", ct);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            throw new InvalidOperationException(
                $"Merge request !{prNumber} not found in project {ProjectId}.", ex);
        }
    }

    /// <inheritdoc />
    public async Task SubmitPullRequestReviewAsync(int prNumber, ReviewSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // When Comments is empty, delegate to the body-only overload
        if (submission.Comments.Count == 0)
        {
            await SubmitPullRequestReviewAsync(prNumber, submission.Body, submission.Type, ct);
            return;
        }

        var client = await GetClientAsync(ct);
        var mrClient = client.GetMergeRequest(ProjectId);

        // Get MR diff version SHAs for positioning inline comments
        MergeRequestVersion? latestVersion = null;
        try
        {
            var versions = new List<MergeRequestVersion>();
            await foreach (var v in mrClient.GetVersionsAsync(prNumber).WithCancellation(ct))
            {
                versions.Add(v);
            }

            latestVersion = versions
                .OrderByDescending(v => v.CreatedAt)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Failed to get MR versions for !{PrNumber}, inline comments will fall back to notes", prNumber);
        }

        // Post the summary body as a note first
        try
        {
            await ExecuteWriteWithResilienceAsync(
                _ =>
                {
                    var commentClient = mrClient.Comments(prNumber);
                    return commentClient.Add(new MergeRequestCommentCreate
                    {
                        Body = submission.Body
                    });
                },
                "SubmitReview.CreateSummaryNote", ct);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            throw new InvalidOperationException(
                $"Merge request !{prNumber} not found in project {ProjectId}.", ex);
        }

        // Create discussion threads for each inline comment
        var discussionClient = mrClient.Discussions(prNumber);

        foreach (var comment in submission.Comments)
        {
            try
            {
                if (latestVersion is not null)
                {
                    // Attempt inline discussion thread with position
                    await ExecuteWriteWithResilienceAsync(
                        _ => Task.Run(() => discussionClient.Add(new MergeRequestDiscussionCreate
                        {
                            Body = comment.Body,
                            Position = new Position
                            {
                                BaseSha = new Sha1(latestVersion.BaseCommitSha),
                                HeadSha = new Sha1(latestVersion.HeadCommitSha),
                                StartSha = new Sha1(latestVersion.StartCommitSha),
                                PositionType = new DynamicEnum<PositionType>(NGitLab.Models.PositionType.Text),
                                NewPath = comment.Path,
                                NewLine = comment.Line
                            }
                        }), ct),
                        $"SubmitReview.InlineComment({comment.Path}:{comment.Line})", ct);
                }
                else
                {
                    // No version info — fall back to non-inline note
                    await CreateFallbackNoteAsync(discussionClient, comment, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            {
                // Position error — fall back to non-inline note with file/line in body
                Log.Warning(ex,
                    "Failed to create inline comment on !{PrNumber} at {Path}:{Line}, falling back to note",
                    prNumber, comment.Path, comment.Line);

                try
                {
                    await CreateFallbackNoteAsync(discussionClient, comment, ct);
                }
                catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                {
                    Log.Warning(fallbackEx,
                        "Failed to create fallback note for !{PrNumber} at {Path}:{Line}",
                        prNumber, comment.Path, comment.Line);
                }
            }
        }
    }

    // ─── Dismiss Previous Reviews ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task DismissPreviousReviewAsync(int prNumber, string marker, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(reason);

        var client = await GetClientAsync(ct);
        var discussionClient = client.GetMergeRequest(ProjectId).Discussions(prNumber);

        // Get all discussions and resolve threads containing the marker
        var discussions = await ExecuteWithResilienceAsync(
            _ => Task.Run(() => discussionClient.All.ToList(), ct),
            "DismissPreviousReview.GetDiscussions", ct);

        var matchingThreads = discussions
            .Where(d => d.Notes?.Any(n =>
                n.Body?.Contains(marker, StringComparison.Ordinal) == true) == true)
            .ToList();

        if (matchingThreads.Count == 0)
        {
            return; // No-op when no matching threads found
        }

        Log.Information(
            "Found {Count} previous review thread(s) to resolve on MR !{PrNumber}",
            matchingThreads.Count, prNumber);

        // Resolve each matching thread. Log warning and continue on individual failures.
        foreach (var thread in matchingThreads)
        {
            try
            {
                await ExecuteWriteWithResilienceAsync(
                    _ => Task.Run(() => discussionClient.Resolve(new MergeRequestDiscussionResolve
                    {
                        Id = thread.Id,
                        Resolved = true
                    }), ct),
                    $"DismissPreviousReview.Resolve({thread.Id})", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(
                    ex,
                    "Failed to resolve discussion thread {ThreadId} on MR !{PrNumber}. Continuing with remaining threads.",
                    thread.Id, prNumber);
            }
        }

        // Log the reason (GitLab's resolve API does not accept a reason message)
        Log.Debug("Dismissed previous reviews on MR !{PrNumber} with reason: {Reason}", prNumber, reason);
    }

    // ─── Find and Update Review Comments ─────────────────────────────────────────

    /// <inheritdoc />
    public async Task<long?> FindExistingReviewCommentAsync(int prNumber, string marker, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(marker);

        var client = await GetClientAsync(ct);
        var commentClient = client.GetMergeRequest(ProjectId).Comments(prNumber);

        var notes = await ExecuteWithResilienceAsync(
            _ => Task.Run(() => commentClient.All.ToList(), ct),
            "FindExistingReviewComment.GetNotes", ct);

        var match = notes.FirstOrDefault(n =>
            n.Body?.Contains(marker, StringComparison.Ordinal) == true);

        return match?.Id;
    }

    /// <inheritdoc />
    public async Task UpdateReviewCommentAsync(int prNumber, long commentId, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            await ExecuteWriteWithResilienceAsync(
                client =>
                {
                    var commentClient = client.GetMergeRequest(ProjectId).Comments(prNumber);
                    return commentClient.Edit(commentId, new MergeRequestCommentEdit
                    {
                        Body = body
                    });
                },
                "UpdateReviewComment", ct);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            throw new InvalidOperationException(
                $"Merge request !{prNumber} or note {commentId} not found in project {ProjectId}.", ex);
        }
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses text for closing keyword patterns (Closes/Fixes/Resolves #N) and adds
    /// matched issue IIDs to the provided set.
    /// </summary>
    private static void ParseClosingKeywords(string? text, HashSet<string> issueNumbers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (Match match in ClosingKeywordPattern.Matches(text))
        {
            issueNumbers.Add(match.Groups[1].Value);
        }
    }

    /// <summary>
    /// Creates a non-inline discussion note as a fallback when inline positioning fails.
    /// Includes the file path and line number in the body text.
    /// </summary>
    private Task CreateFallbackNoteAsync(
        IMergeRequestDiscussionClient discussionClient,
        ReviewComment comment,
        CancellationToken ct)
    {
        var fallbackBody = $"**{comment.Path}:{comment.Line}**\n\n{comment.Body}";
        return Task.Run(() => discussionClient.Add(new MergeRequestDiscussionCreate
        {
            Body = fallbackBody
        }), ct);
    }

    /// <summary>
    /// Retrieves review comments from MR discussions, filtering out pipeline-generated comments.
    /// Returns up to 50 comments ordered by creation date.
    /// </summary>
    private async Task<IReadOnlyList<PullRequestReviewComment>> GetMergeRequestReviewCommentsAsync(
        IGitLabClient client, long mrIid, CancellationToken ct)
    {
        try
        {
            var discussionClient = client.GetMergeRequest(ProjectId).Discussions(mrIid);
            var discussions = await ExecuteWithResilienceAsync(
                _ => Task.Run(() => discussionClient.All.ToList(), ct),
                "GetMergeRequestReviewComments", ct);

            return discussions
                .SelectMany(d => d.Notes ?? Enumerable.Empty<MergeRequestComment>())
                .Where(n => !IsPipelineGeneratedComment(n.Body))
                .OrderBy(n => n.CreatedAt)
                .Take(50)
                .Select(n => new PullRequestReviewComment
                {
                    Id = n.Id.ToString(),
                    Body = n.Body ?? string.Empty,
                    Author = n.Author?.Username ?? string.Empty,
                    CreatedAt = n.CreatedAt,
                    Path = null // GitLab discussion notes don't expose path at the note level easily
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Failed to retrieve review comments for MR !{MrIid}", mrIid);
            return Array.Empty<PullRequestReviewComment>();
        }
    }

    /// <summary>
    /// Determines whether a comment body is pipeline-generated (should be filtered from listings).
    /// Checks for <see cref="CommentMarkers.PipelinePrefix"/> or <see cref="CommentMarkers.AgentCommentPrefix"/>.
    /// </summary>
    private static bool IsPipelineGeneratedComment(string? body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        return body.StartsWith(CommentMarkers.PipelinePrefix, StringComparison.Ordinal)
            || body.Contains(CommentMarkers.AgentCommentPrefix);
    }
}

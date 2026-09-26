using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>
/// In-memory issue provider for E2E tests. Tracks all side effects (comments, labels).
/// Label changes are stateful: AddLabelsAsync/RemoveLabelAsync mutate the stored IssueDetail.
/// Comments are stateful: PostCommentAsync stores comments, ListCommentsAsync returns them,
/// UpdateCommentAsync edits them in place.
/// </summary>
public sealed class InMemoryIssueProvider : IIssueProvider
{
    public List<IssueDetail> Issues { get; } = new();
    public List<(string Identifier, string Body)> PostedComments { get; } = new();
    public List<(string Identifier, string Label, bool Added)> LabelChanges { get; } = new();
    public List<(string Identifier, long CommentId, string NewBody)> UpdatedComments { get; } = new();
    public bool ShouldFail { get; set; }
    public HashSet<string> ClosedIssueIdentifiers { get; } = new();

    // Comment storage: per-issue ordered list of (comment, updatedAt)
    private readonly Dictionary<string, List<(IssueComment Comment, DateTime? UpdatedAt)>> _comments = new();
    // TODO [WARNING]: _nextCommentId is mutated with ++_nextCommentId (non-atomic). If PostCommentAsync is ever
    // called concurrently (e.g. via Task.WhenAll inside a pipeline step), two threads can read the same value
    // and produce duplicate IDs. Switch to Interlocked.Increment(ref _nextCommentId) for safety.
    // TODO [WARNING]: The spec says IDs "start at 5_000_000_000"; with pre-increment (++_nextCommentId) the first
    // issued ID is 5_000_000_001, which is an off-by-one relative to the literal spec wording. Functionally
    // harmless (all IDs remain above int.MaxValue), but worth noting for spec precision.
    private long _nextCommentId = 5_000_000_000L; // above int.MaxValue per spec

    public IssueProviderType ProviderType => IssueProviderType.GitHub;

    public void Reset()
    {
        Issues.Clear();
        PostedComments.Clear();
        LabelChanges.Clear();
        UpdatedComments.Clear();
        _comments.Clear();
        _nextCommentId = 5_000_000_000L;
        ShouldFail = false;
        ClosedIssueIdentifiers.Clear();
    }

    public Task<IssueDetail> GetIssueAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        if (ShouldFail) throw new HttpRequestException("Fake issue provider failure");
        var issue = Issues.FirstOrDefault(i => i.Identifier == identifier.Value)
            ?? throw new KeyNotFoundException($"Issue {identifier} not found in fake provider");
        return Task.FromResult(issue);
    }

    public Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        if (ShouldFail) throw new HttpRequestException("Fake issue provider failure");

        var filtered = labels is { Count: > 0 }
            ? Issues.Where(i => labels.Any(l => i.Labels.Contains(l))).ToList()
            : Issues;

        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<IssueSummary>
        {
            Items = paged.Select(i => new IssueSummary { Identifier = i.Identifier, Title = i.Title, Labels = i.Labels, Description = i.Description }).ToList(),
            Page = page,
            PageSize = pageSize,
            HasMore = filtered.Count > page * pageSize
        });
    }

    public Task<IReadOnlyList<IssueComment>> ListCommentsAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        if (!_comments.TryGetValue(identifier.Value, out var list))
            return Task.FromResult<IReadOnlyList<IssueComment>>(Array.Empty<IssueComment>());
        // TODO [WARNING]: Sort is by CreatedAt only. PostCommentAsync stamps DateTime.UtcNow, which can return
        // identical ticks for rapid successive posts on Linux (coarse clock resolution). The sort becomes
        // unstable under those conditions. Consider adding a secondary tiebreaker (e.g. insertion index) so
        // "oldest-first" order is guaranteed even when two posts land in the same clock tick.
        return Task.FromResult<IReadOnlyList<IssueComment>>(
            list.Select(e => e.Comment).OrderBy(c => c.CreatedAt).ToList());
    }

    public Task<string?> PostCommentAsync(IssueIdentifier identifier, string body, CancellationToken ct)
    {
        var id = ++_nextCommentId;
        var comment = new IssueComment
        {
            Id = id.ToString(),
            Author = "e2e-bot",
            Body = body,
            CreatedAt = DateTime.UtcNow,
        };
        if (!_comments.TryGetValue(identifier.Value, out var list))
            _comments[identifier.Value] = list = new();
        list.Add((comment, null));
        PostedComments.Add((identifier.Value, body));
        // TODO [WARNING]: "test/repo" is hardcoded here but InMemoryRepositoryProvider uses "e2e-org/e2e-repo".
        // Any pipeline code that parses the comment URL to derive repository context will receive an inconsistent
        // repository slug and may break tests on the needs-refinement re-queue path. Align with the repo slug
        // used by the fixture, or make the repository name configurable on InMemoryIssueProvider.
        var url = $"https://github.com/test/repo/issues/{identifier.Value}#issuecomment-{id}";
        return Task.FromResult<string?>(url);
    }

    public Task UpdateCommentAsync(IssueIdentifier issueIdentifier, long commentId, string body, CancellationToken ct)
    {
        if (!_comments.TryGetValue(issueIdentifier.Value, out var list))
            throw new KeyNotFoundException($"No comments for issue {issueIdentifier}");
        var idx = list.FindIndex(e => e.Comment.Id == commentId.ToString());
        if (idx < 0)
            throw new KeyNotFoundException($"Comment {commentId} not found on issue {issueIdentifier}");
        var (old, _) = list[idx];
        var updated = new IssueComment { Id = old.Id, Author = old.Author, Body = body, CreatedAt = old.CreatedAt };
        list[idx] = (updated, DateTime.UtcNow);
        UpdatedComments.Add((issueIdentifier.Value, commentId, body));
        return Task.CompletedTask;
    }

    public Task AddLabelsAsync(IssueIdentifier identifier, IReadOnlyList<string> labels, CancellationToken ct)
    {
        var idx = Issues.FindIndex(i => i.Identifier == identifier.Value);
        if (idx >= 0)
        {
            var existing = Issues[idx];
            var updated = new HashSet<string>(existing.Labels);
            updated.UnionWith(labels);
            Issues[idx] = new IssueDetail
            {
                Description = existing.Description,
                Identifier = existing.Identifier,
                Labels = updated.ToList(),
                Title = existing.Title,
                Images = existing.Images,
                Url = existing.Url,
            };
        }
        foreach (var label in labels)
            LabelChanges.Add((identifier.Value, label, true));
        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(IssueIdentifier identifier, string label, CancellationToken ct)
    {
        var idx = Issues.FindIndex(i => i.Identifier == identifier.Value);
        if (idx >= 0)
        {
            var existing = Issues[idx];
            var updatedLabels = existing.Labels.Where(l => l != label).ToList();
            Issues[idx] = new IssueDetail
            {
                Description = existing.Description,
                Identifier = existing.Identifier,
                Labels = updatedLabels,
                Title = existing.Title,
                Images = existing.Images,
                Url = existing.Url,
            };
        }
        LabelChanges.Add((identifier.Value, label, false));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Seeds a comment with an explicit ID and CreatedAt for deterministic ordering tests.
    /// Use this instead of PostCommentAsync when you need control over comment ordering.
    /// </summary>
    public void SeedComment(string issueIdentifier, long commentId, string body, string author, DateTime createdAt)
    {
        var comment = new IssueComment { Id = commentId.ToString(), Author = author, Body = body, CreatedAt = createdAt };
        if (!_comments.TryGetValue(issueIdentifier, out var list))
            _comments[issueIdentifier] = list = new();
        list.Add((comment, null));
    }

    public List<(string Title, string Body, IReadOnlyList<string>? Labels)> CreatedIssues { get; } = new();

    public Task<CreatedIssueResult> CreateIssueAsync(string title, string body, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        if (ShouldFail) throw new HttpRequestException("Fake issue provider failure");
        CreatedIssues.Add((title, body, labels));
        var number = (Issues.Count + CreatedIssues.Count).ToString();
        return Task.FromResult(new CreatedIssueResult
        {
            Identifier = number,
            Url = $"https://github.com/test/repo/issues/{number}"
        });
    }

    public Task CloseIssueAsync(IssueIdentifier identifier, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> IsIssueClosedAsync(IssueIdentifier identifier, CancellationToken ct)
        => Task.FromResult(ClosedIssueIdentifiers.Contains(identifier.Value));
    public Task<bool> HasAgentLabelsAsync(CancellationToken ct) => Task.FromResult(true);
    public Task<bool> EnsureAgentLabelsAsync(CancellationToken ct) => Task.FromResult(true);
    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListRepositoryLabelsAsync(CancellationToken ct)
    {
        var labels = Issues.SelectMany(i => i.Labels).Distinct().OrderBy(l => l).ToList();
        return Task.FromResult<IReadOnlyList<string>>(labels);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

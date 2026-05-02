using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

/// <summary>
/// In-memory issue provider for E2E tests. Tracks all side effects (comments, labels).
/// </summary>
public sealed class InMemoryIssueProvider : IIssueProvider
{
    public List<IssueDetail> Issues { get; } = new();
    public List<(string Identifier, string Body)> PostedComments { get; } = new();
    public List<(string Identifier, string Label, bool Added)> LabelChanges { get; } = new();
    public bool ShouldFail { get; set; }

    public IssueProviderType ProviderType => IssueProviderType.GitHub;

    public void Reset()
    {
        Issues.Clear();
        PostedComments.Clear();
        LabelChanges.Clear();
        ShouldFail = false;
    }

    public Task<IssueDetail> GetIssueAsync(string identifier, CancellationToken ct)
    {
        if (ShouldFail) throw new HttpRequestException("Fake issue provider failure");
        var issue = Issues.FirstOrDefault(i => i.Identifier == identifier)
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
            Items = paged.Select(i => new IssueSummary { Identifier = i.Identifier, Title = i.Title, Labels = i.Labels }).ToList(),
            Page = page,
            PageSize = pageSize,
            HasMore = filtered.Count > page * pageSize
        });
    }

    public Task<IReadOnlyList<IssueComment>> ListCommentsAsync(string identifier, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IssueComment>>(new List<IssueComment>());

    public Task PostCommentAsync(string identifier, string body, CancellationToken ct)
    {
        PostedComments.Add((identifier, body));
        return Task.CompletedTask;
    }

    public Task UpdateCommentAsync(string issueIdentifier, string commentId, string body, CancellationToken ct) =>
        Task.CompletedTask;

    public Task AddLabelsAsync(string identifier, IReadOnlyList<string> labels, CancellationToken ct)
    {
        foreach (var label in labels)
            LabelChanges.Add((identifier, label, true));
        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(string identifier, string label, CancellationToken ct)
    {
        LabelChanges.Add((identifier, label, false));
        return Task.CompletedTask;
    }

    public Task CloseIssueAsync(string identifier, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> HasAgentLabelsAsync(CancellationToken ct) => Task.FromResult(true);
    public Task<bool> EnsureAgentLabelsAsync(CancellationToken ct) => Task.FromResult(true);
    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

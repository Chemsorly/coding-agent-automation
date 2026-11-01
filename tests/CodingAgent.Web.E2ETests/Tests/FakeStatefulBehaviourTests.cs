using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless tests that pin the stateful contract of <see cref="Fakes.InMemoryIssueProvider"/>
/// and <see cref="Fakes.InMemoryRepositoryProvider"/> in isolation — no pipeline running.
/// These tests exercise label mutation, comment storage, comment editing, and Reset() coverage.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class FakeStatefulBehaviourTests : HeadlessE2ETestBase
{
    public FakeStatefulBehaviourTests(E2EFixture fixture) : base(fixture) { }

    // ── Issue label state ────────────────────────────────────────────────

    [Fact]
    public async Task AddLabels_GetIssueAsync_ReturnsUpdatedLabels()
    {
        // Arrange
        const string issueId = "1001";
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Test issue",
            Description = "desc",
            Labels = new[] { "agent:next" },
        });

        // Act
        await Fixture.IssueProvider.AddLabelsAsync(issueId, new[] { "agent:in-progress" }, CancellationToken.None);

        // Assert — GetIssueAsync reflects the new label set
        var detail = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        Assert.Contains("agent:next", detail.Labels);
        Assert.Contains("agent:in-progress", detail.Labels);

        // LabelChanges log is still updated
        Assert.Contains(Fixture.IssueProvider.LabelChanges,
            c => c.Identifier == issueId && c.Label == "agent:in-progress" && c.Added);
    }

    [Fact]
    public async Task RemoveLabel_GetIssueAsync_ReturnsUpdatedLabels()
    {
        // Arrange
        const string issueId = "1002";
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Test issue",
            Description = "desc",
            Labels = new[] { "agent:next", "agent:in-progress" },
        });

        // Act
        await Fixture.IssueProvider.RemoveLabelAsync(issueId, "agent:next", CancellationToken.None);

        // Assert — GetIssueAsync no longer contains the removed label
        var detail = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        Assert.DoesNotContain("agent:next", detail.Labels);
        Assert.Contains("agent:in-progress", detail.Labels);

        // LabelChanges log records the removal
        Assert.Contains(Fixture.IssueProvider.LabelChanges,
            c => c.Identifier == issueId && c.Label == "agent:next" && !c.Added);
    }

    [Fact]
    public async Task AddLabels_ListOpenIssuesAsync_ReturnsUpdatedLabels()
    {
        // Arrange — issue starts with only "agent:next"
        const string issueId = "1003";
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Test issue",
            Description = "desc",
            Labels = new[] { "agent:next" },
        });

        // Act
        await Fixture.IssueProvider.AddLabelsAsync(issueId, new[] { "agent:in-progress" }, CancellationToken.None);

        // Assert — ListOpenIssuesAsync projects IssueDetail on-demand; filtering runs against the
        // mutated Issues list, so the issue is returned when filtered by the new label.
        var result = await Fixture.IssueProvider.ListOpenIssuesAsync(1, 10, new[] { "agent:in-progress" }, CancellationToken.None);
        Assert.Contains(result.Items, i => i.Identifier == issueId);
        var item = result.Items.Single(i => i.Identifier == issueId);
        Assert.Contains("agent:in-progress", item.Labels);
        Assert.Contains("agent:next", item.Labels);
    }

    // ── Comment state ────────────────────────────────────────────────────

    [Fact]
    public async Task PostComment_ListCommentsAsync_ReturnsPostedComment()
    {
        // Arrange
        const string issueId = "2001";
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Test issue",
            Description = "desc",
            Labels = Array.Empty<string>(),
        });

        // Act
        var url = await Fixture.IssueProvider.PostCommentAsync(issueId, "hello", CancellationToken.None);

        // Assert — URL contains the comment anchor
        Assert.NotNull(url);
        Assert.Contains("#issuecomment-", url);

        // ListCommentsAsync returns the stored comment
        var comments = await Fixture.IssueProvider.ListCommentsAsync(issueId, CancellationToken.None);
        Assert.Single(comments);
        Assert.Equal("hello", comments[0].Body);
        Assert.Equal("e2e-bot", comments[0].Author);
        Assert.True(long.Parse(comments[0].Id) > int.MaxValue);

        // PostedComments log is still populated
        Assert.Contains(Fixture.IssueProvider.PostedComments, c => c.Identifier == issueId && c.Body == "hello");
    }

    [Fact]
    public async Task PostMultipleComments_ListCommentsAsync_ReturnsOldestFirst()
    {
        // Arrange — use SeedComment with explicit CreatedAt for deterministic ordering;
        // DateTime.UtcNow can return the same value for rapid sequential calls on Linux.
        const string issueId = "2002";
        var yesterday = DateTime.UtcNow.AddDays(-1);
        var today = DateTime.UtcNow;

        Fixture.IssueProvider.SeedComment(issueId, commentId: 9000000001L, body: "older comment", author: "e2e-bot", createdAt: yesterday);
        Fixture.IssueProvider.SeedComment(issueId, commentId: 9000000002L, body: "newer comment", author: "e2e-bot", createdAt: today);

        // Act
        var comments = await Fixture.IssueProvider.ListCommentsAsync(issueId, CancellationToken.None);

        // Assert — oldest first
        Assert.Equal(2, comments.Count);
        Assert.Equal("older comment", comments[0].Body);
        Assert.Equal("newer comment", comments[1].Body);
        Assert.True(comments[0].CreatedAt <= comments[1].CreatedAt);
    }

    [Fact]
    public async Task UpdateCommentAsync_ChangesStoredBody()
    {
        // Arrange — post a comment and extract the id from the returned URL
        const string issueId = "2003";
        var url = await Fixture.IssueProvider.PostCommentAsync(issueId, "original body", CancellationToken.None);
        Assert.NotNull(url);

        // Extract comment id from URL: "…#issuecomment-{id}"
        var commentId = long.Parse(url!.Split('#').Last().Replace("issuecomment-", ""));

        // Act
        await Fixture.IssueProvider.UpdateCommentAsync(issueId, commentId, "updated body", CancellationToken.None);

        // Assert — ListCommentsAsync reflects the updated body
        var comments = await Fixture.IssueProvider.ListCommentsAsync(issueId, CancellationToken.None);
        Assert.Single(comments);
        Assert.Equal("updated body", comments[0].Body);

        // UpdatedComments log is populated
        Assert.Contains(Fixture.IssueProvider.UpdatedComments,
            u => u.Identifier == issueId && u.CommentId == commentId && u.NewBody == "updated body");
    }

    [Fact]
    public async Task UpdateCommentAsync_UnknownId_ThrowsKeyNotFoundException()
    {
        // Arrange — post one comment so the issue has a comment entry
        const string issueId = "2004";
        await Fixture.IssueProvider.PostCommentAsync(issueId, "some comment", CancellationToken.None);

        // Act & Assert — a non-existent comment id must throw
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Fixture.IssueProvider.UpdateCommentAsync(issueId, 99_999_999_999L, "irrelevant", CancellationToken.None));
    }

    [Fact]
    public async Task SeedComment_ListCommentsAsync_ReturnsSeededComment()
    {
        // Arrange
        const string issueId = "2005";
        var yesterday = DateTime.UtcNow.AddDays(-1);

        Fixture.IssueProvider.SeedComment(issueId, commentId: 9999999999L, body: "old comment", author: "e2e-bot", createdAt: yesterday);

        // Post a newer comment via PostCommentAsync
        await Fixture.IssueProvider.PostCommentAsync(issueId, "new comment", CancellationToken.None);

        // Act
        var comments = await Fixture.IssueProvider.ListCommentsAsync(issueId, CancellationToken.None);

        // Assert — both comments returned; seeded (older) one comes first
        Assert.Equal(2, comments.Count);
        Assert.Equal("old comment", comments[0].Body);
        Assert.Equal("9999999999", comments[0].Id);
        Assert.Equal("new comment", comments[1].Body);
    }

    [Fact]
    public async Task Reset_ClearsAllCommentState()
    {
        // Arrange — set up comment and label state
        const string issueId = "3001";
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Test issue",
            Description = "desc",
            Labels = new[] { "agent:next" },
        });
        var url = await Fixture.IssueProvider.PostCommentAsync(issueId, "a comment", CancellationToken.None);
        var commentId = long.Parse(url!.Split('#').Last().Replace("issuecomment-", ""));
        await Fixture.IssueProvider.AddLabelsAsync(issueId, new[] { "agent:in-progress" }, CancellationToken.None);
        await Fixture.IssueProvider.UpdateCommentAsync(issueId, commentId, "edited", CancellationToken.None);

        // Sanity-check state was set
        Assert.NotEmpty(Fixture.IssueProvider.PostedComments);
        Assert.NotEmpty(Fixture.IssueProvider.LabelChanges);
        Assert.NotEmpty(Fixture.IssueProvider.UpdatedComments);

        // Act
        Fixture.IssueProvider.Reset();

        // Assert — all collections cleared
        Assert.Empty(Fixture.IssueProvider.PostedComments);
        Assert.Empty(Fixture.IssueProvider.LabelChanges);
        Assert.Empty(Fixture.IssueProvider.UpdatedComments);
        Assert.Empty(Fixture.IssueProvider.Issues);

        // Comments for the issue are gone
        var comments = await Fixture.IssueProvider.ListCommentsAsync(issueId, CancellationToken.None);
        Assert.Empty(comments);
    }

    // ── PR label state ───────────────────────────────────────────────────

    [Fact]
    public async Task AddPrLabel_ListOpenPullRequestsAsync_ReturnsUpdatedLabels()
    {
        // Arrange
        const int prNumber = 101;
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = prNumber.ToString(),
            Title = "Test PR",
            Description = "PR body",
            Labels = new[] { "agent:next" },
            BranchName = "feature/test",
            TargetBranch = "main",
            Url = $"https://github.com/e2e-org/e2e-repo/pull/{prNumber}",
            IsDraft = false,
        });

        // Act
        await Fixture.RepositoryProvider.AddPrLabelAsync(prNumber, "agent:in-progress", CancellationToken.None);

        // Assert — ListOpenPullRequestsAsync returns the PR with the new label
        var result = await Fixture.RepositoryProvider.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None);
        var pr = result.Items.Single(p => p.Number == prNumber);
        Assert.Contains("agent:next", pr.Labels);
        Assert.Contains("agent:in-progress", pr.Labels);

        // PrLabelChanges log is still populated
        Assert.Contains(Fixture.RepositoryProvider.PrLabelChanges,
            c => c.Action == "Add" && c.PrNumber == prNumber && c.Label == "agent:in-progress");
    }

    [Fact]
    public async Task RemovePrLabel_ListOpenPullRequestsAsync_ReturnsUpdatedLabels()
    {
        // Arrange
        const int prNumber = 102;
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = prNumber.ToString(),
            Title = "Test PR",
            Description = "PR body",
            Labels = new[] { "agent:next", "agent:in-progress" },
            BranchName = "feature/test",
            TargetBranch = "main",
            Url = $"https://github.com/e2e-org/e2e-repo/pull/{prNumber}",
            IsDraft = false,
        });

        // Act
        await Fixture.RepositoryProvider.RemovePrLabelAsync(prNumber, "agent:next", CancellationToken.None);

        // Assert — ListOpenPullRequestsAsync no longer returns "agent:next" on the PR
        var result = await Fixture.RepositoryProvider.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None);
        var pr = result.Items.Single(p => p.Number == prNumber);
        Assert.DoesNotContain("agent:next", pr.Labels);
        Assert.Contains("agent:in-progress", pr.Labels);

        // PrLabelChanges log records the removal
        Assert.Contains(Fixture.RepositoryProvider.PrLabelChanges,
            c => c.Action == "Remove" && c.PrNumber == prNumber && c.Label == "agent:next");
    }
}

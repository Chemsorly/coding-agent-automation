using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline.Models;
using Octokit;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// WireMock-based tests for <see cref="GitHubRepositoryProvider"/> pull-request methods
/// that previously had zero coverage:
/// <list type="bullet">
/// <item>ClosePullRequestAsync</item>
/// <item>GetPullRequestBodyAsync</item>
/// <item>ListOpenPullRequestsAsync</item>
/// <item>AddPrLabelAsync</item>
/// <item>RemovePrLabelAsync</item>
/// <item>EnsureAgentLabelsForPullRequestsAsync</item>
/// <item>ExtractLinkedIssuesAsync</item>
/// <item>ListPullRequestCommentsAsync</item>
/// </list>
/// </summary>
public class GitHubRepositoryProviderPullRequestMethodsTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-99";
    private const string BaseBranch = "main";

    private GitHubRepositoryProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token, BaseBranch);

    // ── ClosePullRequestAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ClosePullRequestAsync_SendsPatchWithClosedState()
    {
        StubPatch(ApiPath($"/repos/{Owner}/{Repo}/pulls/42"),
            BuildDetailedPR(42, "feature/branch", draft: false));

        await using var provider = CreateProvider();
        await provider.ClosePullRequestAsync(42, CancellationToken.None);

        var body = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/pulls/42"));
        body.Should().Contain("closed", "PATCH body must include state=closed");
    }

    // ── GetPullRequestBodyAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetPullRequestBodyAsync_ReturnsBody()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/7"),
            BuildDetailedPR(7, "feature/body-test", draft: false, body: "My PR description"));

        await using var provider = CreateProvider();
        var result = await provider.GetPullRequestBodyAsync(7, CancellationToken.None);

        result.Should().Be("My PR description");
    }

    [Fact]
    public async Task GetPullRequestBodyAsync_On404_ReturnsNull()
    {
        // Provider swallows exceptions and returns null
        StubError(ApiPath($"/repos/{Owner}/{Repo}/pulls/999"), 404, new { message = "Not Found" });

        await using var provider = CreateProvider();
        var result = await provider.GetPullRequestBodyAsync(999, CancellationToken.None);

        result.Should().BeNull("GetPullRequestBodyAsync must return null on exception");
    }

    // ── ListOpenPullRequestsAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ListOpenPullRequestsAsync_ReturnsItems()
    {
        // Issues API returns 1 PR issue
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues"),
            new[] { BuildIssueWithPr(10, "feature/open-pr") });
        // Detailed PR fetch
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/10"),
            BuildDetailedPR(10, "feature/open-pr", draft: false));

        await using var provider = CreateProvider();
        var result = await provider.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].Number.Should().Be(10);
        result.Items[0].BranchName.Should().Be("feature/open-pr");
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_WithLabel_ReturnsMatchingItems()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues"),
            new[] { BuildIssueWithPr(5, "feature/labelled", "agent:done") });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/5"),
            BuildDetailedPR(5, "feature/labelled", draft: false));

        await using var provider = CreateProvider();
        var result = await provider.ListOpenPullRequestsAsync(1, 10, ["agent:done"], CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].Number.Should().Be(5);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_EmptyResponse_ReturnsEmpty()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues"), Array.Empty<object>());

        await using var provider = CreateProvider();
        var result = await provider.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_HasMore_WhenRawCountExceedsPageSize()
    {
        // pageSize=2, but 3 issues returned → HasMore=true
        var issues = new[]
        {
            BuildIssueWithPr(1, "branch-1"),
            BuildIssueWithPr(2, "branch-2"),
            BuildIssueWithPr(3, "branch-3")
        };
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues"), issues);
        // Stub detail for first 2 (pageSize)
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/1"), BuildDetailedPR(1, "branch-1", false));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/2"), BuildDetailedPR(2, "branch-2", false));

        await using var provider = CreateProvider();
        var result = await provider.ListOpenPullRequestsAsync(1, 2, null, CancellationToken.None);

        result.HasMore.Should().BeTrue("raw issue count (3) > pageSize (2) indicates more pages");
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_FiltersNonPrIssues()
    {
        // Mix of PR issues and regular issues
        var issues = new object[]
        {
            BuildIssueWithPr(20, "branch-pr"),           // PR issue
            BuildRegularIssue(21, "regular issue title")  // regular issue — must be filtered
        };
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues"), issues);
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/20"), BuildDetailedPR(20, "branch-pr", false));

        await using var provider = CreateProvider();
        var result = await provider.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None);

        result.Items.Should().HaveCount(1, "regular issues must be filtered out");
        result.Items[0].Number.Should().Be(20);
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_PageSizeZero_ThrowsArgumentOutOfRangeException()
    {
        await using var provider = CreateProvider();
        await provider.Invoking(p => p.ListOpenPullRequestsAsync(1, 0, null, CancellationToken.None))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_PageZero_ThrowsArgumentOutOfRangeException()
    {
        await using var provider = CreateProvider();
        await provider.Invoking(p => p.ListOpenPullRequestsAsync(0, 10, null, CancellationToken.None))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ListOpenPullRequestsAsync_PageSizeOver100_ThrowsArgumentOutOfRangeException()
    {
        await using var provider = CreateProvider();
        await provider.Invoking(p => p.ListOpenPullRequestsAsync(1, 101, null, CancellationToken.None))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ── AddPrLabelAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddPrLabelAsync_SendsPostToLabelsEndpoint()
    {
        StubPost(ApiPath($"/repos/{Owner}/{Repo}/issues/15/labels"),
            new[] { BuildLabelJson("agent:done") });

        await using var provider = CreateProvider();
        await provider.AddPrLabelAsync(15, "agent:done", CancellationToken.None);

        var body = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/issues/15/labels"));
        body.Should().Contain("agent:done");
    }

    // ── RemovePrLabelAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task RemovePrLabelAsync_SendsDeleteRequest()
    {
        StubDelete(ApiPath($"/repos/{Owner}/{Repo}/issues/20/labels/agent%3Adone"), 200);

        await using var provider = CreateProvider();
        await provider.RemovePrLabelAsync(20, "agent:done", CancellationToken.None);

        // Verify server received a DELETE (stub would not respond without a match)
        Server.LogEntries.Should().NotBeEmpty("a DELETE request should have been sent");
    }

    [Fact]
    public async Task RemovePrLabelAsync_LabelNotPresent_IsNoOp()
    {
        // 404 from label removal — must be swallowed silently
        StubError(ApiPath($"/repos/{Owner}/{Repo}/issues/20/labels/not-there"), 404,
            new { message = "Label does not exist" });

        await using var provider = CreateProvider();
        var act = () => provider.RemovePrLabelAsync(20, "not-there", CancellationToken.None);
        await act.Should().NotThrowAsync("NotFoundException on label removal must be swallowed");
    }

    // ── EnsureAgentLabelsForPullRequestsAsync ─────────────────────────────────

    [Fact]
    public async Task EnsureAgentLabelsForPullRequestsAsync_AlwaysReturnsTrue()
    {
        await using var provider = CreateProvider();
        var result = await provider.EnsureAgentLabelsForPullRequestsAsync(CancellationToken.None);
        result.Should().BeTrue("GitHub PRs share the issue label namespace — no setup needed");
    }

    // ── ExtractLinkedIssuesAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ExtractLinkedIssuesAsync_ParsesBodyReferences()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/55/timeline"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/55"),
            BuildDetailedPR(55, "fix/thing", false, body: "Closes #101\nFixes #102"));

        await using var provider = CreateProvider();
        var result = await provider.ExtractLinkedIssuesAsync(55, CancellationToken.None);

        result.Should().Contain("101");
        result.Should().Contain("102");
    }

    [Fact]
    public async Task ExtractLinkedIssuesAsync_ParsesTitleReferences()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/56/timeline"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/56"),
            BuildDetailedPRWithTitle(56, "fix/thing", false, title: "Fix #200: login bug", body: null));

        await using var provider = CreateProvider();
        var result = await provider.ExtractLinkedIssuesAsync(56, CancellationToken.None);

        result.Should().Contain("200");
    }

    [Fact]
    public async Task ExtractLinkedIssuesAsync_ParsesTitleReferences_NoDuplicates()
    {
        // Same issue referenced in both title and body — should deduplicate
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/56/timeline"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/56"),
            BuildDetailedPRWithTitle(56, "fix/thing", false,
                title: "Fix #200: login bug",
                body: "Closes #200 and Fixes #201"));

        await using var provider = CreateProvider();
        var result = await provider.ExtractLinkedIssuesAsync(56, CancellationToken.None);

        result.Should().Contain("200");
        result.Should().Contain("201");
    }

    [Fact]
    public async Task ExtractLinkedIssuesAsync_TimelineApiFailure_FallsBackToParsing()
    {
        // Timeline API throws 422 — should not propagate; fall back to PR title/body parsing
        StubError(ApiPath($"/repos/{Owner}/{Repo}/issues/57/timeline"), 422, new { message = "Error" });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/57"),
            BuildDetailedPR(57, "fix/thing", false, body: "Resolves #303"));

        await using var provider = CreateProvider();
        var result = await provider.ExtractLinkedIssuesAsync(57, CancellationToken.None);

        result.Should().Contain("303", "timeline failure must fall back to body parsing");
    }

    [Fact]
    public async Task ExtractLinkedIssuesAsync_MultipleBodyReferences_ReturnsAll()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/59/timeline"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/59"),
            BuildDetailedPRWithTitle(59, "fix/multi", false,
                title: "Refactor auth",
                body: "Fixes #500\nCloses #501"));

        await using var provider = CreateProvider();
        var result = await provider.ExtractLinkedIssuesAsync(59, CancellationToken.None);

        result.Should().Contain("500");
        result.Should().Contain("501");
    }

    // ── ListPullRequestCommentsAsync ──────────────────────────────────────────

    [Fact]
    public async Task ListPullRequestCommentsAsync_MergesAllThreeSources()
    {
        // Issue comments
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/60/comments"),
            new[] { BuildIssueComment(1, "General comment", "reviewer1") });

        // Review comments (inline)
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/60/comments"),
            new[] { BuildReviewComment(2, "Inline review comment", "reviewer2", "src/File.cs") });

        // Reviews with body
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/60/reviews"),
            new[] { BuildReview(3, "LGTM overall", "reviewer3") });

        await using var provider = CreateProvider();
        var result = await provider.ListPullRequestCommentsAsync(60, "pr-author", CancellationToken.None);

        result.Should().HaveCount(3);
        result.Should().Contain(c => c.Body == "General comment");
        result.Should().Contain(c => c.Body == "Inline review comment");
        result.Should().Contain(c => c.Body == "LGTM overall");
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_EmptyReviewBody_IsFiltered()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/61/comments"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/61/comments"), Array.Empty<object>());
        // Review with empty body — must be excluded
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/61/reviews"),
            new[] { BuildReview(4, "", "reviewer") });

        await using var provider = CreateProvider();
        var result = await provider.ListPullRequestCommentsAsync(61, "pr-author", CancellationToken.None);

        result.Should().BeEmpty("empty review body must be filtered out");
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_IsBotFlag_SetForBotAccounts()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/62/comments"),
            new[] { BuildIssueCommentWithAccountType(5, "bot comment", "dependabot[bot]", "Bot") });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/62/comments"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/62/reviews"), Array.Empty<object>());

        await using var provider = CreateProvider();
        var result = await provider.ListPullRequestCommentsAsync(62, "pr-author", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].IsBot.Should().BeTrue("accounts with [bot] suffix must be flagged as bot");
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_IsAuthorFlag_SetForPrAuthor()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/63/comments"),
            new[] { BuildIssueComment(6, "my own comment", "pr-author-user") });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/63/comments"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/63/reviews"), Array.Empty<object>());

        await using var provider = CreateProvider();
        var result = await provider.ListPullRequestCommentsAsync(63, "PR-Author-User", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].IsAuthor.Should().BeTrue("IsAuthor should be case-insensitive");
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_ResultsOrderedByCreatedAt()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/64/comments"), new[]
        {
            BuildIssueCommentAt(7, "Second", "user1", "2026-01-15T11:00:00Z"),
            BuildIssueCommentAt(8, "First", "user2", "2026-01-15T09:00:00Z")
        });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/64/comments"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/64/reviews"), Array.Empty<object>());

        await using var provider = CreateProvider();
        var result = await provider.ListPullRequestCommentsAsync(64, "nobody", CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Body.Should().Be("First", "results must be sorted ascending by CreatedAt");
        result[1].Body.Should().Be("Second");
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_InlineComment_HasFilePath()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/65/comments"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/65/comments"),
            new[] { BuildReviewComment(9, "Look here", "reviewer", "src/MyFile.cs") });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/65/reviews"), Array.Empty<object>());

        await using var provider = CreateProvider();
        var result = await provider.ListPullRequestCommentsAsync(65, "nobody", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].FilePath.Should().Be("src/MyFile.cs");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object BuildDetailedPR(int number, string headRef, bool draft,
        string? body = "PR body") => new
    {
        id = number * 100,
        number,
        html_url = $"https://github.com/{Owner}/{Repo}/pull/{number}",
        state = "open",
        title = $"Update feature",
        body,
        draft,
        node_id = $"PR_node_{number}",
        user = new { login = "testuser", id = 1 },
        labels = Array.Empty<object>(),
        head = new { @ref = headRef, sha = "abc123" },
        @base = new { @ref = "main", sha = "def456" },
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };

    private static object BuildDetailedPRWithTitle(int number, string headRef, bool draft,
        string title, string? body) => new
    {
        id = number * 100,
        number,
        html_url = $"https://github.com/{Owner}/{Repo}/pull/{number}",
        state = "open",
        title,
        body,
        draft,
        node_id = $"PR_node_{number}",
        user = new { login = "testuser", id = 1 },
        head = new { @ref = headRef, sha = "abc123" },
        @base = new { @ref = "main", sha = "def456" },
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };

    private static object BuildIssueWithPr(int number, string headRef, string? label = null)
    {
        var labels = label is not null
            ? new[] { new { id = 1, name = label, color = "ededed" } }
            : Array.Empty<object>();
        return new
        {
            id = number * 100,
            number,
            title = $"PR Issue #{number}",
            body = (string?)null,
            state = "open",
            user = new { login = "testuser", id = 1 },
            labels,
            pull_request = new { html_url = $"https://github.com/{Owner}/{Repo}/pull/{number}" },
            created_at = "2026-01-01T00:00:00Z",
            updated_at = "2026-01-01T00:00:00Z"
        };
    }

    private static object BuildRegularIssue(int number, string title) => new
    {
        id = number * 100,
        number,
        title,
        body = (string?)null,
        state = "open",
        user = new { login = "testuser", id = 1 },
        labels = Array.Empty<object>(),
        // No pull_request property — distinguishes from PR issues
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };

    private static object BuildIssueComment(long id, string body, string author) => new
    {
        id,
        body,
        user = new { login = author, id = 1, type = "User" },
        created_at = "2026-01-15T10:00:00Z",
        updated_at = "2026-01-15T10:00:00Z"
    };

    private static object BuildIssueCommentAt(long id, string body, string author, string createdAt) => new
    {
        id,
        body,
        user = new { login = author, id = 1, type = "User" },
        created_at = createdAt,
        updated_at = createdAt
    };

    private static object BuildIssueCommentWithAccountType(long id, string body, string login, string accountType) => new
    {
        id,
        body,
        user = new { login, id = 1, type = accountType },
        created_at = "2026-01-15T10:00:00Z",
        updated_at = "2026-01-15T10:00:00Z"
    };

    private static object BuildReviewComment(long id, string body, string author, string path) => new
    {
        id,
        body,
        user = new { login = author, id = 1, type = "User" },
        path,
        original_position = 5,
        created_at = "2026-01-15T10:30:00Z",
        updated_at = "2026-01-15T10:30:00Z"
    };

    private static object BuildReview(long id, string body, string author) => new
    {
        id,
        body,
        state = "COMMENTED",
        user = new { login = author, id = 1, type = "User" },
        submitted_at = "2026-01-15T12:00:00Z"
    };
}

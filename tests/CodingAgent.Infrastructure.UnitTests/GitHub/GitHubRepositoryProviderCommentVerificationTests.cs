using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline.Models;
using CodingAgent.Infrastructure;
using Octokit;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// Comments by authors outside the repository: dropped from the rework feedback, and marked
/// <c>IsVerified = false</c> in the PR conversation. Uses WireMock because <c>Octokit.User</c> has no public way
/// to set <c>Type</c>.
/// </summary>
[Collection("GitHubTelemetry")]
public class GitHubRepositoryProviderCommentVerificationTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";
    private const string BaseBranch = "main";
    private const string Branch = "feature/auto-42-abc123";
    private const int PrNumber = 10;

    private GitHubRepositoryProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token, BaseBranch);

    private void StubPermission(string login, string permission) =>
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/collaborators/{login}/permission"),
            new { permission, role_name = permission });

    private int CountPermissionLookups() =>
        Server.LogEntries.Count(e => e.RequestMessage?.Path?.Contains("/collaborators/") == true);

    private void StubComments(object[] inline, object[] issue, object[] reviews)
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/{PrNumber}/comments"), inline);
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/{PrNumber}/comments"), issue);
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/{PrNumber}/reviews"), reviews);
    }

    private void StubAgentPullRequest(object[] inline, object[] issue, object[] reviews)
    {
        StubGet(ApiPath("/search/issues"), new
        {
            total_count = 1,
            incomplete_results = false,
            items = new[] { BuildSearchIssueJson(PrNumber, Branch) }
        });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/{PrNumber}"),
            BuildDetailedPullRequestJson(PrNumber, Branch, false, true));
        StubComments(inline, issue, reviews);
    }

    private async Task<IReadOnlyList<LinkedPullRequest>> GetAgentPullRequestsAsync()
    {
        await using var provider = CreateProvider();
        return await provider.GetAgentPullRequestsAsync("42", CancellationToken.None);
    }

    private static object IssueCommentJson(
        long id, string login, string type, string? association, string body = "A comment",
        string createdAt = "2026-01-15T10:00:00Z") => new
    {
        id,
        body,
        user = new { login, id = 1, type },
        author_association = association,
        created_at = createdAt,
        updated_at = createdAt
    };

    private static object InlineCommentJson(
        long id, string login, string type, string? association, string body = "An inline comment",
        string createdAt = "2026-01-15T10:30:00Z") => new
    {
        id,
        body,
        user = new { login, id = 1, type },
        author_association = association,
        path = "src/file.cs",
        original_position = 5,
        created_at = createdAt,
        updated_at = createdAt
    };

    private static object ReviewJson(
        long id, string login, string type, string? association, string body = "A review body",
        string submittedAt = "2026-01-15T12:00:00Z") => new
    {
        id,
        body,
        state = "COMMENTED",
        user = new { login, id = 1, type },
        author_association = association,
        submitted_at = submittedAt
    };

    private static object BuildSearchIssueJson(int number, string headRef) => new
    {
        id = number * 100,
        number,
        title = $"PR #{number}",
        state = "open",
        user = new { login = "testuser", id = 1 },
        labels = Array.Empty<object>(),
        pull_request = new { html_url = $"https://github.com/test-owner/test-repo/pull/{number}" },
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };

    private static object BuildDetailedPullRequestJson(int number, string headRef, bool draft, bool? mergeable) => new
    {
        id = number * 100,
        number,
        html_url = $"https://github.com/test-owner/test-repo/pull/{number}",
        state = "open",
        title = $"PR #{number}",
        body = "PR body",
        draft,
        mergeable,
        node_id = $"PR_node_{number}",
        user = new { login = "testuser", id = 1 },
        head = new { @ref = headRef, sha = "abc123" },
        @base = new { @ref = "main", sha = "def456" },
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };

    // ─── GetAgentPullRequestsAsync: outside comments are dropped ──────────────

    [Fact]
    public async Task GetAgentPullRequestsAsync_OutsideComment_IsDropped()
    {
        StubAgentPullRequest([], [IssueCommentJson(1, "alice", "User", "NONE")], []);
        StubPermission("alice", "read");

        var result = await GetAgentPullRequestsAsync();

        result.Should().ContainSingle().Which.ReviewComments.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_OutsideInlineComment_IsDropped()
    {
        StubAgentPullRequest([InlineCommentJson(1, "alice", "User", "NONE")], [], []);
        StubPermission("alice", "read");

        var result = await GetAgentPullRequestsAsync();

        result.Should().ContainSingle().Which.ReviewComments.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_OutsideReviewBody_IsDropped()
    {
        StubAgentPullRequest([], [], [ReviewJson(1, "alice", "User", "NONE")]);
        StubPermission("alice", "read");

        var result = await GetAgentPullRequestsAsync();

        result.Should().ContainSingle().Which.ReviewComments.Should().BeEmpty();
    }

    // ─── GetAgentPullRequestsAsync: verified comments are kept ────────────────

    [Fact]
    public async Task GetAgentPullRequestsAsync_MemberComment_IsKept()
    {
        StubAgentPullRequest([], [IssueCommentJson(1, "alice", "User", "MEMBER")], []);

        var result = await GetAgentPullRequestsAsync();

        result.Should().ContainSingle().Which.ReviewComments.Should().ContainSingle()
            .Which.Author.Should().Be("alice");
        CountPermissionLookups().Should().Be(0);
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_BotComment_IsKept()
    {
        StubAgentPullRequest([], [IssueCommentJson(1, "app[bot]", "Bot", "NONE")], []);

        var result = await GetAgentPullRequestsAsync();

        result.Should().ContainSingle().Which.ReviewComments.Should().ContainSingle()
            .Which.Author.Should().Be("app[bot]");
        CountPermissionLookups().Should().Be(0);
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("inline")]
    [InlineData("review")]
    public async Task GetAgentPullRequestsAsync_BotLookalikeLoginOfUserAccount_IsDropped(string arm)
    {
        // The account type decides, never the login: "evil[bot]" is a User with read access.
        const string login = "evil[bot]";
        switch (arm)
        {
            case "inline":
                StubAgentPullRequest([InlineCommentJson(1, login, "User", "NONE")], [], []);
                break;
            case "review":
                StubAgentPullRequest([], [], [ReviewJson(1, login, "User", "NONE")]);
                break;
            default:
                StubAgentPullRequest([], [IssueCommentJson(1, login, "User", "NONE")], []);
                break;
        }
        StubPermission(login, "read");

        var result = await GetAgentPullRequestsAsync();

        result.Should().ContainSingle().Which.ReviewComments.Should().BeEmpty();
        CountPermissionLookups().Should().Be(1, "a [bot] login does not settle verification");
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_PrivateMember_IsKeptThroughLookup()
    {
        StubAgentPullRequest([], [IssueCommentJson(1, "alice", "User", "CONTRIBUTOR")], []);
        StubPermission("alice", "write");

        var result = await GetAgentPullRequestsAsync();

        result.Should().ContainSingle().Which.ReviewComments.Should().ContainSingle();
        CountPermissionLookups().Should().Be(1);
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_ManyOutsideCommentsAndOneMember_KeepsTheMember()
    {
        // 60 outside comments, all older than the member's: the cap of 50 must not drop the member's comment.
        var outside = Enumerable.Range(1, 60)
            .Select(i => IssueCommentJson(
                i, "alice", "User", "NONE", $"Outside {i}",
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i).ToString("yyyy-MM-ddTHH:mm:ssZ")))
            .ToList();
        outside.Add(IssueCommentJson(100, "maintainer", "User", "MEMBER", "From a member", "2026-02-01T00:00:00Z"));
        StubAgentPullRequest([], outside.ToArray(), []);
        StubPermission("alice", "read");

        var result = await GetAgentPullRequestsAsync();

        var comments = result.Should().ContainSingle().Which.ReviewComments;
        comments.Should().ContainSingle().Which.Author.Should().Be("maintainer");
        CountPermissionLookups().Should().Be(1, "the lookup is cached per login");
    }

    // ─── GetAgentPullRequestsAsync: a failed lookup is not "not verified" ─────

    [Fact]
    public async Task GetAgentPullRequestsAsync_LookupFails_Throws()
    {
        StubAgentPullRequest([], [IssueCommentJson(1, "alice", "User", "NONE")], []);
        StubError(ApiPath($"/repos/{Owner}/{Repo}/collaborators/alice/permission"), 500,
            new { message = "Internal Server Error" });

        var act = GetAgentPullRequestsAsync;

        await act.Should().ThrowAsync<ApiException>();
    }

    // ─── ListPullRequestCommentsAsync: IsVerified is set for each source ──────

    [Fact]
    public async Task ListPullRequestCommentsAsync_SetsIsVerifiedForEachSource()
    {
        StubComments(
            [
                InlineCommentJson(1, "maintainer", "User", "MEMBER", "inline member"),
                InlineCommentJson(2, "alice", "User", "NONE", "inline outsider")
            ],
            [
                IssueCommentJson(3, "maintainer", "User", "MEMBER", "issue member"),
                IssueCommentJson(4, "alice", "User", "NONE", "issue outsider")
            ],
            [
                ReviewJson(5, "maintainer", "User", "OWNER", "review member"),
                ReviewJson(6, "alice", "User", "NONE", "review outsider")
            ]);
        StubPermission("alice", "read");

        await using var provider = CreateProvider();
        var result = await provider.ListPullRequestCommentsAsync(PrNumber, "alice", CancellationToken.None);

        result.Should().HaveCount(6);
        foreach (var body in new[] { "inline member", "issue member", "review member" })
        {
            var comment = result.Single(c => c.Body == body);
            comment.IsVerified.Should().BeTrue(body);
            comment.IsBot.Should().BeFalse(body);
            comment.IsAuthor.Should().BeFalse(body);
        }

        foreach (var body in new[] { "inline outsider", "issue outsider", "review outsider" })
        {
            var comment = result.Single(c => c.Body == body);
            comment.IsVerified.Should().BeFalse(body);
            comment.IsBot.Should().BeFalse(body);
            comment.IsAuthor.Should().BeTrue(body, "alice is the PR author: IsAuthor is unchanged by verification");
        }
    }

    [Fact]
    public async Task ListPullRequestCommentsAsync_MannequinAssociation_DoesNotThrow()
    {
        StubComments([], [IssueCommentJson(1, "alice", "User", "MANNEQUIN")], []);
        StubPermission("alice", "none");

        await using var provider = CreateProvider();
        var act = () => provider.ListPullRequestCommentsAsync(PrNumber, "someone", CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().ContainSingle().Which.IsVerified.Should().BeFalse();
    }
}

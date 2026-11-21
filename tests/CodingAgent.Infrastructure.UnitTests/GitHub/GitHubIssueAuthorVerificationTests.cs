using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;
using Octokit;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// The author-verified flag <see cref="GitHubIssueProvider"/> sets on the issues it reads and lists: the same rule
/// as for comments (association OWNER, MEMBER or COLLABORATOR, a bot account, or a write permission looked up once
/// per login), read from the raw REST answer in one request.
/// </summary>
[Collection("GitHubTelemetry")]
public class GitHubIssueAuthorVerificationTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";

    private static string IssuePath => ApiPath($"/repos/{Owner}/{Repo}/issues/42");
    private static string IssuesPath => ApiPath($"/repos/{Owner}/{Repo}/issues");

    private GitHubIssueProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token);

    private void StubPermission(string login, string permission) =>
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/collaborators/{login}/permission"),
            new { permission, role_name = permission });

    private List<string> PermissionLookups() =>
        Server.LogEntries
            .Select(e => e.RequestMessage?.Path ?? string.Empty)
            .Where(path => path.Contains("/collaborators/"))
            .ToList();

    private int RequestsTo(string path) =>
        Server.LogEntries.Count(e => e.RequestMessage?.Path == path);

    private static object IssueJson(
        int number, string login, string? association, string? type = "User", object? pullRequest = null) => new
        {
            id = number * 100,
            number,
            title = $"Issue {number}",
            body = "body",
            state = "open",
            user = new { login, id = 1, type },
            author_association = association,
            labels = Array.Empty<object>(),
            created_at = "2026-01-01T00:00:00Z",
            updated_at = "2026-01-01T00:00:00Z",
            pull_request = pullRequest
        };

    // ─── GetIssueAsync ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("OWNER")]
    [InlineData("MEMBER")]
    [InlineData("COLLABORATOR")]
    public async Task GetIssueAsync_VerifiedAssociation_SetsTheFlagWithoutLookup(string association)
    {
        StubGet(IssuePath, IssueJson(42, "alice", association));

        await using var provider = CreateProvider();
        var detail = await provider.GetIssueAsync("42", CancellationToken.None);

        detail.IsAuthorVerified.Should().BeTrue();
        PermissionLookups().Should().BeEmpty();
    }

    [Fact]
    public async Task GetIssueAsync_BotAuthor_SetsTheFlagWithoutLookup()
    {
        StubGet(IssuePath, IssueJson(42, "pipeline-app[bot]", "NONE", type: "Bot"));

        await using var provider = CreateProvider();
        var detail = await provider.GetIssueAsync("42", CancellationToken.None);

        detail.IsAuthorVerified.Should().BeTrue();
        PermissionLookups().Should().BeEmpty();
    }

    [Fact]
    public async Task GetIssueAsync_PrivateMemberWithWrite_SetsTheFlag()
    {
        StubGet(IssuePath, IssueJson(42, "alice", "CONTRIBUTOR"));
        StubPermission("alice", "write");

        await using var provider = CreateProvider();
        var detail = await provider.GetIssueAsync("42", CancellationToken.None);

        detail.IsAuthorVerified.Should().BeTrue();
        PermissionLookups().Should().ContainSingle();
    }

    [Fact]
    public async Task GetIssueAsync_Outsider_ClearsTheFlag()
    {
        StubGet(IssuePath, IssueJson(42, "mallory", "NONE"));
        StubPermission("mallory", "read");

        await using var provider = CreateProvider();
        var detail = await provider.GetIssueAsync("42", CancellationToken.None);

        detail.IsAuthorVerified.Should().BeFalse();
        PermissionLookups().Should().ContainSingle();
    }

    [Fact]
    public async Task GetIssueAsync_Mannequin_DoesNotThrow()
    {
        StubGet(IssuePath, IssueJson(42, "alice", "MANNEQUIN", type: "Mannequin"));
        StubPermission("alice", "none");

        await using var provider = CreateProvider();
        var act = () => provider.GetIssueAsync("42", CancellationToken.None);

        var detail = await act.Should().NotThrowAsync();
        detail.Subject.IsAuthorVerified.Should().BeFalse();
    }

    [Fact]
    public async Task GetIssueAsync_LookupFails_Throws()
    {
        StubGet(IssuePath, IssueJson(42, "alice", "NONE"));
        StubError(ApiPath($"/repos/{Owner}/{Repo}/collaborators/alice/permission"), 500,
            new { message = "Internal Server Error" });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.GetIssueAsync("42", CancellationToken.None))
            .Should().ThrowAsync<ApiException>();
    }

    [Fact]
    public async Task GetIssueAsync_OnlyOneReadOfTheIssue()
    {
        StubGet(IssuePath, IssueJson(42, "alice", "MEMBER"));

        await using var provider = CreateProvider();
        await provider.GetIssueAsync("42", CancellationToken.None);

        RequestsTo(IssuePath).Should().Be(1);
        Server.LogEntries.Should().HaveCount(1, "the flag comes from the same answer, not from a second read");
    }

    // ─── ListOpenIssuesAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ListOpenIssuesAsync_ReturnsTheFlagPerIssue()
    {
        StubGet(IssuesPath, new[]
        {
            IssueJson(1, "alice", "MEMBER"),
            IssueJson(2, "mallory", "NONE"),
            IssueJson(3, "pipeline-app[bot]", "NONE", type: "Bot"),
            IssueJson(4, "carol", "CONTRIBUTOR")
        });
        StubPermission("mallory", "read");
        StubPermission("carol", "write");

        await using var provider = CreateProvider();
        var page = await provider.ListOpenIssuesAsync(1, 10, CancellationToken.None);

        page.Items.Select(i => (i.Identifier, i.IsAuthorVerified)).Should().Equal(
            ("1", true), ("2", false), ("3", true), ("4", true));
    }

    [Fact]
    public async Task ListOpenIssuesAsync_MakesOneListRequestAndLooksUpEachAuthorOnce()
    {
        StubGet(IssuesPath, new[]
        {
            IssueJson(1, "mallory", "NONE"),
            IssueJson(2, "mallory", "NONE")
        });
        StubPermission("mallory", "read");

        await using var provider = CreateProvider();
        var page = await provider.ListOpenIssuesAsync(1, 10, CancellationToken.None);

        page.Items.Should().HaveCount(2);
        RequestsTo(IssuesPath).Should().Be(1);
        PermissionLookups().Should().ContainSingle();
    }

    [Fact]
    public async Task ListOpenIssuesAsync_DoesNotLookUpPullRequestsOrTheExtraItem()
    {
        // Page size 2 asks GitHub for 3; the stub returns four: issues by a, b and c around a pull request by p.
        // Only the two issues the page returns (a and b) may cost a lookup.
        StubGet(IssuesPath, new[]
        {
            IssueJson(1, "author-a", "NONE"),
            IssueJson(2, "author-p", "NONE", pullRequest: new { url = "https://example.test/pr" }),
            IssueJson(3, "author-b", "NONE"),
            IssueJson(4, "author-c", "NONE")
        });
        foreach (var login in new[] { "author-a", "author-p", "author-b", "author-c" })
            StubPermission(login, "write");

        await using var provider = CreateProvider();
        var page = await provider.ListOpenIssuesAsync(1, 2, CancellationToken.None);

        page.Items.Select(i => i.Identifier).Should().Equal("1", "3");
        page.HasMore.Should().BeTrue();
        PermissionLookups().Should().BeEquivalentTo(
        [
            ApiPath($"/repos/{Owner}/{Repo}/collaborators/author-a/permission"),
            ApiPath($"/repos/{Owner}/{Repo}/collaborators/author-b/permission")
        ]);
    }

    // ─── ListClosedIssuesAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ListClosedIssuesAsync_ReturnsTheFlag()
    {
        StubGet(IssuesPath, new[]
        {
            IssueJson(1, "alice", "OWNER"),
            IssueJson(2, "mallory", "NONE")
        });
        StubPermission("mallory", "read");

        await using var provider = CreateProvider();
        var page = await provider.ListClosedIssuesAsync(1, 10, null, null, CancellationToken.None);

        page.Items.Select(i => (i.Identifier, i.IsAuthorVerified)).Should().Equal(("1", true), ("2", false));
    }
}

using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;
using Octokit;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// The author-verified flag <see cref="GitHubRepositoryProvider"/> sets on the pull requests it reads and lists: the
/// same rule as for comments (association OWNER, MEMBER or COLLABORATOR, a bot account, or a write permission looked
/// up once per login), with the association and account type read from the raw REST answer of the pull request.
/// </summary>
[Collection("GitHubTelemetry")]
public class GitHubPullRequestAuthorVerificationTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";

    private static string PullPath(int number) => ApiPath($"/repos/{Owner}/{Repo}/pulls/{number}");
    private static string IssuesPath => ApiPath($"/repos/{Owner}/{Repo}/issues");
    private static string PermissionPath(string login) => ApiPath($"/repos/{Owner}/{Repo}/collaborators/{login}/permission");

    private GitHubRepositoryProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token, "main");

    private void StubPermission(string login, string permission) =>
        StubGet(PermissionPath(login), new { permission, role_name = permission });

    private List<string> PermissionLookups() =>
        Server.LogEntries
            .Select(e => e.RequestMessage?.Path ?? string.Empty)
            .Where(path => path.Contains("/collaborators/"))
            .ToList();

    private static object PullJson(
        int number, string login, string? association, string? type = "User", string state = "open") => new
        {
            id = number * 100,
            number,
            title = $"Pull request {number}",
            body = "body",
            draft = false,
            state,
            head = new { @ref = "b" },
            @base = new { @ref = "main" },
            html_url = $"https://github.com/{Owner}/{Repo}/pull/{number}",
            user = new { login, type },
            created_at = "2026-01-01T00:00:00Z",
            author_association = association
        };

    private static object IssueWithPullRequest(int number, string login) => new
    {
        id = number * 100,
        number,
        title = $"Pull request {number}",
        body = (string?)null,
        state = "open",
        user = new { login, id = 1 },
        labels = Array.Empty<object>(),
        pull_request = new { html_url = $"https://github.com/{Owner}/{Repo}/pull/{number}" },
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };

    // ─── GetOpenPullRequestAsync ──────────────────────────────────────────────

    [Theory]
    [InlineData("OWNER")]
    [InlineData("MEMBER")]
    [InlineData("COLLABORATOR")]
    public async Task GetOpenPullRequestAsync_VerifiedAssociation_SetsTheFlagWithoutLookup(string association)
    {
        StubGet(PullPath(7), PullJson(7, "alice", association));

        await using var provider = CreateProvider();
        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary.Should().NotBeNull();
        summary!.IsAuthorVerified.Should().BeTrue();
        summary.Author.Should().Be("alice");
        PermissionLookups().Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_AppBotAuthor_SetsTheFlagWithoutLookup()
    {
        // A pull request the pipeline opened through a GitHub App is CONTRIBUTOR with a Bot account.
        StubGet(PullPath(7), PullJson(7, "pipeline-app[bot]", "CONTRIBUTOR", type: "Bot"));

        await using var provider = CreateProvider();
        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary!.IsAuthorVerified.Should().BeTrue();
        PermissionLookups().Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_OrdinaryLoginWithBotAccountType_SetsTheFlagWithoutLookup()
    {
        // The account type decides, not the login: nothing in this login says "bot".
        StubGet(PullPath(7), PullJson(7, "release-helper", "NONE", type: "Bot"));

        await using var provider = CreateProvider();
        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary!.IsAuthorVerified.Should().BeTrue();
        PermissionLookups().Should().BeEmpty();
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_BotSuffixedLoginOfAUserAccount_ClearsTheFlag()
    {
        // A user can pick a login ending in "[bot]" on a platform that allows it: only the account type counts.
        StubGet(PullPath(7), PullJson(7, "pipeline-app[bot]", "CONTRIBUTOR", type: "User"));
        StubPermission("pipeline-app[bot]", "read");

        await using var provider = CreateProvider();
        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary!.IsAuthorVerified.Should().BeFalse();
        PermissionLookups().Should().ContainSingle("a login suffix does not settle the author without a lookup");
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_PrivateMemberWithWrite_SetsTheFlag()
    {
        StubGet(PullPath(7), PullJson(7, "alice", "CONTRIBUTOR"));
        StubPermission("alice", "write");

        await using var provider = CreateProvider();
        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary!.IsAuthorVerified.Should().BeTrue();
        PermissionLookups().Should().ContainSingle();
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_Outsider_ClearsTheFlag()
    {
        StubGet(PullPath(7), PullJson(7, "mallory", "NONE"));
        StubPermission("mallory", "read");

        await using var provider = CreateProvider();
        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary!.IsAuthorVerified.Should().BeFalse();
        PermissionLookups().Should().ContainSingle();
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_Mannequin_DoesNotThrow()
    {
        StubGet(PullPath(7), PullJson(7, "alice", "MANNEQUIN", type: "Mannequin"));
        StubPermission("alice", "none");

        await using var provider = CreateProvider();
        var act = () => provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        var summary = await act.Should().NotThrowAsync();
        summary.Subject!.IsAuthorVerified.Should().BeFalse();
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_AuthorDoesNotExist_ClearsTheFlagAndKeepsThePullRequestOpen()
    {
        // The outer catch of NotFoundException must not see the lookup's 404 and report the open PR as "not open".
        StubGet(PullPath(7), PullJson(7, "ghost", "NONE"));
        StubError(PermissionPath("ghost"), 404, new { message = "Not Found" });

        await using var provider = CreateProvider();
        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary.Should().NotBeNull();
        summary!.IsAuthorVerified.Should().BeFalse();
        PermissionLookups().Should().ContainSingle();
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_LookupFails_Throws()
    {
        StubGet(PullPath(7), PullJson(7, "alice", "NONE"));
        StubError(PermissionPath("alice"), 500, new { message = "Internal Server Error" });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.GetOpenPullRequestAsync(7, CancellationToken.None))
            .Should().ThrowAsync<ApiException>();
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_ClosedPullRequest_DoesNotLookUpTheAuthor()
    {
        StubGet(PullPath(7), PullJson(7, "mallory", "NONE", state: "closed"));
        StubPermission("mallory", "write");

        await using var provider = CreateProvider();
        var summary = await provider.GetOpenPullRequestAsync(7, CancellationToken.None);

        summary.Should().BeNull();
        PermissionLookups().Should().BeEmpty("a pull request that is not open is not read into a summary");
    }

    // ─── ListOpenPullRequestsAsync ────────────────────────────────────────────

    [Fact]
    public async Task ListOpenPullRequestsAsync_ReturnsTheFlagPerPullRequest()
    {
        StubGet(IssuesPath, new[]
        {
            IssueWithPullRequest(1, "alice"),
            IssueWithPullRequest(2, "mallory"),
            IssueWithPullRequest(3, "pipeline-app[bot]"),
            IssueWithPullRequest(4, "carol")
        });
        StubGet(PullPath(1), PullJson(1, "alice", "MEMBER"));
        StubGet(PullPath(2), PullJson(2, "mallory", "NONE"));
        StubGet(PullPath(3), PullJson(3, "pipeline-app[bot]", "CONTRIBUTOR", type: "Bot"));
        StubGet(PullPath(4), PullJson(4, "carol", "CONTRIBUTOR"));
        StubPermission("mallory", "read");
        StubPermission("carol", "write");

        await using var provider = CreateProvider();
        var page = await provider.ListOpenPullRequestsAsync(1, 10, null, CancellationToken.None);

        page.Items.Select(p => (p.Number, p.IsAuthorVerified)).Should().Equal(
            (1, true), (2, false), (3, true), (4, true));
        PermissionLookups().Should().BeEquivalentTo(
        [
            PermissionPath("mallory"),
            PermissionPath("carol")
        ]);
    }
}

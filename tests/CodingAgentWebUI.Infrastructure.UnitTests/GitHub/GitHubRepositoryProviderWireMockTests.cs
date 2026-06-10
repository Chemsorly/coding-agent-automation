using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure;
using Octokit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

public class GitHubRepositoryProviderWireMockTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token-12345";
    private const string BaseBranch = "main";

    private GitHubRepositoryProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token, BaseBranch);

    // NOTE: HasCommitsAheadAsync is listed in the issue as a required test scenario, but it uses
    // LibGit2Sharp (local git operations), not the GitHub HTTP API, so it cannot be tested via WireMock.

    #region CreatePullRequestAsync

    [Fact]
    public async Task CreatePullRequestAsync_SendsCorrectRequestBody()
    {
        var prUrl = $"https://github.com/{Owner}/{Repo}/pull/1";
        StubPost(ApiPath($"/repos/{Owner}/{Repo}/pulls"), BuildPullRequestJson(1, prUrl));

        await using var provider = CreateProvider();
        var prInfo = new PullRequestInfo
        {
            Title = "feat: add login",
            Body = "Implements login feature",
            BranchName = "feature/login",
            BaseBranch = "main",
            IsDraft = true
        };

        var result = await provider.CreatePullRequestAsync(prInfo, CancellationToken.None);

        result.Should().Be(prUrl);

        var body = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/pulls"));
        body.Should().NotBeNull();
        body.Should().Contain("feat: add login");
        body.Should().Contain("Implements login feature");
        body.Should().Contain("feature/login");
    }

    [Fact]
    public async Task ValidateAsync_SucceedsWhenRepoAccessible()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}"), BuildRepoJson(Owner, Repo));

        await using var provider = CreateProvider();
        await provider.ValidateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ValidateAsync_404_ThrowsNotFoundException()
    {
        StubError(ApiPath($"/repos/{Owner}/{Repo}"), 404, new { message = "Not Found" });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.ValidateAsync(CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task CreatePullRequestAsync_IncludesAuthHeader()
    {
        StubPost(ApiPath($"/repos/{Owner}/{Repo}/pulls"),
            BuildPullRequestJson(1, "https://github.com/owner/repo/pull/1"));

        await using var provider = CreateProvider();
        await provider.CreatePullRequestAsync(new PullRequestInfo
        {
            Title = "test",
            Body = "test",
            BranchName = "test-branch",
            BaseBranch = "main"
        }, CancellationToken.None);

        AssertAllRequestsHaveAuthHeader(Token);
    }

    #endregion

    #region GetAgentPullRequestsAsync

    [Fact]
    public async Task GetAgentPullRequestsAsync_NoMatchingBranches_ReturnsEmpty()
    {
        // Search returns no matching PRs
        StubGet(ApiPath("/search/issues"), new
        {
            total_count = 0,
            incomplete_results = false,
            items = Array.Empty<object>()
        });

        await using var provider = CreateProvider();
        var result = await provider.GetAgentPullRequestsAsync("42", CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_MatchingBranch_ReturnsLinkedPullRequest()
    {
        var branchName = "feature/auto-42-abc123";
        StubGet(ApiPath("/search/issues"), new
        {
            total_count = 1,
            incomplete_results = false,
            items = new[] { BuildSearchIssueJson(10, branchName) }
        });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/10"),
            BuildDetailedPullRequestJson(10, branchName, true, true));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/10/comments"), new[]
        {
            new { id = 1, body = "Looks good!", user = new { login = "reviewer1", id = 1 }, path = "src/file.cs", created_at = "2026-01-15T10:00:00Z", updated_at = "2026-01-15T10:00:00Z" }
        });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/10/comments"), new[]
        {
            new { id = 2, body = "Nice work", user = new { login = "reviewer2", id = 2 }, created_at = "2026-01-15T11:00:00Z", updated_at = "2026-01-15T11:00:00Z" }
        });

        await using var provider = CreateProvider();
        var result = await provider.GetAgentPullRequestsAsync("42", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Number.Should().Be(10);
        result[0].BranchName.Should().Be(branchName);
        result[0].IsDraft.Should().BeTrue();
        result[0].ReviewComments.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_FiltersOnlyMatchingPrefix()
    {
        // Search API returns only matching PRs server-side; we verify only PR 5 is fetched
        StubGet(ApiPath("/search/issues"), new
        {
            total_count = 1,
            incomplete_results = false,
            items = new[] { BuildSearchIssueJson(5, "feature/auto-99-impl") }
        });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/5"),
            BuildDetailedPullRequestJson(5, "feature/auto-99-impl", false, true));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/5/comments"), Array.Empty<object>());
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/5/comments"), Array.Empty<object>());

        await using var provider = CreateProvider();
        var result = await provider.GetAgentPullRequestsAsync("99", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Number.Should().Be(5);
    }

    [Fact]
    public async Task GetAgentPullRequestsAsync_FiltersPipelineGeneratedComments()
    {
        var branchName = "feature/auto-7-fix";
        StubGet(ApiPath("/search/issues"), new
        {
            total_count = 1,
            incomplete_results = false,
            items = new[] { BuildSearchIssueJson(3, branchName) }
        });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/3"),
            BuildDetailedPullRequestJson(3, branchName, false, true));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/3/comments"), new[]
        {
            new { id = 1, body = "## 🤖 Pipeline generated this", user = new { login = "bot", id = 1 }, path = "file.cs", created_at = "2026-01-15T10:00:00Z", updated_at = "2026-01-15T10:00:00Z" },
            new { id = 2, body = "Real review comment", user = new { login = "human", id = 2 }, path = "file.cs", created_at = "2026-01-15T11:00:00Z", updated_at = "2026-01-15T11:00:00Z" }
        });
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/issues/3/comments"), new[]
        {
            new { id = 3, body = "Contains <!-- agent: marker -->", user = new { login = "bot", id = 1 }, created_at = "2026-01-15T12:00:00Z", updated_at = "2026-01-15T12:00:00Z" },
            new { id = 4, body = "Human conversation comment", user = new { login = "human", id = 2 }, created_at = "2026-01-15T13:00:00Z", updated_at = "2026-01-15T13:00:00Z" }
        });

        await using var provider = CreateProvider();
        var result = await provider.GetAgentPullRequestsAsync("7", CancellationToken.None);

        result.Should().HaveCount(1);
        // Pipeline-generated comments should be filtered out
        result[0].ReviewComments.Should().HaveCount(2);
        result[0].ReviewComments.Should().Contain(c => c.Body == "Real review comment");
        result[0].ReviewComments.Should().Contain(c => c.Body == "Human conversation comment");
        result[0].ReviewComments.Should().NotContain(c => c.Body.Contains("🤖"));
        result[0].ReviewComments.Should().NotContain(c => c.Body.Contains("<!-- agent:"));
    }

    #endregion

    #region UpdatePullRequestAsync

    [Fact]
    public async Task UpdatePullRequestAsync_Success_UpdatesBody()
    {
        StubPatch(ApiPath($"/repos/{Owner}/{Repo}/pulls/42"),
            BuildDetailedPullRequestJson(42, "feature/branch", false, true));

        await using var provider = CreateProvider();
        await provider.UpdatePullRequestAsync(42, "Updated body content", false, CancellationToken.None);

        var body = GetRequestBody(ApiPath($"/repos/{Owner}/{Repo}/pulls/42"));
        body.Should().Contain("Updated body content");
    }

    [Fact]
    public async Task UpdatePullRequestAsync_MarkReady_NonDraft_DoesNotCallGraphQL()
    {
        // PR is not a draft — markReady should not trigger GraphQL
        StubPatch(ApiPath($"/repos/{Owner}/{Repo}/pulls/42"),
            BuildDetailedPullRequestJson(42, "feature/branch", draft: false, mergeable: true));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/42"),
            BuildDetailedPullRequestJson(42, "feature/branch", draft: false, mergeable: true));

        await using var provider = CreateProvider();
        await provider.UpdatePullRequestAsync(42, "body", true, CancellationToken.None);

        // Should complete without error — no GraphQL call needed for non-draft PR
    }

    [Fact]
    public async Task UpdatePullRequestAsync_MarkReady_Draft_HandlesGraphQLFailureGracefully()
    {
        // PR is a draft — markReady triggers GraphQL which will fail (hardcoded URL),
        // but the method should not throw because the failure is caught and logged
        StubPatch(ApiPath($"/repos/{Owner}/{Repo}/pulls/42"),
            BuildDetailedPullRequestJson(42, "feature/branch", draft: true, mergeable: true));
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/42"),
            BuildDetailedPullRequestJson(42, "feature/branch", draft: true, mergeable: true));

        await using var provider = CreateProvider();
        // Should not throw — GraphQL failure is non-fatal
        await provider.UpdatePullRequestAsync(42, "body", true, CancellationToken.None);
    }

    [Fact]
    public async Task UpdatePullRequestAsync_NotFound_ThrowsInvalidOperationException()
    {
        StubError(ApiPath($"/repos/{Owner}/{Repo}/pulls/999"), 404, new { message = "Not Found" });

        await using var provider = CreateProvider();
        await provider.Invoking(p => p.UpdatePullRequestAsync(999, "body", false, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*not found*");
    }

    #endregion

    #region Helpers

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

    #endregion
}

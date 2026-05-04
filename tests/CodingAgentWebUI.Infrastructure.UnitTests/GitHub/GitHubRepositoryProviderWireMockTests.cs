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
        new(Server.Url!, Token, Owner, Repo, BaseBranch);

    // NOTE: HasCommitsAheadAsync is listed in the issue as a required test scenario, but it uses
    // LibGit2Sharp (local git operations), not the GitHub HTTP API, so it cannot be tested via WireMock.

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
}

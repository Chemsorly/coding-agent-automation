using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline.Interfaces;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// WireMock-based tests for the auto-branch-updater methods on
/// <see cref="GitHubRepositoryProvider"/>: <c>IsPullRequestBehindBaseAsync</c>
/// and <c>UpdatePullRequestBranchAsync</c> (spec 040, task 3.2 / 3.3).
/// </summary>
public class GitHubRepositoryProviderAutoUpdateTests : WireMockTestBase
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";
    private const string Token = "fake-token";
    private const string BaseBranch = "main";

    private GitHubRepositoryProvider CreateProvider() =>
        new(new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token, BaseBranch);

    // ── SupportsServerSideBranchUpdate ────────────────────────────────────────

    [Fact]
    public async Task SupportsServerSideBranchUpdate_ReturnsTrue()
    {
        await using var provider = new GitHubRepositoryProvider(
            new GitHubConnectionInfo(Server.Url!, Owner, Repo), Token, BaseBranch);
        ((IRepositoryProvider)provider).SupportsServerSideBranchUpdate.Should().BeTrue();
    }

    // ── IsPullRequestBehindBaseAsync — mergeable_state mapping ────────────────

    [Theory]
    [InlineData("behind", true)]
    public async Task IsPullRequestBehindBaseAsync_BehindState_ReturnsTrue(string mergeableState, bool? expected)
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/10"), BuildPrWithMergeableState(10, mergeableState));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(10, CancellationToken.None);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("dirty")]
    [InlineData("has_hooks")]
    [InlineData("draft")]
    public async Task IsPullRequestBehindBaseAsync_FalseStates_ReturnsFalse(string mergeableState)
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/11"), BuildPrWithMergeableState(11, mergeableState));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(11, CancellationToken.None);
        result.Should().BeFalse($"mergeable_state='{mergeableState}' should map to false");
    }

    [Theory]
    [InlineData("blocked")]   // CRITICAL: required checks running — must be null, NOT false
    [InlineData("unstable")]  // CRITICAL: non-required checks; required CI may still run
    [InlineData("unknown")]
    public async Task IsPullRequestBehindBaseAsync_NullStates_ReturnsNull(string mergeableState)
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/12"), BuildPrWithMergeableState(12, mergeableState));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(12, CancellationToken.None);
        result.Should().BeNull(
            $"mergeable_state='{mergeableState}' means CI may be running — slot must stay in-flight (null)");
    }

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_NullMergeableState_ReturnsNull()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/13"), BuildPrWithNullMergeableState(13));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(13, CancellationToken.None);
        result.Should().BeNull("null mergeable_state means not yet computed");
    }

    // ── UpdatePullRequestBranchAsync ──────────────────────────────────────────

    [Fact]
    public async Task UpdatePullRequestBranchAsync_On202_DoesNotThrow()
    {
        StubPut(
            ApiPath($"/repos/{Owner}/{Repo}/pulls/20/update-branch"),
            new { message = "Updating pull request branch.", url = "https://api.github.com/repos/test-owner/test-repo/pulls/20" },
            statusCode: 202);

        await using var provider = CreateProvider();
        var exception = await Record.ExceptionAsync(
            () => provider.UpdatePullRequestBranchAsync(20, CancellationToken.None));
        exception.Should().BeNull("202 Accepted is the success response");
    }

    [Fact]
    public async Task UpdatePullRequestBranchAsync_On403_Throws()
    {
        StubError(ApiPath($"/repos/{Owner}/{Repo}/pulls/21/update-branch"), 403,
            new { message = "Must have push access to this repository" });

        await using var provider = CreateProvider();
        await Assert.ThrowsAnyAsync<Exception>(
            () => provider.UpdatePullRequestBranchAsync(21, CancellationToken.None));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object BuildPrWithMergeableState(int number, string mergeableState) => new
    {
        id = number * 100,
        number,
        html_url = $"https://github.com/{Owner}/{Repo}/pull/{number}",
        state = "open",
        title = "Test PR",
        body = "body",
        draft = false,
        node_id = $"PR_node_{number}",
        user = new { login = "testuser", id = 1 },
        labels = Array.Empty<object>(),
        head = new { @ref = "feature-branch", sha = "abc123" },
        @base = new { @ref = "main", sha = "def456" },
        mergeable = true,
        mergeable_state = mergeableState,
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };

    private static object BuildPrWithNullMergeableState(int number) => new
    {
        id = number * 100,
        number,
        html_url = $"https://github.com/{Owner}/{Repo}/pull/{number}",
        state = "open",
        title = "Test PR",
        body = "body",
        draft = false,
        node_id = $"PR_node_{number}",
        user = new { login = "testuser", id = 1 },
        labels = Array.Empty<object>(),
        head = new { @ref = "feature-branch", sha = "abc123" },
        @base = new { @ref = "main", sha = "def456" },
        // mergeable_state intentionally omitted so it deserializes as null
        created_at = "2026-01-01T00:00:00Z",
        updated_at = "2026-01-01T00:00:00Z"
    };
}

using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
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

    // ── IsPullRequestBehindBaseAsync — Behind ─────────────────────────────────

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_Behind_ReturnsBehind()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/10"), BuildPrWithMergeableState(10, "behind"));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(10, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.Behind);
    }

    // ── IsPullRequestBehindBaseAsync — UpToDate states ───────────────────────

    [Theory]
    [InlineData("clean")]
    [InlineData("has_hooks")]
    [InlineData("draft")]
    [InlineData("unstable")]   // non-required checks only — not a conflict or CI-blocker
    public async Task IsPullRequestBehindBaseAsync_UpToDateStates_ReturnsUpToDate(string mergeableState)
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/11"), BuildPrWithMergeableState(11, mergeableState));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(11, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.UpToDate,
            $"mergeable_state='{mergeableState}' should map to UpToDate");
    }

    // ── IsPullRequestBehindBaseAsync — Conflicted ─────────────────────────────

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_Dirty_ReturnsConflicted()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/12"), BuildPrWithMergeableState(12, "dirty"));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(12, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.Conflicted,
            "mergeable_state='dirty' means a merge conflict — trigger rework label swap");
    }

    // ── IsPullRequestBehindBaseAsync — Blocked ────────────────────────────────

    [Theory]
    [InlineData("blocked")]  // CRITICAL: required checks running — must be Blocked, NOT UpToDate/Conflicted
    public async Task IsPullRequestBehindBaseAsync_Blocked_ReturnsBlocked(string mergeableState)
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/13"), BuildPrWithMergeableState(13, mergeableState));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(13, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.Blocked,
            $"mergeable_state='{mergeableState}' means required CI is running — slot must stay in-flight");
    }

    // ── IsPullRequestBehindBaseAsync — Unknown ────────────────────────────────

    [Theory]
    [InlineData("unknown")]
    public async Task IsPullRequestBehindBaseAsync_Unknown_ReturnsUnknown(string mergeableState)
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/14"), BuildPrWithMergeableState(14, mergeableState));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(14, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.Unknown,
            $"mergeable_state='{mergeableState}' means still computing — conservative Unknown");
    }

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_NullMergeableState_ReturnsUnknown()
    {
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/pulls/15"), BuildPrWithNullMergeableState(15));
        await using var provider = CreateProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(15, CancellationToken.None);
        result.Should().Be(PrMergeabilityStatus.Unknown, "null mergeable_state means not yet computed");
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

    // ── ListAgentBranchesAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ListAgentBranchesAsync_ReturnsOnlyAgentBranches()
    {
        // Stub the branches list endpoint with a mix of agent and non-agent branches
        var branches = new[]
        {
            new { name = $"{PipelineConstants.BranchPrefix}123-fix-login", @protected = false },
            new { name = "main", @protected = true },
            new { name = $"{PipelineConstants.BranchPrefix}42-update-deps", @protected = false },
            new { name = "feature/manual-work", @protected = false },
        };
        StubGet(ApiPath($"/repos/{Owner}/{Repo}/branches"), branches);

        await using var provider = CreateProvider();
        var result = await provider.ListAgentBranchesAsync(CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain($"{PipelineConstants.BranchPrefix}123-fix-login");
        result.Should().Contain($"{PipelineConstants.BranchPrefix}42-update-deps");
        result.Should().NotContain("main");
        result.Should().NotContain("feature/manual-work");
    }

    // ── DeleteBranchAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteBranchAsync_On204_DoesNotThrow()
    {
        var branchName = $"{PipelineConstants.BranchPrefix}42-fix-login";
        StubDelete(ApiPath($"/repos/{Owner}/{Repo}/git/refs/heads/{branchName}"), statusCode: 204);

        await using var provider = CreateProvider();
        var exception = await Record.ExceptionAsync(
            () => provider.DeleteBranchAsync(branchName, CancellationToken.None));
        exception.Should().BeNull("204 No Content is the success response for branch deletion");
    }

    [Fact]
    public async Task DeleteBranchAsync_On422_TreatedAsNoOp()
    {
        // GitHub returns 422 when the branch doesn't exist
        var branchName = $"{PipelineConstants.BranchPrefix}99-already-gone";
        StubError(ApiPath($"/repos/{Owner}/{Repo}/git/refs/heads/{branchName}"), 422,
            new { message = "Reference does not exist" });

        await using var provider = CreateProvider();
        // NotFoundException wraps 422 in Octokit — the provider catches it as no-op
        var exception = await Record.ExceptionAsync(
            () => provider.DeleteBranchAsync(branchName, CancellationToken.None));
        exception.Should().BeNull("non-existent branch should be a no-op, not an exception");
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

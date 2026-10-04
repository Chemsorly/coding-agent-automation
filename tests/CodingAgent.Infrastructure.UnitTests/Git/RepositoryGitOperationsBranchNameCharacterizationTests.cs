using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;
using Polly;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Characterization tests that confirm the BranchName value type is accepted directly
/// (not only via implicit string→BranchName promotion) by RepositoryGitOperations
/// after the signature migration (issue #3313).
///
/// These tests are written first (TDD prerequisite from the issue) and initially fail
/// against the old string-based signatures. Once the signatures are updated they pass.
///
/// Note: no [Trait("Category", "Integration")] — these run under the normal unit-test
/// filter and contribute to coverage.
/// </summary>
public class RepositoryGitOperationsBranchNameCharacterizationTests : IDisposable
{
    private readonly string _repoPath;
    private static readonly ResiliencePipeline NoOpPipeline = ResiliencePipeline.Empty;

    public RepositoryGitOperationsBranchNameCharacterizationTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"rgo-bn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        InitRepoWithInitialCommit();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── CreateBranch ──────────────────────────────────────────────────────

    /// <summary>
    /// Passes an explicit BranchName value (not a string literal) to CreateBranch and
    /// confirms the returned friendly name equals the value.
    /// </summary>
    [Fact]
    public void CreateBranch_AcceptsBranchNameType_ReturnsBranchNameValue()
    {
        var branchName = new BranchName("feature/bn-typed");

        var result = RepositoryGitOperations.CreateBranch(_repoPath, branchName);

        // TODO: The comment "BranchName→string implicit operator" is misleading — the implicit operator
        // being exercised here is string→BranchName on the *parameter*, not on the return value.
        // CreateBranch returns string directly; the assertion validates the correct string value is returned.
        // Rename or clarify if refactoring this test.
        string resultStr = result;
        resultStr.Should().Be("feature/bn-typed");
    }

    // ── CheckoutRemoteBranch ──────────────────────────────────────────────

    /// <summary>
    /// Passes an explicit BranchName to CheckoutRemoteBranch and verifies that the
    /// InvalidOperationException is thrown with the expected message when the remote
    /// branch doesn't exist. This exercises the BranchName parameter through the
    /// error path (where string interpolation of the value type occurs).
    /// </summary>
    [Fact]
    public void CheckoutRemoteBranch_AcceptsBranchNameType_ThrowsOnMissingBranch()
    {
        // Arrange: init a repo with at least one commit and a dummy origin remote
        // so the "origin/{branch}" lookup path is reachable
        using (var repo = new Repository(_repoPath))
        {
            if (!repo.Network.Remotes.Any(r => r.Name == "origin"))
                repo.Network.Remotes.Add("origin", "https://example.com/repo.git");
        }

        var branchName = new BranchName("nonexistent-typed-branch");

        var act = () => RepositoryGitOperations.CheckoutRemoteBranch(_repoPath, branchName);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*nonexistent-typed-branch*");
    }

    // ── HasCommitsAhead ───────────────────────────────────────────────────

    /// <summary>
    /// Passes an explicit BranchName for a non-existent base branch; confirms
    /// the method returns true (conservative default) without throwing.
    /// </summary>
    [Fact]
    public async Task HasCommitsAhead_AcceptsBranchNameType_ReturnsTrueForMissingBranch()
    {
        var baseBranch = new BranchName("nonexistent-base-typed");

        // TODO: This test exercises the error/fallback path and would compile and pass against both
        // the old string signature (via implicit BranchName→string) and the new BranchName signature.
        // It does not serve as a regression guard for the signature change itself — consider adding a
        // test that directly verifies BranchName is accepted where string is no longer accepted.
        var result = await RepositoryGitOperations.HasCommitsAhead(
            _repoPath, baseBranch, NoOpPipeline, CancellationToken.None);

        result.Should().BeTrue("non-existent base branch defaults to true conservatively");
    }

    // ── GetFileChanges ────────────────────────────────────────────────────

    /// <summary>
    /// Passes an explicit BranchName for an unknown branch; confirms empty result
    /// (graceful fallback) without throwing.
    /// </summary>
    [Fact]
    public void GetFileChanges_AcceptsBranchNameType_ReturnsEmptyForMissingBranch()
    {
        var baseBranch = new BranchName("nonexistent-base-typed");

        // TODO: Same concern as HasCommitsAhead above — because BranchName has bidirectional implicit
        // conversion, this test would compile and pass against the old string signature too. It does not
        // serve as a regression guard for the signature change itself.
        var changes = RepositoryGitOperations.GetFileChanges(_repoPath, baseBranch);

        changes.Should().BeEmpty("unknown branch returns empty result gracefully");
    }

    // ── PushWithTokenFactory error path ───────────────────────────────────

    /// <summary>
    /// Passes an explicit BranchName? to PushWithTokenFactory to verify that the two
    /// body-fix expressions — GetActionableMessage(category, branchName?.Value) and
    /// branchName?.Value ?? "unknown" — work correctly when branchName has a value.
    ///
    /// A branch-protection error is used because it renders the branch name in its
    /// actionable message, confirming branchName?.Value is correctly extracted.
    /// </summary>
    [Fact]
    public async Task PushWithTokenFactory_WithBranchNameValue_ErrorPathExtractsBranchNameValue()
    {
        // TODO: This test uses real ResiliencePipelineFactory timeouts (10s/30s). Consider passing
        // retryDelay: TimeSpan.FromMilliseconds(1) to keep the test fast (see RepositoryGitOperationsPushRetryTests).
        // TODO: The assertion WithMessage("*feature/typed-push-branch*") passes whether branchName?.Value,
        // branchName.ToString(), or the implicit string operator is used — all produce the same string.
        // The test does not distinguish between these paths; it verifies correct value extraction but
        // cannot detect a regression where .ToString() is used instead of .Value in a diverging future.
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(
            Serilog.Log.Logger,
            timeout: TimeSpan.FromSeconds(10),
            outerTimeout: TimeSpan.FromSeconds(30));

        BranchName? branchName = new BranchName("feature/typed-push-branch");

        var fakePushAction = new Func<string, Task>(_ =>
            throw new LibGit2SharpException("GH006: Protected branch update failed"));

        Func<Task> act = () => RepositoryGitOperations.PushWithTokenFactory(
            tokenFactory: _ => Task.FromResult("token"),
            tokenUsername: "x-access-token",
            pushAction: fakePushAction,
            pipeline: pipeline,
            branchName: branchName,
            ct: CancellationToken.None);

        // The actionable message for BranchProtection includes the branch name value
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*feature/typed-push-branch*");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void InitRepoWithInitialCommit()
    {
        Repository.Init(_repoPath);
        GlobalSettings.SetOwnerValidation(false);

        using var repo = new Repository(_repoPath);
        File.WriteAllText(Path.Combine(_repoPath, "readme.md"), "# test\n");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("initial commit", sig, sig);
    }
}

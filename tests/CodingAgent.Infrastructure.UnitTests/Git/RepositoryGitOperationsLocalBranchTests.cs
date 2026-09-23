using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;
using Polly;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="RepositoryGitOperations"/> methods that operate on local git
/// repositories without any network I/O: CreateBranch, GetHeadCommitSha,
/// HasCommitsAhead, GetFileChanges (local branch), MapChangeKind, and
/// CommitAll with allowEmpty=true.
///
/// These cover previously-uncovered lines exposed by PR #2852 which added
/// <c>RepositoryGitOperations.cs</c> to the diff.
///
/// Note: intentionally no [Trait("Category", "Integration")] so these run in CI
/// under the "Category!=E2E&amp;Category!=Integration" filter and contribute to coverage.
/// All tests use only local temp git repos — no network I/O.
/// </summary>
public class RepositoryGitOperationsLocalBranchTests : IDisposable
{
    private readonly string _repoPath;
    private static readonly ResiliencePipeline NoOpPipeline = ResiliencePipeline.Empty;

    public RepositoryGitOperationsLocalBranchTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"rgo-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        InitRepoWithInitialCommit();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── CreateBranch ─────────────────────────────────────────────────────

    [Fact]
    public void CreateBranch_NewBranch_ReturnsBranchName()
    {
        var branchName = RepositoryGitOperations.CreateBranch(_repoPath, "feature/test-branch");

        branchName.Should().Be("feature/test-branch");
    }

    [Fact]
    public void CreateBranch_NewBranch_ChecksOutBranch()
    {
        RepositoryGitOperations.CreateBranch(_repoPath, "feature/checkout-test");

        using var repo = new Repository(_repoPath);
        repo.Head.FriendlyName.Should().Be("feature/checkout-test");
    }

    [Fact]
    public void CreateBranch_BranchIsAtSameCommit_AsPrevious()
    {
        using var repo = new Repository(_repoPath);
        var originalSha = repo.Head.Tip.Sha;

        RepositoryGitOperations.CreateBranch(_repoPath, "feature/sha-test");

        using var repo2 = new Repository(_repoPath);
        repo2.Head.Tip.Sha.Should().Be(originalSha, "new branch starts at same commit as main");
    }

    // ── GetHeadCommitSha ─────────────────────────────────────────────────

    [Fact]
    public void GetHeadCommitSha_ReturnsFullSha()
    {
        var sha = RepositoryGitOperations.GetHeadCommitSha(_repoPath);

        sha.Should().HaveLength(40, "full SHA is 40 hex chars");
        sha.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Fact]
    public void GetHeadCommitSha_MatchesRepositoryHeadTip()
    {
        using var repo = new Repository(_repoPath);
        var expected = repo.Head.Tip.Sha;

        var actual = RepositoryGitOperations.GetHeadCommitSha(_repoPath);

        actual.Should().Be(expected);
    }

    [Fact]
    public void GetHeadCommitSha_AfterNewCommit_ReturnsUpdatedSha()
    {
        var originalSha = RepositoryGitOperations.GetHeadCommitSha(_repoPath);

        // Make another commit
        File.WriteAllText(Path.Combine(_repoPath, "extra.txt"), "extra");
        using (var repo = new Repository(_repoPath))
        {
            Commands.Stage(repo, "extra.txt");
            var sig = new Signature("t", "t@t.com", DateTimeOffset.UtcNow);
            repo.Commit("extra commit", sig, sig);
        }

        var newSha = RepositoryGitOperations.GetHeadCommitSha(_repoPath);
        newSha.Should().NotBe(originalSha, "a new commit changes HEAD SHA");
    }

    // ── HasCommitsAhead (local branch) ───────────────────────────────────

    [Fact]
    public async Task HasCommitsAhead_LocalBranchAndBaseAreTheSameCommit_ReturnsFalse()
    {
        // When HEAD and the base branch share the same tip, there are no commits ahead.
        // Use the current default branch as both HEAD and base.
        var defaultBranch = GetDefaultBranchName();
        var result = await RepositoryGitOperations.HasCommitsAhead(_repoPath, defaultBranch, NoOpPipeline, CancellationToken.None);

        result.Should().BeFalse("HEAD equals base branch tip, nothing is ahead");
    }

    [Fact]
    public async Task HasCommitsAhead_NonExistentBranch_ReturnsTrue()
    {
        // When the base branch reference can't be found, method returns true (conservative)
        var result = await RepositoryGitOperations.HasCommitsAhead(_repoPath, "non-existent-branch", NoOpPipeline, CancellationToken.None);

        result.Should().BeTrue("missing base branch defaults to true");
    }

    [Fact]
    public async Task HasCommitsAhead_ErrorPathInvalidRepo_ReturnsTrue()
    {
        // An invalid repo path triggers the catch block which returns true conservatively.
        var invalidPath = Path.Combine(Path.GetTempPath(), $"not-a-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidPath);

        var result = await RepositoryGitOperations.HasCommitsAhead(invalidPath, "main", NoOpPipeline, CancellationToken.None);

        result.Should().BeTrue("error path defaults to true");
        Directory.Delete(invalidPath);
    }

    // ── GetFileChanges (local branch ref) ───────────────────────────────

    [Fact]
    public void GetFileChanges_LocalBranchWithChanges_ReturnsChangedFiles()
    {
        // Create main branch state, then create a feature branch with a new commit
        var defaultBranch = GetDefaultBranchName();
        RepositoryGitOperations.CreateBranch(_repoPath, "feature/file-changes");

        File.WriteAllText(Path.Combine(_repoPath, "new-feature.cs"), "// feature");
        using (var repo = new Repository(_repoPath))
        {
            Commands.Stage(repo, "new-feature.cs");
            var sig = new Signature("t", "t@t.com", DateTimeOffset.UtcNow);
            repo.Commit("add new-feature.cs", sig, sig);
        }

        // GetFileChanges compares HEAD against origin/baseBranch ?? baseBranch
        var changes = RepositoryGitOperations.GetFileChanges(_repoPath, defaultBranch);

        changes.Should().ContainSingle(c => c.Path == "new-feature.cs",
            "newly added file should be visible as a change");
    }

    [Fact]
    public void GetFileChanges_InvalidRepo_ReturnsEmpty()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), $"invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidPath);

        // Not a git repository — should return empty gracefully
        var result = RepositoryGitOperations.GetFileChanges(invalidPath, "main");

        result.Should().BeEmpty();

        Directory.Delete(invalidPath, recursive: true);
    }

    // ── CommitAll allowEmpty=true ────────────────────────────────────────

    [Fact]
    public void CommitAll_AllowEmpty_True_WithNoChanges_SucceedsWithEmptyCommit()
    {
        // allowEmpty=true should succeed even when there is nothing staged
        var unstaged = RepositoryGitOperations.CommitAll(
            _repoPath,
            "empty commit",
            blacklistedPaths: null,
            allowEmpty: true);

        unstaged.Should().BeEmpty();

        using var repo = new Repository(_repoPath);
        repo.Head.Tip.Message.Should().Contain("empty commit");
    }

    [Fact]
    public void CommitAll_AllowEmpty_False_WithNoChanges_Throws()
    {
        // allowEmpty=false with nothing staged must throw
        var act = () => RepositoryGitOperations.CommitAll(
            _repoPath,
            "empty commit",
            blacklistedPaths: null,
            allowEmpty: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No changes to commit*");
    }

    // ── MapChangeKind ────────────────────────────────────────────────────

    [Fact]
    public void MapChangeKind_Added_ReturnsAdded()
        => RepositoryGitOperations.MapChangeKind(ChangeKind.Added).Should().Be("Added");

    [Fact]
    public void MapChangeKind_Deleted_ReturnsDeleted()
        => RepositoryGitOperations.MapChangeKind(ChangeKind.Deleted).Should().Be("Deleted");

    [Fact]
    public void MapChangeKind_Renamed_ReturnsRenamed()
        => RepositoryGitOperations.MapChangeKind(ChangeKind.Renamed).Should().Be("Renamed");

    [Fact]
    public void MapChangeKind_Copied_ReturnsCopied()
        => RepositoryGitOperations.MapChangeKind(ChangeKind.Copied).Should().Be("Copied");

    [Fact]
    public void MapChangeKind_TypeChanged_ReturnsModified()
        => RepositoryGitOperations.MapChangeKind(ChangeKind.TypeChanged).Should().Be("Modified");

    [Fact]
    public void MapChangeKind_Modified_ReturnsModified()
        => RepositoryGitOperations.MapChangeKind(ChangeKind.Modified).Should().Be("Modified");

    [Fact]
    public void MapChangeKind_Unmodified_ReturnsModified()
        => RepositoryGitOperations.MapChangeKind(ChangeKind.Unmodified).Should().Be("Modified");

    // ── CommitAll with configurable blacklist overlap ─────────────────────

    [Fact]
    public void CommitAll_ConfigurableBlacklist_UnstagesMatchingFiles()
    {
        // Files in the configurable blacklist (not already in hardcoded list)
        // should be unstaged by the second pass in UnstageBlacklistedPaths.
        Directory.CreateDirectory(Path.Combine(_repoPath, "dist"));
        File.WriteAllText(Path.Combine(_repoPath, "dist", "bundle.js"), "// bundled");
        File.WriteAllText(Path.Combine(_repoPath, "src", "app.cs"), "// updated app");

        var unstaged = RepositoryGitOperations.CommitAll(
            _repoPath,
            "commit with dist blacklisted",
            blacklistedPaths: new[] { "dist" },
            allowEmpty: false);

        unstaged.Should().Contain(f => f.StartsWith("dist/"),
            "dist/ files should be unstaged by configurable blacklist");

        using var repo = new Repository(_repoPath);
        var tree = repo.Head.Tip.Tree;
        tree["dist"].Should().BeNull("dist/ was blacklisted");
        tree["src/app.cs"].Should().NotBeNull("src/app.cs was committed");
    }

    [Fact]
    public void CommitAll_ConfigurableBlacklist_OverlapsWithHardcoded_NoDoubleCount()
    {
        // When .agent is in both hardcoded AND configurable blacklist, it should
        // appear exactly once in the returned unstaged list.
        Directory.CreateDirectory(Path.Combine(_repoPath, ".agent"));
        File.WriteAllText(Path.Combine(_repoPath, ".agent", "notes.md"), "notes");
        File.WriteAllText(Path.Combine(_repoPath, "readme2.md"), "readme update");

        var unstaged = RepositoryGitOperations.CommitAll(
            _repoPath,
            "overlap test",
            blacklistedPaths: new[] { ".agent", "dist" },
            allowEmpty: false);

        var agentEntries = unstaged.Where(f => f.StartsWith(".agent/")).ToList();
        agentEntries.Should().HaveCount(1, "same path should not appear twice");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void InitRepoWithInitialCommit()
    {
        Repository.Init(_repoPath);
        GlobalSettings.SetOwnerValidation(false);

        using var repo = new Repository(_repoPath);

        Directory.CreateDirectory(Path.Combine(_repoPath, "src"));
        File.WriteAllText(Path.Combine(_repoPath, "readme.md"), "# test\n");
        File.WriteAllText(Path.Combine(_repoPath, "src", "app.cs"), "// app\n");
        Commands.Stage(repo, "*");

        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("initial commit", sig, sig);
    }

    private string GetDefaultBranchName()
    {
        using var repo = new Repository(_repoPath);
        return repo.Head.FriendlyName; // "main" or "master" depending on git config
    }
}

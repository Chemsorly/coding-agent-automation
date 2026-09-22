using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using LibGit2Sharp;
using Polly;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Unit tests for locally-testable methods of <see cref="RepositoryGitOperations"/> that
/// operate purely on a local LibGit2Sharp repository (no network, no remote).
///
/// Covers: GetHeadCommitSha, CreateBranch, GetFileChanges, CollectChangesWithLineStats,
/// HasCommitsAhead (local branch logic), and GetFileChanges error fallback.
/// </summary>
public class RepositoryGitOperationsLocalTests : IDisposable
{
    private readonly string _workspacePath;

    public RepositoryGitOperationsLocalTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"git-local-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        InitGitRepoWithInitialCommit();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_workspacePath, recursive: true); } catch { /* best-effort */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void InitGitRepoWithInitialCommit()
    {
        Repository.Init(_workspacePath);
        using var repo = new Repository(_workspacePath);
        var filePath = Path.Combine(_workspacePath, "README.md");
        File.WriteAllText(filePath, "# Test Repo");
        Commands.Stage(repo, "README.md");
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("Initial commit", sig, sig);
    }

    private void AddAndCommitFile(string relativePath, string content = "// content")
    {
        var fullPath = Path.Combine(_workspacePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        using var repo = new Repository(_workspacePath);
        Commands.Stage(repo, relativePath);
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit($"Add {relativePath}", sig, sig);
    }

    // ── GetHeadCommitSha ────────────────────────────────────────────────────────

    [Fact]
    public void GetHeadCommitSha_ReturnsCurrentHeadSha()
    {
        string expected;
        using (var repo = new Repository(_workspacePath))
            expected = repo.Head.Tip.Sha;

        var actual = RepositoryGitOperations.GetHeadCommitSha(_workspacePath);

        actual.Should().Be(expected, "GetHeadCommitSha must return the current HEAD commit SHA");
    }

    [Fact]
    public void GetHeadCommitSha_AfterNewCommit_ReturnsUpdatedSha()
    {
        var before = RepositoryGitOperations.GetHeadCommitSha(_workspacePath);
        AddAndCommitFile("src/new.cs");
        var after = RepositoryGitOperations.GetHeadCommitSha(_workspacePath);

        after.Should().NotBe(before, "SHA must change after a new commit");
        after.Should().HaveLength(40, "SHA must be a full 40-character hex string");
    }

    // ── CreateBranch ────────────────────────────────────────────────────────────

    [Fact]
    public void CreateBranch_CreatesAndChecksOutNewBranch()
    {
        var branchName = $"feature/test-{Guid.NewGuid():N}".Substring(0, 20);

        var result = RepositoryGitOperations.CreateBranch(_workspacePath, branchName);

        result.Should().Be(branchName, "CreateBranch should return the branch friendly name");

        using var repo = new Repository(_workspacePath);
        repo.Head.FriendlyName.Should().Be(branchName, "HEAD should be checked out on the new branch");
        repo.Branches[branchName].Should().NotBeNull("the branch must exist after creation");
    }

    [Fact]
    public void CreateBranch_NewBranchPointsToSameCommitAsMain()
    {
        string headShaBefore;
        using (var repo = new Repository(_workspacePath))
            headShaBefore = repo.Head.Tip.Sha;

        var branchName = $"feature/b-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
        RepositoryGitOperations.CreateBranch(_workspacePath, branchName);

        using var repoAfter = new Repository(_workspacePath);
        repoAfter.Head.Tip.Sha.Should().Be(headShaBefore,
            "new branch should point to the same commit that HEAD was on before creation");
    }

    // ── CollectChangesWithLineStats ─────────────────────────────────────────────

    [Fact]
    public void CollectChangesWithLineStats_NewFile_ReturnsAddedEntry()
    {
        AddAndCommitFile("src/added.cs", "public class Added {}");

        using var repo = new Repository(_workspacePath);
        var commits = repo.Commits.ToList();
        // commits[0] is the latest, commits[1] is the initial commit
        var changes = RepositoryGitOperations.CollectChangesWithLineStats(
            repo, commits[1].Tree, commits[0].Tree);

        changes.Should().NotBeEmpty("adding a file must appear as a change");
        changes.Should().Contain(c => c.Path == "src/added.cs",
            "the newly added file path must be in the changes list");
        changes.Single(c => c.Path == "src/added.cs").Status.Should().NotBeNullOrEmpty(
            "the change status must be populated");
    }

    [Fact]
    public void CollectChangesWithLineStats_ModifiedFile_ReturnsModifiedEntry()
    {
        AddAndCommitFile("src/modified.cs", "// original");
        File.WriteAllText(Path.Combine(_workspacePath, "src/modified.cs"), "// updated");
        using var repo = new Repository(_workspacePath);
        Commands.Stage(repo, "src/modified.cs");
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("Modify file", sig, sig);

        using var repo2 = new Repository(_workspacePath);
        var commits = repo2.Commits.ToList();
        var changes = RepositoryGitOperations.CollectChangesWithLineStats(
            repo2, commits[1].Tree, commits[0].Tree);

        changes.Should().Contain(c => c.Path == "src/modified.cs");
        changes.Single(c => c.Path == "src/modified.cs").Status.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CollectChangesWithLineStats_NoChanges_ReturnsEmptyList()
    {
        using var repo = new Repository(_workspacePath);
        var head = repo.Head.Tip;
        // Compare tree against itself — no changes
        var changes = RepositoryGitOperations.CollectChangesWithLineStats(
            repo, head.Tree, head.Tree);

        changes.Should().BeEmpty("comparing identical trees must produce no changes");
    }

    // ── GetFileChanges ──────────────────────────────────────────────────────────

    [Fact]
    public void GetFileChanges_BranchNotFound_ReturnsEmptyList()
    {
        // No "origin/nonexistent" branch — should not throw, returns empty
        var changes = RepositoryGitOperations.GetFileChanges(
            _workspacePath, "nonexistent-branch-xyz");

        changes.Should().BeEmpty(
            "when the base branch is not found, GetFileChanges must return empty rather than throw");
    }

    [Fact]
    public void GetFileChanges_WithLocalBranch_ReturnsChanges()
    {
        // Create a local base branch and add a commit on a new branch
        using (var repo = new Repository(_workspacePath))
        {
            repo.Branches.Add("base", repo.Head.Tip);
        }

        AddAndCommitFile("src/feature.cs", "public class Feature {}");

        var changes = RepositoryGitOperations.GetFileChanges(_workspacePath, "base");
        changes.Should().NotBeEmpty("there is one new file relative to the base branch");
        changes.Should().Contain(c => c.Path == "src/feature.cs");
    }

    // ── MapChangeKind ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChangeKind.Added, "Added")]
    [InlineData(ChangeKind.Deleted, "Deleted")]
    [InlineData(ChangeKind.Renamed, "Renamed")]
    [InlineData(ChangeKind.Copied, "Copied")]
    [InlineData(ChangeKind.TypeChanged, "Modified")]
    [InlineData(ChangeKind.Modified, "Modified")]
    [InlineData(ChangeKind.Conflicted, "Modified")]
    [InlineData(ChangeKind.Ignored, "Modified")]
    [InlineData(ChangeKind.Untracked, "Modified")]
    public void MapChangeKind_ReturnsExpectedString(ChangeKind kind, string expected)
    {
        var result = RepositoryGitOperations.MapChangeKind(kind);
        result.Should().Be(expected, $"MapChangeKind({kind}) must return '{expected}'");
    }

    // ── CollectChangesWithLineStats — multiple files ────────────────────────────

    [Fact]
    public void CollectChangesWithLineStats_DeletedFile_ReturnsDeletedEntry()
    {
        // Add a file in first commit, delete it in second
        AddAndCommitFile("src/toDelete.cs", "// to be deleted");

        string firstSha, secondSha;
        using (var repo = new Repository(_workspacePath))
        {
            firstSha = repo.Head.Tip.Sha;
            var filePath = Path.Combine(_workspacePath, "src/toDelete.cs");
            File.Delete(filePath);
            repo.Index.Remove("src/toDelete.cs");
            repo.Index.Write();
            var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
            repo.Commit("Delete file", sig, sig);
            secondSha = repo.Head.Tip.Sha;
        }

        using var repo2 = new Repository(_workspacePath);
        var baseCommit = repo2.Lookup<Commit>(firstSha);
        var headCommit = repo2.Lookup<Commit>(secondSha);
        var changes = RepositoryGitOperations.CollectChangesWithLineStats(
            repo2, baseCommit.Tree, headCommit.Tree);

        changes.Should().Contain(c => c.Path == "src/toDelete.cs",
            "deleted file must appear in the change set");
    }

    [Fact]
    public void CollectChangesWithLineStats_LinesAddedAndDeleted_ReturnsLineStats()
    {
        AddAndCommitFile("src/counted.cs", "line1\nline2\nline3");
        File.WriteAllText(Path.Combine(_workspacePath, "src/counted.cs"), "line1\nnewline2\nline3\nline4");
        using var repo = new Repository(_workspacePath);
        Commands.Stage(repo, "src/counted.cs");
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("Modify with line changes", sig, sig);

        using var repo2 = new Repository(_workspacePath);
        var commits = repo2.Commits.ToList();
        var changes = RepositoryGitOperations.CollectChangesWithLineStats(
            repo2, commits[1].Tree, commits[0].Tree);

        var summary = changes.Single(c => c.Path == "src/counted.cs");
        summary.LinesAdded.Should().BeGreaterThanOrEqualTo(0);
        summary.LinesDeleted.Should().BeGreaterThanOrEqualTo(0);
    }

    // ── GetHeadCommitSha — additional assertions ────────────────────────────────

    [Fact]
    public void GetHeadCommitSha_IsFortyHexCharacters()
    {
        var sha = RepositoryGitOperations.GetHeadCommitSha(_workspacePath);
        sha.Should().HaveLength(40);
        sha.Should().MatchRegex("^[0-9a-f]{40}$", "SHA must be a lowercase hex string");
    }


    [Fact]
    public async Task HasCommitsAhead_WhenAhead_ReturnsTrue()
    {
        // Set up: create a local "base" branch pointing to initial commit,
        // then add a commit on main (ahead of base)
        using (var repo = new Repository(_workspacePath))
        {
            repo.Branches.Add("base", repo.Head.Tip);
        }

        AddAndCommitFile("src/ahead.cs");

        var result = await RepositoryGitOperations.HasCommitsAhead(
            _workspacePath, "base", ResiliencePipeline.Empty, CancellationToken.None);

        result.Should().BeTrue("HEAD is 1 commit ahead of 'base'");
    }

    [Fact]
    public async Task HasCommitsAhead_BranchNotFound_ReturnsTrue()
    {
        // Branch not found → returns true (conservative — assume there are changes)
        var result = await RepositoryGitOperations.HasCommitsAhead(
            _workspacePath, "nonexistent-branch-for-test", ResiliencePipeline.Empty, CancellationToken.None);

        result.Should().BeTrue("when the base branch cannot be found, HasCommitsAhead returns true conservatively");
    }

    [Fact]
    public async Task HasCommitsAhead_WhenAtSameCommit_ReturnsFalse()
    {
        // Create a local branch pointing to same commit as HEAD
        string headSha;
        using (var repo = new Repository(_workspacePath))
        {
            headSha = repo.Head.Tip.Sha;
            repo.Branches.Add("same-commit", repo.Head.Tip);
        }

        var result = await RepositoryGitOperations.HasCommitsAhead(
            _workspacePath, "same-commit", ResiliencePipeline.Empty, CancellationToken.None);

        result.Should().BeFalse("HEAD is at the same commit as 'same-commit', so there are no commits ahead");
    }
}

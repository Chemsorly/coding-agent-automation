using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Models;
using LibGit2Sharp;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="RepositoryGitOperations.CollectChangesWithLineStats"/> and
/// <see cref="RepositoryGitOperations.GetFileChanges"/>, both extracted/refactored in PR #1778.
/// </summary>
[Trait("Category", "Integration")]
public class RepositoryGitOperationsCollectChangesTests : IDisposable
{
    private readonly string _repoPath;

    public RepositoryGitOperationsCollectChangesTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"collect-changes-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        InitRepoWithBaseCommit();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, recursive: true); } catch { }
    }

    // ── CollectChangesWithLineStats ──────────────────────────────────────

    [Fact]
    public void CollectChangesWithLineStats_NoDiff_ReturnsEmptyList()
    {
        using var repo = new Repository(_repoPath);
        var baseTree = repo.Head.Tip.Tree;
        var headTree = repo.Head.Tip.Tree; // same commit

        var changes = RepositoryGitOperations.CollectChangesWithLineStats(repo, baseTree, headTree);

        changes.Should().BeEmpty("identical trees produce no changes");
    }

    [Fact]
    public void CollectChangesWithLineStats_AddedFile_ReturnsAddedEntry()
    {
        // Arrange — add a new file and commit it on a new branch
        using var repo = new Repository(_repoPath);
        var baseTree = repo.Head.Tip.Tree;

        File.WriteAllText(Path.Combine(_repoPath, "newfile.cs"), "// new");
        Commands.Stage(repo, "newfile.cs");
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("add newfile.cs", sig, sig);
        var headTree = repo.Head.Tip.Tree;

        // Act
        var changes = RepositoryGitOperations.CollectChangesWithLineStats(repo, baseTree, headTree);

        // Assert
        changes.Should().ContainSingle(c => c.Path == "newfile.cs",
            "newly added file should appear in changes");
    }

    [Fact]
    public void CollectChangesWithLineStats_ModifiedFile_ReturnsModifiedEntry()
    {
        using var repo = new Repository(_repoPath);
        var baseTree = repo.Head.Tip.Tree;

        // Modify the existing file
        File.WriteAllText(Path.Combine(_repoPath, "readme.md"), "updated content\nmore lines\n");
        Commands.Stage(repo, "readme.md");
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("modify readme.md", sig, sig);
        var headTree = repo.Head.Tip.Tree;

        var changes = RepositoryGitOperations.CollectChangesWithLineStats(repo, baseTree, headTree);

        changes.Should().ContainSingle(c => c.Path == "readme.md");
    }

    [Fact]
    public void CollectChangesWithLineStats_ModifiedFile_HasLineStats()
    {
        using var repo = new Repository(_repoPath);
        var baseTree = repo.Head.Tip.Tree;

        File.WriteAllText(Path.Combine(_repoPath, "readme.md"), "line1\nline2\nline3\n");
        Commands.Stage(repo, "readme.md");
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("modify readme.md with 3 lines", sig, sig);
        var headTree = repo.Head.Tip.Tree;

        var changes = RepositoryGitOperations.CollectChangesWithLineStats(repo, baseTree, headTree);

        var change = changes.Single(c => c.Path == "readme.md");
        (change.LinesAdded + change.LinesDeleted).Should().BeGreaterThan(0,
            "line stats should be populated for modified files");
    }

    [Fact]
    public void CollectChangesWithLineStats_MultipleFiles_ReturnsAll()
    {
        using var repo = new Repository(_repoPath);
        var baseTree = repo.Head.Tip.Tree;

        File.WriteAllText(Path.Combine(_repoPath, "file1.cs"), "// file1");
        File.WriteAllText(Path.Combine(_repoPath, "file2.cs"), "// file2");
        Commands.Stage(repo, "file1.cs");
        Commands.Stage(repo, "file2.cs");
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("add two files", sig, sig);
        var headTree = repo.Head.Tip.Tree;

        var changes = RepositoryGitOperations.CollectChangesWithLineStats(repo, baseTree, headTree);

        changes.Should().HaveCount(2);
        changes.Should().Contain(c => c.Path == "file1.cs");
        changes.Should().Contain(c => c.Path == "file2.cs");
    }

    // ── GetFileChanges ───────────────────────────────────────────────────

    [Fact]
    public void GetFileChanges_UnknownBranch_ReturnsEmpty()
    {
        // Branch does not exist → should return empty, not throw
        var result = RepositoryGitOperations.GetFileChanges(_repoPath, "non-existent-branch");

        result.Should().BeEmpty("unknown branch should return empty result gracefully");
    }

    // ── FileChangeSummary content ────────────────────────────────────────

    [Fact]
    public void CollectChangesWithLineStats_AddedFile_HasNonNegativeLineStats()
    {
        using var repo = new Repository(_repoPath);
        var baseTree = repo.Head.Tip.Tree;

        File.WriteAllText(Path.Combine(_repoPath, "stats.cs"), "line1\nline2\n");
        Commands.Stage(repo, "stats.cs");
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("add stats.cs", sig, sig);
        var headTree = repo.Head.Tip.Tree;

        var changes = RepositoryGitOperations.CollectChangesWithLineStats(repo, baseTree, headTree);

        var change = changes.Single(c => c.Path == "stats.cs");
        change.LinesAdded.Should().BeGreaterThanOrEqualTo(0);
        change.LinesDeleted.Should().BeGreaterThanOrEqualTo(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void InitRepoWithBaseCommit()
    {
        Repository.Init(_repoPath);
        using var repo = new Repository(_repoPath);

        // Disable ownership validation (Docker/CI compatibility)
        GlobalSettings.SetOwnerValidation(false);

        File.WriteAllText(Path.Combine(_repoPath, "readme.md"), "# Test Repo\n");
        Directory.CreateDirectory(Path.Combine(_repoPath, "src"));
        File.WriteAllText(Path.Combine(_repoPath, "src", "app.cs"), "// app");

        Commands.Stage(repo, "*");
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);
        repo.Commit("initial commit", sig, sig);
    }
}

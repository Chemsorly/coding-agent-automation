using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Git;
using LibGit2Sharp;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Git;

/// <summary>
/// Integration tests for <see cref="LibGit2SharpGitOperations"/>.
/// Each test initialises a temp git repository, exercises the operation under test,
/// and asserts on observable outcomes rather than LibGit2Sharp internals.
///
/// Uses the same real-on-disk temp-repo pattern established by
/// <see cref="RepositoryGitOperationsCommitBlacklistTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public class LibGit2SharpGitOperationsTests : IDisposable
{
    private readonly string _repoPath;
    private readonly LibGit2SharpGitOperations _sut = new();

    public LibGit2SharpGitOperationsTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"libgit2-ops-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        InitRepoWithCommit(_repoPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, recursive: true); } catch { }
    }

    // ── StageAllAndCommit: EmptyCommitException wrapping ─────────────────────

    [Fact]
    public void StageAllAndCommit_NothingToCommit_ThrowsDomainEmptyCommitException()
    {
        // No changes since the initial commit — nothing staged
        var act = () => _sut.StageAllAndCommit(_repoPath, "empty commit");

        act.Should().Throw<CodingAgentWebUI.Infrastructure.Git.EmptyCommitException>(
            "LibGit2Sharp.EmptyCommitException must be translated to the domain type");
    }

    [Fact]
    public void StageAllAndCommit_WithChanges_CommitsSuccessfully()
    {
        File.WriteAllText(Path.Combine(_repoPath, "new-file.txt"), "content");

        _sut.StageAllAndCommit(_repoPath, "add new-file");

        using var repo = new Repository(_repoPath);
        repo.Head.Tip.Message.Trim().Should().Be("add new-file",
            "commit message must match what was passed in");
        repo.Head.Tip.Tree["new-file.txt"].Should().NotBeNull(
            "new-file.txt must appear in the committed tree");
    }

    // ── GetChangedFiles: excludes ignored and unaltered files ─────────────────

    [Fact]
    public void GetChangedFiles_ModifiedFile_ReturnsRelativePath()
    {
        File.WriteAllText(Path.Combine(_repoPath, "tracked.txt"), "modified content");

        var changed = _sut.GetChangedFiles(_repoPath);

        changed.Should().Contain("tracked.txt",
            "a modified tracked file must appear in GetChangedFiles");
    }

    [Fact]
    public void GetChangedFiles_IgnoredFile_IsExcluded()
    {
        // Create a .gitignore that ignores *.log, then add a .log file
        File.WriteAllText(Path.Combine(_repoPath, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(_repoPath, "ignored.log"), "log data");

        var changed = _sut.GetChangedFiles(_repoPath);

        changed.Should().NotContain("ignored.log",
            "files matching .gitignore must not appear in GetChangedFiles");
    }

    [Fact]
    public void GetChangedFiles_UnmodifiedTrackedFile_IsExcluded()
    {
        // The initial commit already includes "tracked.txt" — do not modify it
        var changed = _sut.GetChangedFiles(_repoPath);

        changed.Should().NotContain("tracked.txt",
            "an unmodified tracked file must not appear in GetChangedFiles");
    }

    // ── GetFileContentFromHeadParent: returns null on orphan HEAD ─────────────

    [Fact]
    public void GetFileContentFromHeadParent_OrphanHead_ReturnsNull()
    {
        // Create a fresh repo with only one commit (no parent)
        var singleCommitRepo = Path.Combine(Path.GetTempPath(), $"orphan-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(singleCommitRepo);
            InitRepoWithCommit(singleCommitRepo);

            // HEAD has a parent from InitRepoWithCommit, but we want an orphan.
            // Use a brand-new repo that has only 1 commit (InitRepoWithCommit creates exactly 1).
            var result = _sut.GetFileContentFromHeadParent(singleCommitRepo, "tracked.txt");

            // Single commit → no parent → must return null, not throw
            result.Should().BeNull(
                "HEAD with no parent must return null from GetFileContentFromHeadParent");
        }
        finally
        {
            try { Directory.Delete(singleCommitRepo, recursive: true); } catch { }
        }
    }

    // ── GetFileContentFromHeadParent: returns content when parent exists ──────

    [Fact]
    public void GetFileContentFromHeadParent_WithParentCommit_ReturnsOriginalContent()
    {
        const string originalContent = "original content";
        const string updatedContent  = "updated content";

        // The initial commit already has "tracked.txt" with "initial content".
        // Create a second commit that modifies the file.
        File.WriteAllText(Path.Combine(_repoPath, "tracked.txt"), updatedContent);
        _sut.StageAllAndCommit(_repoPath, "update tracked.txt");

        // GetFileContentFromHeadParent should return content from the FIRST commit (parent)
        var result = _sut.GetFileContentFromHeadParent(_repoPath, "tracked.txt");

        result.Should().Be(originalContent,
            "GetFileContentFromHeadParent must return the parent commit's version of the file");
    }

    // ── ResetHardToRemote: no-op when remote branch does not exist ────────────

    [Fact]
    public void ResetHardToRemote_NonExistentBranch_DoesNotThrow()
    {
        // No remotes configured → branch doesn't exist → must be a no-op
        var act = () => _sut.ResetHardToRemote(_repoPath, "nonexistent-branch");

        act.Should().NotThrow(
            "ResetHardToRemote must be a no-op when the remote branch does not exist");
    }

    // ── HasConflicts: false on a clean working tree ────────────────────────────

    [Fact]
    public void HasConflicts_CleanRepo_ReturnsFalse()
    {
        _sut.HasConflicts(_repoPath).Should().BeFalse(
            "a clean repository must have no conflicts");
    }

    // ── GetHeadCommitFileCount: correct after a second commit ─────────────────

    [Fact]
    public void GetHeadCommitFileCount_InitialCommit_ReturnsZero()
    {
        // Initial commit has no parent → diff against parent is empty → returns 0
        _sut.GetHeadCommitFileCount(_repoPath).Should().Be(0,
            "initial commit has no parent, so diff returns 0");
    }

    [Fact]
    public void GetHeadCommitFileCount_AfterSecondCommit_ReturnsOne()
    {
        // Second commit: add one new file
        File.WriteAllText(Path.Combine(_repoPath, "second-file.txt"), "second content");
        _sut.StageAllAndCommit(_repoPath, "add second-file");

        _sut.GetHeadCommitFileCount(_repoPath).Should().Be(1,
            "second commit adds exactly 1 file relative to its parent");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void InitRepoWithCommit(string path)
    {
        Repository.Init(path);
        using var repo = new Repository(path);

        // Suppress ownership validation (same as GlobalSettings in production code)
        GlobalSettings.SetOwnerValidation(false);

        // Seed with one tracked file so HEAD is non-null and has content
        File.WriteAllText(Path.Combine(path, "tracked.txt"), "original content");
        Commands.Stage(repo, "*");
        var sig = new Signature("test-author", "test@example.com", DateTimeOffset.UtcNow);
        repo.Commit("initial commit", sig, sig);
    }
}

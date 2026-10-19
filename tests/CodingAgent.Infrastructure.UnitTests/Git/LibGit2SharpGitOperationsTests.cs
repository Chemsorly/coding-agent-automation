using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using LibGit2Sharp;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="LibGit2SharpGitOperations"/>.
/// Each test initialises a temp git repository, exercises the operation under test,
/// and asserts on observable outcomes rather than LibGit2Sharp internals.
///
/// Uses the same real-on-disk temp-repo pattern established by
/// <see cref="RepositoryGitOperationsCommitBlacklistTests"/>.
/// No external services required — all operations are purely in-process.
/// </summary>
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

        act.Should().Throw<CodingAgent.Infrastructure.Git.EmptyCommitException>(
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

    // ── GetConflicts: two-branch conflict returns ours/theirs content ─────────

    [Fact]
    public void GetConflicts_TwoBranchConflict_ReturnsBothContents()
    {
        // Arrange: set up conflicting commits on two branches using the same base.
        // Must use a scoped using block — the Repository handle must be released
        // before _sut.GetConflicts opens a second handle to the same path.
        using (var repo = new Repository(_repoPath))
        {
            var sig = new Signature("test-author", "test@example.com", DateTimeOffset.UtcNow);
            // Capture the default branch name (may be "main" or "master" depending on env)
            var currentBranch = repo.Head.FriendlyName;

            // Create feature branch pointing at the same initial commit
            repo.CreateBranch("feature");

            // Commit "ours content" on the current (main/master) branch
            File.WriteAllText(Path.Combine(_repoPath, "tracked.txt"), "ours content");
            Commands.Stage(repo, "tracked.txt");
            repo.Commit("ours commit", sig, sig);

            // Switch to feature branch and commit "theirs content"
            Commands.Checkout(repo, repo.Branches["feature"]);
            File.WriteAllText(Path.Combine(_repoPath, "tracked.txt"), "theirs content");
            Commands.Stage(repo, "tracked.txt");
            repo.Commit("theirs commit", sig, sig);

            // Switch back to main/master and merge feature — produces a conflict
            Commands.Checkout(repo, repo.Branches[currentBranch]);
            repo.Merge(repo.Branches["feature"], sig, new MergeOptions());
        }

        // Act
        var conflicts = _sut.GetConflicts(_repoPath);

        // Assert
        conflicts.Should().ContainSingle("one file was modified on both branches");
        conflicts[0].FilePath.Should().Be("tracked.txt");
        conflicts[0].OursContent.Should().Be("ours content",
            "OursContent must reflect the receiving branch's blob");
        conflicts[0].TheirsContent.Should().Be("theirs content",
            "TheirsContent must reflect the merging branch's blob");
    }

    [Fact]
    public void GetConflicts_ModifyDeleteConflict_OursContentIsEmpty()
    {
        // Arrange: modify-delete conflict — main deletes tracked.txt, feature modifies it.
        // This exercises the conflict.Ours == null null-guard in GetConflicts (production L41):
        //   var oursContent = conflict.Ours != null ? ... : "";
        // Must use a scoped using block to release the Repository handle before SUT call.
        using (var repo = new Repository(_repoPath))
        {
            var sig = new Signature("test-author", "test@example.com", DateTimeOffset.UtcNow);
            var currentBranch = repo.Head.FriendlyName;

            // Create feature branch at the initial commit (ancestor has tracked.txt)
            repo.CreateBranch("feature");

            // On main: delete tracked.txt and commit → conflict.Ours will be null on merge
            File.Delete(Path.Combine(_repoPath, "tracked.txt"));
            Commands.Stage(repo, "tracked.txt");
            repo.Commit("delete tracked.txt on main", sig, sig);

            // Switch to feature: tracked.txt is restored from the initial commit
            Commands.Checkout(repo, repo.Branches["feature"]);
            File.WriteAllText(Path.Combine(_repoPath, "tracked.txt"), "theirs content");
            Commands.Stage(repo, "tracked.txt");
            repo.Commit("modify tracked.txt on feature", sig, sig);

            // Merge feature into main: main deleted the file, feature modified it → conflict
            Commands.Checkout(repo, repo.Branches[currentBranch]);
            repo.Merge(repo.Branches["feature"], sig, new MergeOptions());
        }

        // Act
        var conflicts = _sut.GetConflicts(_repoPath);

        // Assert
        conflicts.Should().ContainSingle("one file has a modify-delete conflict");
        conflicts[0].FilePath.Should().Be("tracked.txt",
            "FilePath is resolved via conflict.Theirs.Path when Ours is null");
        conflicts[0].OursContent.Should().Be("",
            "OursContent must be empty string when conflict.Ours is null (main deleted the file)");
        conflicts[0].TheirsContent.Should().Be("theirs content",
            "TheirsContent must contain the feature branch's blob content");
    }

    // ── StageFile: single file appears in the index after staging ─────────────

    [Fact]
    public void StageFile_NewFile_AppearsInIndex()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_repoPath, "to-stage.txt"), "staged content");

        // Act
        _sut.StageFile(_repoPath, "to-stage.txt");

        // Assert
        using var repo = new Repository(_repoPath);
        repo.Index["to-stage.txt"].Should().NotBeNull(
            "the file must appear in the git index after StageFile is called");
    }

    // ── GetHeadCommitChanges: Added and Deleted status mapping ────────────────

    [Fact]
    public void GetHeadCommitChanges_AddAndDeleteInSingleCommit_ReturnsBothStatuses()
    {
        // Arrange: create a second commit that adds one file and deletes another.
        // StageAllAndCommit uses Commands.Stage("*") which is equivalent to git add -A
        // and stages both new files and deletions.
        File.WriteAllText(Path.Combine(_repoPath, "new-added.txt"), "added content");
        File.Delete(Path.Combine(_repoPath, "tracked.txt"));
        _sut.StageAllAndCommit(_repoPath, "add new, delete old");

        // Act
        var changes = _sut.GetHeadCommitChanges(_repoPath);

        // Assert
        changes.Should().Contain(c => c.Path == "new-added.txt" && c.Status == FileChangeStatus.Added,
            "new-added.txt was created in this commit and must map to FileChangeStatus.Added");
        changes.Should().Contain(c => c.Path == "tracked.txt" && c.Status == FileChangeStatus.Deleted,
            "tracked.txt was removed in this commit and must map to FileChangeStatus.Deleted");
    }

    // ── GetFileContentFromHead: existing path / non-existent path ─────────────

    [Fact]
    public void GetFileContentFromHead_ExistingFile_ReturnsContent()
    {
        // InitRepoWithCommit writes "original content" to tracked.txt without a trailing newline.
        // Blob.GetContentText() returns the raw stored blob — no CRLF normalisation applied.
        var result = _sut.GetFileContentFromHead(_repoPath, "tracked.txt");

        result.Should().Be("original content",
            "GetFileContentFromHead must return the exact content stored in the HEAD commit tree");
    }

    [Fact]
    public void GetFileContentFromHead_NonExistentFile_ReturnsNull()
    {
        var result = _sut.GetFileContentFromHead(_repoPath, "does-not-exist.txt");

        result.Should().BeNull(
            "GetFileContentFromHead must return null when the path is not present in the HEAD tree");
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

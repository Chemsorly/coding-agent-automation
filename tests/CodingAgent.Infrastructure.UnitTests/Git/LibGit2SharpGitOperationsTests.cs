using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using LibGit2Sharp;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Runtime.Versioning;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Integration tests for <see cref="LibGit2SharpGitOperations"/>.
/// Each test initialises a temp git repository, exercises the operation under test,
/// and asserts on observable outcomes rather than LibGit2Sharp internals.
///
/// Uses the same real-on-disk temp-repo pattern established by
/// <see cref="RepositoryGitOperationsCommitBlacklistTests"/>.
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
        const string updatedContent = "updated content";

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

    // ── GetConflicts: two-branch merge conflict ───────────────────────────────

    [Fact]
    public void GetConflicts_WithMergeConflict_ReturnsBothSidesContent()
    {
        const string oursContent = "ours content";
        const string theirsContent = "theirs content";

        CreateMergeConflict(_repoPath, "tracked.txt", oursContent, theirsContent);

        var conflicts = _sut.GetConflicts(_repoPath);

        conflicts.Should().HaveCount(1, "exactly one file was modified on both sides");
        var conflict = conflicts[0];
        conflict.FilePath.Should().Be("tracked.txt");
        conflict.OursContent.Should().Be(oursContent);
        conflict.TheirsContent.Should().Be(theirsContent);
    }

    // ── StageFile: single file staged ─────────────────────────────────────────

    [Fact]
    public void StageFile_NewFile_AppearsInIndex()
    {
        const string fileName = "unstaged.txt";
        File.WriteAllText(Path.Combine(_repoPath, fileName), "some content");

        _sut.StageFile(_repoPath, fileName);

        using var repo = new Repository(_repoPath);
        repo.Index[fileName].Should().NotBeNull(
            "StageFile must add the file to the git index");
    }

    // ── GetHeadCommitChanges: Added and Deleted status mapping ────────────────

    [Fact]
    public void GetHeadCommitChanges_InitialCommit_ReturnsEmpty()
    {
        // Initial commit has no parent → parentTree is null → must return empty, not throw.
        // Guards against regression of the `if (parentTree is null) return Array.Empty<FileChange>()`
        // null-check in the production implementation.
        var changes = _sut.GetHeadCommitChanges(_repoPath);

        changes.Should().BeEmpty(
            "the initial commit has no parent, so GetHeadCommitChanges must return an empty list");
    }

    [Fact]
    public void GetHeadCommitChanges_AddedAndDeletedFiles_BothStatusesReturned()
    {
        const string addedFileName = "added-file.txt";
        var sig = new Signature("test-author", "test@example.com", DateTimeOffset.UtcNow);

        File.WriteAllText(Path.Combine(_repoPath, addedFileName), "new content");
        File.Delete(Path.Combine(_repoPath, "tracked.txt"));

        using (var repo = new Repository(_repoPath))
        {
            Commands.Stage(repo, addedFileName);
            Commands.Stage(repo, "tracked.txt");  // stages the deletion
            repo.Commit("add and delete", sig, sig);
        }

        var changes = _sut.GetHeadCommitChanges(_repoPath);

        changes.Any(c => c.Path == addedFileName && c.Status == FileChangeStatus.Added)
            .Should().BeTrue("added-file.txt was added in this commit");
        changes.Any(c => c.Path == "tracked.txt" && c.Status == FileChangeStatus.Deleted)
            .Should().BeTrue("tracked.txt was deleted in this commit");
    }

    // ── GetFileContentFromHead ────────────────────────────────────────────────

    [Fact]
    public void GetFileContentFromHead_ExistingFile_ReturnsCommittedContent()
    {
        var content = _sut.GetFileContentFromHead(_repoPath, "tracked.txt");

        content.Should().Be("original content",
            "tracked.txt was committed with this exact content in the initial commit");
    }

    [Fact]
    public void GetFileContentFromHead_NonExistentFile_ReturnsNull()
    {
        var content = _sut.GetFileContentFromHead(_repoPath, "does-not-exist.txt");

        content.Should().BeNull(
            "a path not present in the HEAD tree must return null");
    }

    // ── TryDeleteEmptyDirectories: locked directory is skipped, debug log emitted ──

    /// <summary>
    /// Simulates a directory that cannot be deleted (parent has no write permission)
    /// and verifies that the exception is caught, a debug log entry is emitted, and
    /// iteration continues for remaining directories without throwing.
    /// A second deletable directory is included so that the test proves the loop did
    /// not stop after the failure (a break-after-log regression would leave it present).
    /// Requires Unix file-permission semantics as a non-root user — skipped otherwise.
    /// </summary>
    [SkipOnWindowsFact("Requires Unix file permissions to simulate a non-deletable directory")]
    [UnsupportedOSPlatform("windows")]
    public void TryDeleteEmptyDirectories_LockedDirectory_ExceptionCaughtAndDebugLogged()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var sut = new LibGit2SharpGitOperations(logger);

        // container/locked/ — Directory.Delete will fail because container/ has no write permission.
        var containerDir = Path.Combine(_repoPath, "container");
        var lockedDir = Path.Combine(containerDir, "locked");
        Directory.CreateDirectory(lockedDir);

        // alsoToDelete/ — direct child of _repoPath (writable parent); will succeed after the failure.
        // Its presence proves the loop continues rather than stopping after catching the exception.
        var alsoToDeleteDir = Path.Combine(_repoPath, "alsoToDelete");
        Directory.CreateDirectory(alsoToDeleteDir);

        // Remove write permission from containerDir so that Directory.Delete(lockedDir) fails.
        File.SetUnixFileMode(containerDir,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {
            var act = () => sut.DiscardChangesExcept(_repoPath, "tracked.txt");

            act.Should().NotThrow("exceptions from Directory.Delete must be caught and logged, not propagated");

            sink.Events.Should()
                .ContainSingle(e =>
                    e.Level == LogEventLevel.Debug &&
                    e.MessageTemplate.Text.Contains("Could not remove empty directory"),
                    "a debug log entry must be emitted for the directory that could not be deleted");

            Directory.Exists(lockedDir).Should().BeTrue(
                "the locked directory must still exist after a failed delete attempt");

            Directory.Exists(alsoToDeleteDir).Should().BeFalse(
                "the second deletable directory must have been removed, proving the loop continued after the failure");
        }
        finally
        {
            // Restore write permission so Dispose() can clean up the temp repo.
            File.SetUnixFileMode(containerDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// Verifies that when a mix of deletable and locked directories exists, all deletable
    /// directories are removed and only the locked one is skipped.
    /// Requires Unix file-permission semantics — skipped on Windows.
    /// </summary>
    [SkipOnWindowsFact("Requires Unix file permissions to simulate a non-deletable directory")]
    [UnsupportedOSPlatform("windows")]
    public void TryDeleteEmptyDirectories_MixedDirectories_DeletableRemovedLockedSkipped()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var sut = new LibGit2SharpGitOperations(logger);

        // toDelete/ is a direct child of _repoPath (writable parent) — will succeed.
        var toDeleteDir = Path.Combine(_repoPath, "toDelete");
        Directory.CreateDirectory(toDeleteDir);

        // container/locked/ — write permission removed from container/ so locked/ cannot be deleted.
        var containerDir = Path.Combine(_repoPath, "container");
        var lockedDir = Path.Combine(containerDir, "locked");
        Directory.CreateDirectory(lockedDir);

        File.SetUnixFileMode(containerDir,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {
            var act = () => sut.DiscardChangesExcept(_repoPath, "tracked.txt");

            act.Should().NotThrow();

            Directory.Exists(toDeleteDir).Should().BeFalse(
                "the deletable directory must have been removed");

            Directory.Exists(lockedDir).Should().BeTrue(
                "the locked directory must still exist after the failed delete attempt");

            sink.Events.Should()
                .ContainSingle(e =>
                    e.Level == LogEventLevel.Debug &&
                    e.MessageTemplate.Text.Contains("Could not remove empty directory"),
                    "exactly one debug log entry must be emitted — only for the locked directory");
        }
        finally
        {
            File.SetUnixFileMode(containerDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
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

    /// <summary>
    /// Creates a two-branch merge conflict for <paramref name="fileName"/> in <paramref name="repoPath"/>.
    /// Commits <paramref name="oursContent"/> on the main branch and <paramref name="theirsContent"/> on a
    /// "feature" branch, then merges feature into main. After this call the repo is in a conflicted state.
    /// </summary>
    private static void CreateMergeConflict(
        string repoPath, string fileName, string oursContent, string theirsContent)
    {
        var sig = new Signature("test-author", "test@example.com", DateTimeOffset.UtcNow);

        using var repo = new Repository(repoPath);
        var mainBranchName = repo.Head.FriendlyName;

        // Create the feature branch at the current HEAD (before any divergence)
        repo.CreateBranch("feature");

        // Commit the 'ours' change on the main branch
        File.WriteAllText(Path.Combine(repoPath, fileName), oursContent);
        Commands.Stage(repo, fileName);
        repo.Commit("ours change", sig, sig);

        // Switch to feature and commit the 'theirs' change
        Commands.Checkout(repo, repo.Branches["feature"]);
        File.WriteAllText(Path.Combine(repoPath, fileName), theirsContent);
        Commands.Stage(repo, fileName);
        repo.Commit("theirs change", sig, sig);

        // Return to main and merge feature — this creates a conflict because both branches
        // independently modified the same file from the same base commit.
        Commands.Checkout(repo, repo.Branches[mainBranchName]);
        repo.Merge(repo.Branches["feature"], sig, new MergeOptions
        {
            FastForwardStrategy = FastForwardStrategy.NoFastForward
        });
    }

    /// <summary>
    /// Thread-safe Serilog sink that collects log events for assertion.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly object _lock = new();
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_lock) { return [.. _events]; } }
        }

        public void Emit(LogEvent logEvent) { lock (_lock) { _events.Add(logEvent); } }
    }

    /// <summary>
    /// xUnit FactAttribute that skips the test on Windows or when running as a privileged (root) process.
    /// Used for tests that require Unix file-permission semantics as a non-root user.
    /// Root bypasses DAC permission bits on Linux, so the simulated lock would not hold.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class SkipOnWindowsFact : FactAttribute
    {
        public SkipOnWindowsFact(string reason = "Not supported on Windows or as root")
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                Skip = $"[Windows] {reason}";
            else if (Environment.IsPrivilegedProcess)
                Skip = $"[Root] {reason}: root bypasses DAC permissions so the simulated lock would not hold";
        }
    }
}

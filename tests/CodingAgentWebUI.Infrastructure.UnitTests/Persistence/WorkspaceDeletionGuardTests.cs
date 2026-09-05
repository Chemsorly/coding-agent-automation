using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="WorkspaceDeletionGuard.TryDelete"/>.
/// Consolidates the workspace-guard scenarios previously duplicated across
/// <see cref="PipelineRunHistoryServiceAdditionalTests"/> and
/// <see cref="PostgresPipelineRunHistoryServiceWorkspaceTests"/>.
/// </summary>
public sealed class WorkspaceDeletionGuardTests : IDisposable
{
    private readonly string _tempBase;
    private readonly Mock<ILogger> _mockLogger;

    public WorkspaceDeletionGuardTests()
    {
        _tempBase = Path.Combine(Path.GetTempPath(), $"wdg-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempBase);
        _mockLogger = new Mock<ILogger>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempBase))
            Directory.Delete(_tempBase, recursive: true);
    }

    // ── null / empty path ────────────────────────────────────────────────────

    [Fact]
    public void TryDelete_NullPath_DoesNothing()
    {
        // TODO: assertion only checks no Warning is logged, but would silently pass if the method
        // threw a non-warning exception. Add act.Should().NotThrow() to properly pin no-exception
        // contract. [review-findings.md WARNING]
        WorkspaceDeletionGuard.TryDelete(null, "run-1", _tempBase, _mockLogger.Object);

        // no side effects — no exception, no warning logged
        _mockLogger.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public void TryDelete_EmptyPath_DoesNothing()
    {
        // TODO: same narrow-assertion issue as TryDelete_NullPath_DoesNothing — add
        // act.Should().NotThrow(). [review-findings.md WARNING]
        WorkspaceDeletionGuard.TryDelete("", "run-1", _tempBase, _mockLogger.Object);

        _mockLogger.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    // ── non-existent path ────────────────────────────────────────────────────

    [Fact]
    public void TryDelete_NonExistentDirectory_DoesNothing()
    {
        var path = Path.Combine(_tempBase, "does-not-exist");

        WorkspaceDeletionGuard.TryDelete(path, "run-1", _tempBase, _mockLogger.Object);

        // TODO: this assertion is tautological — path was never created so BeFalse() is trivially
        // true regardless of method behavior. Replace with act.Should().NotThrow() and a
        // _mockLogger.Verify no Warning call to assert meaningful behavior. [review-findings.md WARNING]
        // directory was never created and still does not exist
        Directory.Exists(path).Should().BeFalse();
    }

    // ── path-traversal / base-equality guard ─────────────────────────────────

    // TODO: no test covers the symlink-skip guard (dirInfo.LinkTarget != null branch in
    // WorkspaceDeletionGuard.cs). This is a security-sensitive code path that prevents following
    // a symlink out of the workspace. Add a test that creates a symlink (Directory.CreateSymbolicLink)
    // and asserts TryDelete does not delete the symlink target. [review-findings.md WARNING]

    [Fact]
    public void TryDelete_PathOutsideBase_DoesNotDelete()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"wdg-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);

        try
        {
            WorkspaceDeletionGuard.TryDelete(outsideDir, "run-1", _tempBase, _mockLogger.Object);

            Directory.Exists(outsideDir).Should().BeTrue("path outside base must not be deleted");
        }
        finally
        {
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void TryDelete_PathEqualsBase_DoesNotDelete()
    {
        // Attempting to delete the base directory itself must be rejected
        WorkspaceDeletionGuard.TryDelete(_tempBase, "run-1", _tempBase, _mockLogger.Object);

        Directory.Exists(_tempBase).Should().BeTrue("deleting the base directory itself must be blocked");
    }

    // ── successful deletion ──────────────────────────────────────────────────

    // TODO: no test covers the catch/log-without-rethrow path (delete throws exception). The old
    // parallel suites had TryDeleteWorkspace_DeleteThrows_LogsAndDoesNotPropagate. Add a test that
    // forces Directory.Delete to throw (e.g. delete the dir between existence check and the call,
    // or make it read-only/locked) and asserts TryDelete does not propagate the exception, preserving
    // the "logs but never throws" contract. [review-findings.md WARNING]
    // TODO: no test verifies the Warning log is emitted when path is outside base, nor the Information
    // log on successful deletion. These logger verifications were present in the old Postgres suite
    // (TryDeleteWorkspace_PathOutsideBase_LogsWarningAndSkips, TryDeleteWorkspace_ValidPath_LogsInformationOnSuccess).
    // Add verification against _mockLogger for both branches. [review-findings.md WARNING]

    [Fact]
    public void TryDelete_ValidPath_DeletesDirectory()
    {
        var runId = Guid.NewGuid().ToString();
        var workspaceDir = Path.Combine(_tempBase, runId);
        Directory.CreateDirectory(workspaceDir);
        File.WriteAllText(Path.Combine(workspaceDir, "output.log"), "agent output");

        WorkspaceDeletionGuard.TryDelete(workspaceDir, runId, _tempBase, _mockLogger.Object);

        Directory.Exists(workspaceDir).Should().BeFalse("successful cleanup must remove the workspace directory");
    }

    [Fact]
    public void TryDelete_ValidPath_DirectoryGoneAfterDelete()
    {
        // TODO: this test is functionally duplicate of TryDelete_ValidPath_DeletesDirectory —
        // both create a workspace dir, call TryDelete, and assert directory is gone. Collapse into
        // one test or make this assert a distinct observable behavior (e.g. nested files removed,
        // or Information log emitted). [review-findings.md WARNING]
        // Separate test: confirms success path via filesystem state (logger mock not asserted —
        // Serilog's generic overload makes Moq verification fragile).
        var runId = Guid.NewGuid().ToString();
        var workspaceDir = Path.Combine(_tempBase, runId);
        Directory.CreateDirectory(workspaceDir);

        WorkspaceDeletionGuard.TryDelete(workspaceDir, runId, _tempBase, _mockLogger.Object);

        Directory.Exists(workspaceDir).Should().BeFalse("successful cleanup must remove the workspace directory");
    }
}

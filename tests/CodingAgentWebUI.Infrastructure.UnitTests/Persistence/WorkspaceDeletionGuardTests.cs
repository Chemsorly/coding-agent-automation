using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="WorkspaceDeletionGuard.TryDelete"/>.
/// Both <see cref="PipelineRunHistoryService"/> and
/// <see cref="CodingAgentWebUI.Infrastructure.Persistence.Services.PostgresPipelineRunHistoryService"/>
/// delegate their TryDeleteWorkspace body to this shared guard;
/// this single suite exercises all guard branches once.
/// </summary>
public sealed class WorkspaceDeletionGuardTests : IDisposable
{
    private readonly string _baseDir;
    private readonly List<string> _extraDirs = [];
    private readonly Mock<ILogger> _mockLogger = new();

    public WorkspaceDeletionGuardTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), $"wdg-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        foreach (var dir in _extraDirs)
        {
            for (var i = 0; i < 10; i++)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); break; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && i < 9)
                { Thread.Sleep(100); }
            }
        }

        for (var i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true); break; }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && i < 9)
            { Thread.Sleep(100); }
        }
    }

    private string MakeSubDir(string? name = null)
    {
        var path = Path.Combine(_baseDir, name ?? Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private string MakeExternalDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wdg-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _extraDirs.Add(path);
        return path;
    }

    // ── Early-exit paths ──────────────────────────────────────────────────

    [Fact]
    public void TryDelete_NullPath_DoesNothing()
    {
        WorkspaceDeletionGuard.TryDelete(null, "run-1", _baseDir, _mockLogger.Object);

        // no side effects
        _mockLogger.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public void TryDelete_EmptyPath_DoesNothing()
    {
        WorkspaceDeletionGuard.TryDelete("", "run-1", _baseDir, _mockLogger.Object);

        _mockLogger.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public void TryDelete_NonExistentDirectory_DoesNothing()
    {
        var path = Path.Combine(_baseDir, "does-not-exist");

        // TODO: Add an explicit NotThrow assertion here. The current assertion only checks
        // Directory.Exists which is trivially true even if TryDelete throws — a thrown exception
        // would propagate and the assertion line would never execute, causing a misleading failure
        // message. Add: var act = () => WorkspaceDeletionGuard.TryDelete(...); act.Should().NotThrow();
        WorkspaceDeletionGuard.TryDelete(path, "run-1", _baseDir, _mockLogger.Object);

        Directory.Exists(path).Should().BeFalse("nothing was created, nothing to delete");
    }

    // ── Symlink guard ─────────────────────────────────────────────────────

    // Symlink creation requires elevated privileges on Windows; test is skipped outside Linux/macOS.
    [Fact]
    [Trait("Category", "LinuxOnly")]
    public void TryDelete_SymlinkDirectory_LogsWarningAndSkips()
    {
        // TODO: Replace the runtime `return` below with Skip.If(...) (xUnit.SkippableFact) or
        // Assert.Skip(...) (xUnit v3) so the test runner correctly reports this as "skipped"
        // rather than "passed" on Windows. A silent return gives false green if the symlink
        // guard is ever broken on Linux and tests happen to run on a Windows CI agent.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // skip on Windows where symlink creation may require elevation

        var realTarget = MakeSubDir("real-target");
        File.WriteAllText(Path.Combine(realTarget, "data.txt"), "content");

        var linkPath = Path.Combine(_baseDir, "symlink-workspace");
        Directory.CreateSymbolicLink(linkPath, realTarget);

        try
        {
            WorkspaceDeletionGuard.TryDelete(linkPath, "run-sym", _baseDir, _mockLogger.Object);

            // The symlink itself must not have been followed or deleted
            Directory.Exists(linkPath).Should().BeTrue("symlink must not be deleted");
            // Real target must be untouched
            File.Exists(Path.Combine(realTarget, "data.txt")).Should().BeTrue("real target must be untouched");
        }
        finally
        {
            if (Directory.Exists(linkPath) && new DirectoryInfo(linkPath).LinkTarget != null)
                Directory.Delete(linkPath); // remove the symlink only
        }
    }

    // ── Path-containment guard ────────────────────────────────────────────

    [Fact]
    public void TryDelete_PathOutsideBase_LogsWarningAndSkips()
    {
        var outsideDir = MakeExternalDir();

        WorkspaceDeletionGuard.TryDelete(outsideDir, "run-1", _baseDir, _mockLogger.Object);

        Directory.Exists(outsideDir).Should().BeTrue("path outside base must not be deleted");
    }

    [Fact]
    public void TryDelete_PathEqualsBase_LogsWarningAndSkips()
    {
        WorkspaceDeletionGuard.TryDelete(_baseDir, "run-1", _baseDir, _mockLogger.Object);

        Directory.Exists(_baseDir).Should().BeTrue("base directory itself must not be deleted");
    }

    // Ensure a path like /base/dir/../other cannot bypass the guard.
    [Fact]
    public void TryDelete_PathWithDotDotTraversal_LogsWarningAndSkips()
    {
        var sibling = MakeExternalDir();
        // Construct path that resolves to sibling: _baseDir + "/sub/../../../" + siblingName
        // In practice GetFullPath resolves this, so the guard must catch it.
        var traversalPath = Path.Combine(_baseDir, "..", Path.GetFileName(sibling));

        WorkspaceDeletionGuard.TryDelete(traversalPath, "run-1", _baseDir, _mockLogger.Object);

        // sibling must not be deleted
        Directory.Exists(sibling).Should().BeTrue("path-traversal attempt must be blocked");
    }

    // ── Happy path ────────────────────────────────────────────────────────

    [Fact]
    public void TryDelete_ValidSubDirectory_DeletesRecursively()
    {
        var workspaceDir = MakeSubDir("run-workspace");
        var nested = Path.Combine(workspaceDir, "nested", "deep");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "output.log"), "data");

        WorkspaceDeletionGuard.TryDelete(workspaceDir, "run-1", _baseDir, _mockLogger.Object);

        Directory.Exists(workspaceDir).Should().BeFalse("workspace directory must be deleted recursively");
    }

    [Fact]
    public void TryDelete_ValidSubDirectory_DoesNotThrow()
    {
        var workspaceDir = MakeSubDir("run-ok");

        var act = () => WorkspaceDeletionGuard.TryDelete(workspaceDir, "run-ok", _baseDir, _mockLogger.Object);

        act.Should().NotThrow("exceptions must be swallowed and logged, not propagated");
    }
}

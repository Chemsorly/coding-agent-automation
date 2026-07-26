using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationWorkspaceManager"/>.
/// Validates: Requirements 9.1, 9.3, 9.4
/// </summary>
public sealed class ConsolidationWorkspaceManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConsolidationWorkspaceManager _sut;

    public ConsolidationWorkspaceManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"workspace-mgr-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _sut = new ConsolidationWorkspaceManager(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void GetWorkspacePath_ReturnsPathUnderConsolidationSubdirectory()
    {
        var runId = Guid.NewGuid().ToString();

        var workspacePath = _sut.GetWorkspacePath(runId);

        workspacePath.Should().StartWith(Path.Combine(_tempDir, "consolidation"));
        workspacePath.Should().Contain(runId);
    }

    [Fact]
    public void GetWorkspacePath_ThrowsOnInvalidGuid()
    {
        var act = () => _sut.GetWorkspacePath("not-a-guid");

        act.Should().Throw<ArgumentException>().WithMessage("*valid GUID*");
    }

    [Fact]
    public void GetWorkspacePath_ThrowsOnNull()
    {
        var act = () => _sut.GetWorkspacePath(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateWorkspace_CreatesDirectoryOnDisk()
    {
        var runId = Guid.NewGuid().ToString();

        var workspacePath = _sut.CreateWorkspace(runId);

        Directory.Exists(workspacePath).Should().BeTrue();
    }

    [Fact]
    public void CreateWorkspace_ReturnsPathMatchingGetWorkspacePath()
    {
        var runId = Guid.NewGuid().ToString();

        var createdPath = _sut.CreateWorkspace(runId);
        var expectedPath = _sut.GetWorkspacePath(runId);

        createdPath.Should().Be(expectedPath);
    }

    [Fact]
    public void CreateWorkspace_IdempotentIfDirectoryExists()
    {
        var runId = Guid.NewGuid().ToString();

        var first = _sut.CreateWorkspace(runId);
        var second = _sut.CreateWorkspace(runId);

        first.Should().Be(second);
        Directory.Exists(first).Should().BeTrue();
    }

    [Fact]
    public void CleanupWorkspaceIfSucceeded_Succeeded_DeletesDirectory()
    {
        var runId = Guid.NewGuid().ToString();
        _sut.CreateWorkspace(runId);
        var workspacePath = _sut.GetWorkspacePath(runId);
        Directory.Exists(workspacePath).Should().BeTrue();

        _sut.CleanupWorkspaceIfSucceeded(runId, ConsolidationRunStatus.Succeeded);

        Directory.Exists(workspacePath).Should().BeFalse();
    }

    [Fact]
    public void CleanupWorkspaceIfSucceeded_Failed_RetainsDirectory()
    {
        var runId = Guid.NewGuid().ToString();
        _sut.CreateWorkspace(runId);
        var workspacePath = _sut.GetWorkspacePath(runId);

        _sut.CleanupWorkspaceIfSucceeded(runId, ConsolidationRunStatus.Failed);

        Directory.Exists(workspacePath).Should().BeTrue();
    }

    [Fact]
    public void CleanupWorkspaceIfSucceeded_NonExistentDirectory_NoOp()
    {
        var runId = Guid.NewGuid().ToString();

        // Should not throw when directory doesn't exist
        var act = () => _sut.CleanupWorkspaceIfSucceeded(runId, ConsolidationRunStatus.Succeeded);

        act.Should().NotThrow();
    }
}

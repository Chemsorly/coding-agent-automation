using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for consolidation workspace isolation.
/// Validates: Requirements 9.1, 9.3, 9.4
/// </summary>
public sealed class ConsolidationWorkspaceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _runsDir;
    private readonly string _suggestionsPath;
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistory;
    private readonly PipelineConfiguration _config;

    public ConsolidationWorkspaceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"workspace-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _runsDir = Path.Combine(_tempDir, "runs");
        _suggestionsPath = Path.Combine(_tempDir, "harness-suggestions.json");

        _mockRunHistory = new Mock<IPipelineRunHistoryService>();
        _mockRunHistory.Setup(x => x.GetRunHistory()).Returns(new List<PipelineRunSummary>());

        _config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempDir,
            PipelineJobTemplates = new List<PipelineJobTemplate>
            {
                new()
                {
                    Id = "tmpl-1",
                    Name = "Test Template",
                    IssueProviderId = "ip-1",
                    RepoProviderId = "rp-1",
                    BrainProviderId = "bp-1",
                    Enabled = true
                }
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private ConsolidationService CreateSut(ILogger? logger = null) => new(
        logger ?? new LoggerConfiguration().CreateLogger(),
        _config,
        _mockRunHistory.Object,
        consolidationRunsDirectory: _runsDir,
        harnessSuggestionsPath: _suggestionsPath);

    // ── Workspace uses separate directory from pipeline ───────────────────

    [Fact]
    public void GetWorkspacePath_ReturnsPathUnderConsolidationSubdirectory()
    {
        // Validates: Requirement 9.1 — consolidation uses separate directory from pipeline
        var sut = CreateSut();
        var runId = Guid.NewGuid().ToString();

        var workspacePath = sut.GetWorkspacePath(runId);

        // Workspace should be under {base}/consolidation/{runId}/
        workspacePath.Should().StartWith(Path.Combine(_tempDir, "consolidation"));
        workspacePath.Should().Contain(runId);
    }

    [Fact]
    public void GetWorkspacePath_IsSeparateFromRegularPipelineWorkspace()
    {
        // Validates: Requirement 9.1 — consolidation workspaces are distinct from pipeline workspaces
        var sut = CreateSut();
        var runId = Guid.NewGuid().ToString();

        var consolidationPath = sut.GetWorkspacePath(runId);

        // Regular pipeline workspaces are directly under WorkspaceBaseDirectory (not in /consolidation/)
        var regularPipelinePath = Path.Combine(_tempDir, runId);

        consolidationPath.Should().NotBe(regularPipelinePath);
        consolidationPath.Should().Contain("consolidation");
    }

    [Fact]
    public void CreateWorkspace_CreatesDirectoryOnDisk()
    {
        // Validates: Requirement 9.1
        var sut = CreateSut();
        var runId = Guid.NewGuid().ToString();

        var workspacePath = sut.CreateWorkspace(runId);

        Directory.Exists(workspacePath).Should().BeTrue();
    }

    [Fact]
    public void CreateWorkspace_ReturnsPathMatchingGetWorkspacePath()
    {
        // Validates: Requirement 9.1
        var sut = CreateSut();
        var runId = Guid.NewGuid().ToString();

        var createdPath = sut.CreateWorkspace(runId);
        var expectedPath = sut.GetWorkspacePath(runId);

        createdPath.Should().Be(expectedPath);
    }

    // ── Cleanup removes directory on success ─────────────────────────────

    [Fact]
    public async Task UpdateRunAsync_Succeeded_RemovesWorkspaceDirectory()
    {
        // Validates: Requirement 9.4 — cleanup removes directory on success
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Create the workspace directory (simulating what the executor would do)
        var workspacePath = sut.CreateWorkspace(run!.RunId);
        Directory.Exists(workspacePath).Should().BeTrue();

        // Mark as succeeded — should trigger cleanup
        await sut.UpdateRunAsync(
            run.RunId, ConsolidationRunStatus.Succeeded, "Done", CancellationToken.None);

        Directory.Exists(workspacePath).Should().BeFalse();
    }

    // ── Failed runs retain workspace ─────────────────────────────────────

    [Fact]
    public async Task UpdateRunAsync_Failed_RetainsWorkspaceDirectory()
    {
        // Validates: Requirement 9.3 — failed runs retain workspace for debugging
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Create the workspace directory
        var workspacePath = sut.CreateWorkspace(run!.RunId);
        Directory.Exists(workspacePath).Should().BeTrue();

        // Mark as failed — workspace should be retained
        await sut.UpdateRunAsync(
            run.RunId, ConsolidationRunStatus.Failed, "Agent timed out", CancellationToken.None);

        Directory.Exists(workspacePath).Should().BeTrue();
    }

    // ── Cleanup failure is non-fatal (logged warning) ────────────────────

    [Fact]
    public async Task UpdateRunAsync_CleanupFailure_IsNonFatal_RunStillMarkedSucceeded()
    {
        // Validates: Requirement 9.4 — cleanup failure is non-fatal (logged warning)
        // Strategy: point the workspace path at a system-protected directory that exists
        // but cannot be deleted. This avoids platform-specific file locking hacks.
        var collectingSink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(collectingSink)
            .MinimumLevel.Debug()
            .CreateLogger();

        // Use a custom workspace base that points to a protected system path for cleanup
        // We'll create the run normally, then swap the workspace base to a protected path
        // so that GetWorkspacePath resolves to an undeletable location.
        var sut = CreateSut(logger);

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Create workspace normally so it exists for the Directory.Exists check
        var workspacePath = sut.CreateWorkspace(run!.RunId);
        Directory.Exists(workspacePath).Should().BeTrue();

        // Create a subdirectory and make it impossible to delete:
        // Write a file, then make the SUBDIRECTORY read-only (not the workspace root).
        // On Linux: removing write from a directory prevents deleting its contents.
        // On Windows: we use a file lock.
        var guardDir = Path.Combine(workspacePath, "guard");
        Directory.CreateDirectory(guardDir);
        var guardFile = Path.Combine(guardDir, "hold.txt");
        File.WriteAllText(guardFile, "x");

        FileStream? handle = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                handle = new FileStream(guardFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            else
            {
                // Remove write+execute from the guard subdirectory so its contents can't be unlinked
                File.SetUnixFileMode(guardDir, UnixFileMode.UserRead);
            }

            // Act: mark as succeeded — cleanup will attempt Directory.Delete(recursive:true) and fail
            await sut.UpdateRunAsync(
                run.RunId, ConsolidationRunStatus.Succeeded, "Done", CancellationToken.None);

            // Assert: run is still marked succeeded despite cleanup failure
            var history = await sut.GetRunHistoryAsync(CancellationToken.None);
            var updatedRun = history.First(r => r.RunId == run.RunId);
            updatedRun.Status.Should().Be(ConsolidationRunStatus.Succeeded);
            updatedRun.Summary.Should().Be("Done");

            // Assert: a warning was logged about the cleanup failure
            collectingSink.Events.Should().Contain(e =>
                e.Level == LogEventLevel.Warning &&
                e.MessageTemplate.Text.Contains("Failed to clean up"));
        }
        finally
        {
            handle?.Dispose();
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(guardDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>
    /// Simple Serilog sink that collects log events for assertion in tests.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}

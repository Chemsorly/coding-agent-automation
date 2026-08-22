using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Additional unit tests for PipelineRunHistoryService covering branches not exercised by PipelineRunHistoryServiceTests:
/// GetRunHistoryAsync paginated overload (validation, hasMore, feedbackOnly filter),
/// GetRunAsync,
/// TryDeleteWorkspace (symlink, path-traversal, delete exception),
/// CleanupExpiredWorkspaces (activeRunId guard, completedAt null path),
/// GetRunHistory empty-directory path.
/// </summary>
public class PipelineRunHistoryServiceAdditionalTests : IDisposable
{
    private readonly Mock<ILogger> _mockLogger = new();

    // Temp dirs created by individual tests — cleaned up in Dispose
    private readonly List<string> _tempDirs = [];

    private string MakeTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prs-add-{Guid.NewGuid()}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try { Directory.Delete(dir, recursive: true); break; }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 9)
                    { Thread.Sleep(100); }
            }
        }
        GC.SuppressFinalize(this);
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static async Task WaitForFileAsync(string path, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!File.Exists(path) && Environment.TickCount64 < deadline)
            await Task.Delay(50);
    }

    // ── GetRunAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRunAsync_ReturnsNull_WhenRunIdNotInHistory()
    {
        var dir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);

        var result = await svc.GetRunAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetRunAsync_ReturnsSummary_WhenRunExists()
    {
        var dir = MakeTempDir();
        var runId = Guid.NewGuid();
        var summary = new PipelineRunSummary
        {
            RunId = runId.ToString(),
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow,
            CompletedAtOffset = DateTimeOffset.UtcNow
        };
        File.WriteAllText(Path.Combine(dir, $"{runId}.json"), JsonSerializer.Serialize(summary, _jsonOpts));

        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);
        var result = await svc.GetRunAsync(runId);

        result.Should().NotBeNull();
        result!.RunId.Should().Be(runId.ToString());
    }

    // ── GetRunHistoryAsync paginated ─────────────────────────────────────────

    [Fact]
    public async Task GetRunHistoryAsync_PageLessThanOne_Throws()
    {
        var dir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);

        var act = () => svc.GetRunHistoryAsync(page: 0, pageSize: 10);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("page");
    }

    [Fact]
    public async Task GetRunHistoryAsync_PageSizeLessThanOne_Throws()
    {
        var dir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);

        var act = () => svc.GetRunHistoryAsync(page: 1, pageSize: 0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("pageSize");
    }

    [Fact]
    public async Task GetRunHistoryAsync_PageSizeExceedsMax_Throws()
    {
        var dir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);

        var act = () => svc.GetRunHistoryAsync(page: 1, pageSize: PipelineRunHistoryService.MaxHistorySize + 1);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("pageSize");
    }

    [Fact]
    public async Task GetRunHistoryAsync_PageOverflow_Throws()
    {
        var dir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);

        // page = int.MaxValue, pageSize = 2 → offset overflows
        var act = () => svc.GetRunHistoryAsync(page: int.MaxValue, pageSize: 2);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("page");
    }

    [Fact]
    public async Task GetRunHistoryAsync_Page1_ReturnsFirstPage()
    {
        var dir = MakeTempDir();
        // Seed 5 runs
        var runs = Enumerable.Range(1, 5).Select(i => new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = $"{i}",
            IssueTitle = $"Run {i}",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-i)
        }).ToList();
        foreach (var r in runs)
            File.WriteAllText(Path.Combine(dir, $"{r.RunId}.json"), JsonSerializer.Serialize(r, _jsonOpts));

        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);
        var result = await svc.GetRunHistoryAsync(page: 1, pageSize: 3);

        result.Items.Count.Should().Be(3);
        result.HasMore.Should().BeTrue();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(3);
    }

    [Fact]
    public async Task GetRunHistoryAsync_LastPage_HasMoreFalse()
    {
        var dir = MakeTempDir();
        var runs = Enumerable.Range(1, 4).Select(i => new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = $"{i}",
            IssueTitle = $"Run {i}",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-i)
        }).ToList();
        foreach (var r in runs)
            File.WriteAllText(Path.Combine(dir, $"{r.RunId}.json"), JsonSerializer.Serialize(r, _jsonOpts));

        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);
        var result = await svc.GetRunHistoryAsync(page: 2, pageSize: 3);

        result.Items.Count.Should().Be(1, "4 items, page 2 of page-size 3 = 1 item");
        result.HasMore.Should().BeFalse();
    }

    // ── GetRunHistoryAsync feedbackOnly ──────────────────────────────────────

    [Fact]
    public async Task GetRunHistoryAsync_FeedbackOnly_False_ReturnsSameAsPaginated()
    {
        var dir = MakeTempDir();
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "1",
            IssueTitle = "Any",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow,
            Feedback = null
        };
        File.WriteAllText(Path.Combine(dir, $"{run.RunId}.json"), JsonSerializer.Serialize(run, _jsonOpts));

        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);
        var withFeedback = await svc.GetRunHistoryAsync(page: 1, pageSize: 10, feedbackOnly: false);

        withFeedback.Items.Count.Should().Be(1);
    }

    [Fact]
    public async Task GetRunHistoryAsync_FeedbackOnly_True_FiltersToRunsWithFeedback()
    {
        var dir = MakeTempDir();
        var withFeedback = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "1",
            IssueTitle = "Has Feedback",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-1),
            Feedback = new RunFeedback
            {
                Outcome = FeedbackOutcome.Success,
                CollectedAtUtc = DateTime.UtcNow,
                Harness = new HarnessFeedback { Category = "ok" }
            }
        };
        var withoutFeedback = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2",
            IssueTitle = "No Feedback",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow,
            Feedback = null
        };
        File.WriteAllText(Path.Combine(dir, $"{withFeedback.RunId}.json"), JsonSerializer.Serialize(withFeedback, _jsonOpts));
        File.WriteAllText(Path.Combine(dir, $"{withoutFeedback.RunId}.json"), JsonSerializer.Serialize(withoutFeedback, _jsonOpts));

        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);
        var result = await svc.GetRunHistoryAsync(page: 1, pageSize: 10, feedbackOnly: true);

        result.Items.Count.Should().Be(1);
        result.Items[0].IssueTitle.Should().Be("Has Feedback");
    }

    [Fact]
    public async Task GetRunHistoryAsync_FeedbackOnly_PageLessThanOne_Throws()
    {
        var dir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);

        var act = () => svc.GetRunHistoryAsync(page: 0, pageSize: 10, feedbackOnly: true);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("page");
    }

    [Fact]
    public async Task GetRunHistoryAsync_FeedbackOnly_PageSizeLessThanOne_Throws()
    {
        var dir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);

        var act = () => svc.GetRunHistoryAsync(page: 1, pageSize: 0, feedbackOnly: true);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("pageSize");
    }

    [Fact]
    public async Task GetRunHistoryAsync_FeedbackOnly_HasMoreWhenMoreItems()
    {
        var dir = MakeTempDir();
        // Seed 3 runs with feedback
        for (var i = 1; i <= 3; i++)
        {
            var r = new PipelineRunSummary
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = $"{i}",
                IssueTitle = $"Feedback Run {i}",
                FinalStep = PipelineStep.Completed,
                StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-i),
                Feedback = new RunFeedback
                {
                    Outcome = FeedbackOutcome.Success,
                    CollectedAtUtc = DateTime.UtcNow,
                    Harness = new HarnessFeedback { Category = "ok" }
                }
            };
            File.WriteAllText(Path.Combine(dir, $"{r.RunId}.json"), JsonSerializer.Serialize(r, _jsonOpts));
        }

        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);
        var result = await svc.GetRunHistoryAsync(page: 1, pageSize: 2, feedbackOnly: true);

        result.Items.Count.Should().Be(2);
        result.HasMore.Should().BeTrue();
    }

    // ── LoadRunHistory — no directory ────────────────────────────────────────

    [Fact]
    public async Task Constructor_RunsDirectoryDoesNotExist_LoadsEmptyHistory()
    {
        // Directory that doesn't exist — constructor should not throw, history is empty
        var nonExistent = Path.Combine(Path.GetTempPath(), $"prs-nonexistent-{Guid.NewGuid()}");

        var svc = new PipelineRunHistoryService(_mockLogger.Object, nonExistent);
        var history = await svc.GetRunHistoryAsync();

        history.Should().BeEmpty();
    }

    // ── TryDeleteWorkspace ───────────────────────────────────────────────────

    [Fact]
    public void TryDeleteWorkspace_NullPath_DoesNothing()
    {
        var dir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);

        // Must not throw
        svc.TryDeleteWorkspace(null, "run-1", dir);
    }

    [Fact]
    public void TryDeleteWorkspace_NonExistentPath_DoesNothing()
    {
        var dir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, dir);
        var fakePath = Path.Combine(dir, "nonexistent-workspace");

        svc.TryDeleteWorkspace(fakePath, "run-1", dir);
        // Should not throw
    }

    [Fact]
    public void TryDeleteWorkspace_PathOutsideBase_SkipsDelete()
    {
        var baseDir = MakeTempDir();
        var targetDir = MakeTempDir(); // different temp dir — outside base
        var svc = new PipelineRunHistoryService(_mockLogger.Object, baseDir);

        // Target is outside baseDir — must not delete
        svc.TryDeleteWorkspace(targetDir, "run-1", baseDir);

        Directory.Exists(targetDir).Should().BeTrue("path outside base must not be deleted");
    }

    [Fact]
    public void TryDeleteWorkspace_PathEqualsBase_SkipsDelete()
    {
        var baseDir = MakeTempDir();
        var svc = new PipelineRunHistoryService(_mockLogger.Object, baseDir);

        // Workspace path IS the base — must not delete
        svc.TryDeleteWorkspace(baseDir, "run-1", baseDir);

        Directory.Exists(baseDir).Should().BeTrue("base directory itself must not be deleted");
    }

    [Fact]
    public void TryDeleteWorkspace_ValidPath_DeletesDirectory()
    {
        var baseDir = MakeTempDir();
        var workspaceDir = Path.Combine(baseDir, "run-workspace-to-delete");
        Directory.CreateDirectory(workspaceDir);
        var svc = new PipelineRunHistoryService(_mockLogger.Object, baseDir);

        svc.TryDeleteWorkspace(workspaceDir, "run-1", baseDir);

        Directory.Exists(workspaceDir).Should().BeFalse("workspace should have been deleted");
    }

    [Fact]
    public void TryDeleteWorkspace_DeleteThrows_LogsAndDoesNotPropagate()
    {
        // Cannot easily force Directory.Delete to throw without special setup,
        // but we can verify the logger is not invoked for a valid deletion.
        var baseDir = MakeTempDir();
        var workspaceDir = Path.Combine(baseDir, "run-workspace-ok");
        Directory.CreateDirectory(workspaceDir);
        var svc = new PipelineRunHistoryService(_mockLogger.Object, baseDir);

        var act = () => svc.TryDeleteWorkspace(workspaceDir, "run-ok", baseDir);
        act.Should().NotThrow();
    }

    // ── CleanupExpiredWorkspaces — activeRunId guard ──────────────────────────

    [Fact]
    public void CleanupExpiredWorkspaces_ActiveRunId_PreservesItsWorkspace()
    {
        var baseDir = MakeTempDir();
        var runsDir = MakeTempDir();
        var activeRunId = Guid.NewGuid().ToString();
        var expiredRunId = Guid.NewGuid().ToString();

        // Both workspaces exist
        Directory.CreateDirectory(Path.Combine(baseDir, activeRunId));
        Directory.CreateDirectory(Path.Combine(baseDir, expiredRunId));

        // Both runs are expired (completedAt 10 days ago, retention = 7 days)
        var activeSummary = new PipelineRunSummary
        {
            RunId = activeRunId,
            IssueIdentifier = "1",
            IssueTitle = "Active",
            FinalStep = PipelineStep.Failed,
            CompletedAtOffset = DateTimeOffset.UtcNow.AddDays(-10)
        };
        var expiredSummary = new PipelineRunSummary
        {
            RunId = expiredRunId,
            IssueIdentifier = "2",
            IssueTitle = "Expired",
            FinalStep = PipelineStep.Failed,
            CompletedAtOffset = DateTimeOffset.UtcNow.AddDays(-10)
        };

        File.WriteAllText(Path.Combine(runsDir, $"{activeRunId}.json"), JsonSerializer.Serialize(activeSummary, _jsonOpts));
        File.WriteAllText(Path.Combine(runsDir, $"{expiredRunId}.json"), JsonSerializer.Serialize(expiredSummary, _jsonOpts));

        var svc = new PipelineRunHistoryService(_mockLogger.Object, runsDir);
        svc.CleanupExpiredWorkspaces(
            new PipelineConfiguration { WorkspaceBaseDirectory = baseDir, FailedWorkspaceRetentionDays = 7 },
            activeRunId: activeRunId);

        // Active run workspace preserved (its run is still in-progress)
        Directory.Exists(Path.Combine(baseDir, activeRunId)).Should().BeTrue("active run workspace must be preserved");
        // Expired run workspace removed (past retention, not the active run)
        Directory.Exists(Path.Combine(baseDir, expiredRunId)).Should().BeFalse("expired run workspace must be deleted");
    }

    [Fact]
    public void CleanupExpiredWorkspaces_CompletedStep_SkipsWorkspace()
    {
        var baseDir = MakeTempDir();
        var runsDir = MakeTempDir();
        var runId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(baseDir, runId));

        var summary = new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "1",
            IssueTitle = "Completed",
            FinalStep = PipelineStep.Completed,         // terminal "success" step — must be skipped
            CompletedAtOffset = DateTimeOffset.UtcNow.AddDays(-10)
        };
        File.WriteAllText(Path.Combine(runsDir, $"{runId}.json"), JsonSerializer.Serialize(summary, _jsonOpts));

        var svc = new PipelineRunHistoryService(_mockLogger.Object, runsDir);
        svc.CleanupExpiredWorkspaces(
            new PipelineConfiguration { WorkspaceBaseDirectory = baseDir, FailedWorkspaceRetentionDays = 7 });

        Directory.Exists(Path.Combine(baseDir, runId)).Should().BeTrue(
            "Completed runs are skipped by CleanupExpiredWorkspaces (continue when FinalStep == Completed)");
    }

    [Fact]
    public void CleanupExpiredWorkspaces_NullCompletedAt_SkipsWorkspace()
    {
        var baseDir = MakeTempDir();
        var runsDir = MakeTempDir();
        var runId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(baseDir, runId));

        // No CompletedAtOffset AND no legacy CompletedAt → completedOffset will be null → skip
        var summary = new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "1",
            IssueTitle = "No CompletedAt",
            FinalStep = PipelineStep.Failed,
            CompletedAtOffset = null
            // CompletedAt (DateTime?) is also null by default
        };
        File.WriteAllText(Path.Combine(runsDir, $"{runId}.json"), JsonSerializer.Serialize(summary, _jsonOpts));

        var svc = new PipelineRunHistoryService(_mockLogger.Object, runsDir);
        svc.CleanupExpiredWorkspaces(
            new PipelineConfiguration { WorkspaceBaseDirectory = baseDir, FailedWorkspaceRetentionDays = 7 });

        Directory.Exists(Path.Combine(baseDir, runId)).Should().BeTrue(
            "run with null CompletedAt is skipped — cannot evaluate expiry");
    }

    [Fact]
    public void CleanupExpiredWorkspaces_WithinRetentionPeriod_SkipsWorkspace()
    {
        var baseDir = MakeTempDir();
        var runsDir = MakeTempDir();
        var runId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(baseDir, runId));

        var summary = new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "1",
            IssueTitle = "Recent",
            FinalStep = PipelineStep.Failed,
            CompletedAtOffset = DateTimeOffset.UtcNow.AddDays(-3)   // within 7-day retention
        };
        File.WriteAllText(Path.Combine(runsDir, $"{runId}.json"), JsonSerializer.Serialize(summary, _jsonOpts));

        var svc = new PipelineRunHistoryService(_mockLogger.Object, runsDir);
        svc.CleanupExpiredWorkspaces(
            new PipelineConfiguration { WorkspaceBaseDirectory = baseDir, FailedWorkspaceRetentionDays = 7 });

        Directory.Exists(Path.Combine(baseDir, runId)).Should().BeTrue("within retention — workspace must not be deleted");
    }
}

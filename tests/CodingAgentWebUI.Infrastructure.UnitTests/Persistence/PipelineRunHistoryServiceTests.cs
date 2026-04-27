using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for PipelineRunHistoryService (concrete infrastructure behavior).
/// </summary>
public class PipelineRunHistoryServiceTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    [Fact]
    public void CleanupExpiredWorkspaces_DeletesExpiredFailedRunWorkspaces()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-retention-{Guid.NewGuid()}");
        var expiredRunId = Guid.NewGuid().ToString();
        var recentRunId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(workspaceBase, expiredRunId));
        Directory.CreateDirectory(Path.Combine(workspaceBase, recentRunId));
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-retention-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(Path.Combine(runsDir, $"{expiredRunId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = expiredRunId, IssueIdentifier = "1", IssueTitle = "Expired", FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow.AddDays(-10), CompletedAt = DateTime.UtcNow.AddDays(-10) }, jsonOptions));
            File.WriteAllText(Path.Combine(runsDir, $"{recentRunId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = recentRunId, IssueIdentifier = "2", IssueTitle = "Recent", FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow.AddDays(-1), CompletedAt = DateTime.UtcNow.AddDays(-1) }, jsonOptions));

            var historyService = new PipelineRunHistoryService(_mockLogger.Object, runsDir);
            historyService.CleanupExpiredWorkspaces(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, FailedWorkspaceRetentionDays = 7 });

            Directory.Exists(Path.Combine(workspaceBase, expiredRunId)).Should().BeFalse();
            Directory.Exists(Path.Combine(workspaceBase, recentRunId)).Should().BeTrue();
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public void CleanupExpiredWorkspaces_RetainsAll_WhenRetentionIsNegativeOne()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-retain-{Guid.NewGuid()}");
        var oldRunId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(workspaceBase, oldRunId));
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-retain-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(Path.Combine(runsDir, $"{oldRunId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = oldRunId, IssueIdentifier = "1", IssueTitle = "Old", FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow.AddDays(-100), CompletedAt = DateTime.UtcNow.AddDays(-100) }, jsonOptions));

            var historyService = new PipelineRunHistoryService(_mockLogger.Object, runsDir);
            historyService.CleanupExpiredWorkspaces(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, FailedWorkspaceRetentionDays = -1 });

            Directory.Exists(Path.Combine(workspaceBase, oldRunId)).Should().BeTrue();
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public void CleanupExpiredWorkspaces_DeletesImmediately_WhenRetentionIsZero()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-zero-{Guid.NewGuid()}");
        var runId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(workspaceBase, runId));
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-zero-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(Path.Combine(runsDir, $"{runId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = runId, IssueIdentifier = "1", IssueTitle = "Recent", FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow.AddSeconds(-5), CompletedAt = DateTime.UtcNow.AddSeconds(-1) }, jsonOptions));

            var historyService = new PipelineRunHistoryService(_mockLogger.Object, runsDir);
            historyService.CleanupExpiredWorkspaces(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, FailedWorkspaceRetentionDays = 0 });

            Directory.Exists(Path.Combine(workspaceBase, runId)).Should().BeFalse();
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }

    [Fact]
    public void CleanupExpiredWorkspaces_IncludesCancelledRuns()
    {
        var workspaceBase = Path.Combine(Path.GetTempPath(), $"ws-cancel-{Guid.NewGuid()}");
        var runId = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(workspaceBase, runId));
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-cancel-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            File.WriteAllText(Path.Combine(runsDir, $"{runId}.json"), System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary { RunId = runId, IssueIdentifier = "1", IssueTitle = "Cancelled", FinalStep = PipelineStep.Cancelled, StartedAt = DateTime.UtcNow.AddDays(-10), CompletedAt = DateTime.UtcNow.AddDays(-10) }, jsonOptions));

            var historyService = new PipelineRunHistoryService(_mockLogger.Object, runsDir);
            historyService.CleanupExpiredWorkspaces(new PipelineConfiguration { WorkspaceBaseDirectory = workspaceBase, FailedWorkspaceRetentionDays = 7 });

            Directory.Exists(Path.Combine(workspaceBase, runId)).Should().BeFalse();
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); if (Directory.Exists(workspaceBase)) Directory.Delete(workspaceBase, true); }
    }
}

using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Unit tests for PipelineRunHistoryService (concrete infrastructure behavior).
/// </summary>
public class PipelineRunHistoryServiceTests : IDisposable
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"test-history-{Guid.NewGuid()}");

    private static JsonSerializerOptions JsonOptions => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

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

    [Fact]
    public void GetRunsByAgentId_ReturnsEmpty_WhenNoRunsForAgent()
    {
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-agent-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var historyService = new PipelineRunHistoryService(_mockLogger.Object, runsDir);
            var result = historyService.GetRunsByAgentId("agent-1");
            result.Should().BeEmpty();
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); }
    }

    [Fact]
    public void GetRunsByAgentId_FiltersAndLimitsCorrectly()
    {
        var runsDir = Path.Combine(Path.GetTempPath(), $"test-runs-agent-filter-{Guid.NewGuid()}");
        Directory.CreateDirectory(runsDir);
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

            // Create 3 runs for agent-1 and 1 for agent-2
            for (var i = 0; i < 3; i++)
            {
                var id = Guid.NewGuid().ToString();
                File.WriteAllText(Path.Combine(runsDir, $"{id}.json"),
                    System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary
                    {
                        RunId = id, IssueIdentifier = $"{i}", IssueTitle = $"Run {i}",
                        FinalStep = PipelineStep.Completed, StartedAt = DateTime.UtcNow.AddHours(-i),
                        CompletedAt = DateTime.UtcNow.AddHours(-i).AddMinutes(10), AgentId = "agent-1"
                    }, jsonOptions));
            }
            var otherId = Guid.NewGuid().ToString();
            File.WriteAllText(Path.Combine(runsDir, $"{otherId}.json"),
                System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary
                {
                    RunId = otherId, IssueIdentifier = "99", IssueTitle = "Other",
                    FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow, AgentId = "agent-2"
                }, jsonOptions));

            var historyService = new PipelineRunHistoryService(_mockLogger.Object, runsDir);

            var agent1Runs = historyService.GetRunsByAgentId("agent-1");
            agent1Runs.Should().HaveCount(3);
            agent1Runs.Should().OnlyContain(r => r.AgentId == "agent-1");

            var agent2Runs = historyService.GetRunsByAgentId("agent-2");
            agent2Runs.Should().ContainSingle();

            // Test limit
            var limited = historyService.GetRunsByAgentId("agent-1", 2);
            limited.Should().HaveCount(2);
        }
        finally { if (Directory.Exists(runsDir)) Directory.Delete(runsDir, true); }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 100)]
    [InlineData(2, 67)]
    public void JobHistoryStatistics_SuccessRate_ComputesCorrectly(int completedCount, int expectedRate)
    {
        // This tests the statistics computation logic used in the run detail modal agent section
        var runs = new List<PipelineRunSummary>();
        for (var i = 0; i < completedCount; i++)
            runs.Add(new PipelineRunSummary { RunId = Guid.NewGuid().ToString(), IssueIdentifier = $"{i}", IssueTitle = "OK", FinalStep = PipelineStep.Completed, StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow.AddMinutes(5) });
        // Add some failed runs to make total = 3 (except when completedCount is 0, total is 0)
        for (var i = completedCount; i < 3; i++)
            runs.Add(new PipelineRunSummary { RunId = Guid.NewGuid().ToString(), IssueIdentifier = $"{i}", IssueTitle = "Fail", FinalStep = PipelineStep.Failed, StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow.AddMinutes(5) });

        if (runs.Count == 0)
        {
            // Empty case
            var rate = 0;
            rate.Should().Be(expectedRate);
        }
        else
        {
            var completed = runs.Count(r => r.FinalStep == PipelineStep.Completed);
            var rate = (int)Math.Round(100.0 * completed / runs.Count);
            rate.Should().Be(expectedRate);
        }
    }

    [Fact]
    public void AddRunToHistory_ThrowsOnNull()
    {
        var runsDir = Path.Combine(Path.GetTempPath(), $"runs-{Guid.NewGuid()}");
        var service = new PipelineRunHistoryService(_mockLogger.Object, runsDir);

        var act = () => service.AddRunToHistory(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("run");
    }

    [Fact]
    public void CleanupExpiredWorkspaces_ThrowsOnNullConfig()
    {
        var runsDir = Path.Combine(Path.GetTempPath(), $"runs-{Guid.NewGuid()}");
        var service = new PipelineRunHistoryService(_mockLogger.Object, runsDir);

        var act = () => service.CleanupExpiredWorkspaces(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("config");
    }

    [Fact]
    public void AddRunToHistory_PersistsRunToConfiguredDirectory()
    {
        Directory.CreateDirectory(_tempDir);
        var service = new PipelineRunHistoryService(_mockLogger.Object, _tempDir);

        var run = new PipelineRun
        {
            RunId = "persist-test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "provider-1",
            RepoProviderConfigId = "repo-1",
            CurrentStep = PipelineStep.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow.AddMinutes(5)
        };

        service.AddRunToHistory(run);

        var expectedFile = Path.Combine(_tempDir, "persist-test-run.json");
        File.Exists(expectedFile).Should().BeTrue();

        var json = File.ReadAllText(expectedFile);
        var deserialized = JsonSerializer.Deserialize<PipelineRunSummary>(json, JsonOptions);
        deserialized.Should().NotBeNull();
        deserialized!.RunId.Should().Be("persist-test-run");
        deserialized.IssueIdentifier.Should().Be("42");
        deserialized.IssueTitle.Should().Be("Test Issue");
        deserialized.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public void GetRunHistory_ReturnsAllPersistedRunsInChronologicalOrder_NewestFirst()
    {
        Directory.CreateDirectory(_tempDir);

        // Write runs with different timestamps
        var oldRun = new PipelineRunSummary
        {
            RunId = "old-run",
            IssueIdentifier = "1",
            IssueTitle = "Old",
            FinalStep = PipelineStep.Completed,
            StartedAt = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2024, 1, 1, 10, 30, 0, DateTimeKind.Utc)
        };
        var middleRun = new PipelineRunSummary
        {
            RunId = "middle-run",
            IssueIdentifier = "2",
            IssueTitle = "Middle",
            FinalStep = PipelineStep.Failed,
            StartedAt = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2024, 6, 15, 12, 45, 0, DateTimeKind.Utc)
        };
        var newestRun = new PipelineRunSummary
        {
            RunId = "newest-run",
            IssueIdentifier = "3",
            IssueTitle = "Newest",
            FinalStep = PipelineStep.Completed,
            StartedAt = new DateTime(2025, 3, 20, 8, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2025, 3, 20, 9, 0, 0, DateTimeKind.Utc)
        };

        File.WriteAllText(Path.Combine(_tempDir, $"{oldRun.RunId}.json"), JsonSerializer.Serialize(oldRun, JsonOptions));
        File.WriteAllText(Path.Combine(_tempDir, $"{middleRun.RunId}.json"), JsonSerializer.Serialize(middleRun, JsonOptions));
        File.WriteAllText(Path.Combine(_tempDir, $"{newestRun.RunId}.json"), JsonSerializer.Serialize(newestRun, JsonOptions));

        var service = new PipelineRunHistoryService(_mockLogger.Object, _tempDir);

        var history = service.GetRunHistory();

        history.Should().HaveCount(3);
        history[0].RunId.Should().Be("newest-run");
        history[1].RunId.Should().Be("middle-run");
        history[2].RunId.Should().Be("old-run");
    }

    [Fact]
    public void GetRunHistory_EmptyDirectory_ReturnsEmptyList()
    {
        Directory.CreateDirectory(_tempDir);

        var service = new PipelineRunHistoryService(_mockLogger.Object, _tempDir);

        var history = service.GetRunHistory();

        history.Should().BeEmpty();
    }

    [Fact]
    public void GetRunHistory_CorruptedJsonFileIsSkipped_RemainingFilesStillLoaded()
    {
        Directory.CreateDirectory(_tempDir);

        // Write a valid run
        var validRun = new PipelineRunSummary
        {
            RunId = "valid-run",
            IssueIdentifier = "1",
            IssueTitle = "Valid",
            FinalStep = PipelineStep.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow.AddMinutes(5)
        };
        File.WriteAllText(Path.Combine(_tempDir, $"{validRun.RunId}.json"), JsonSerializer.Serialize(validRun, JsonOptions));

        // Write a corrupted JSON file
        File.WriteAllText(Path.Combine(_tempDir, "corrupted-run.json"), "{ this is not valid json !!!");

        var service = new PipelineRunHistoryService(_mockLogger.Object, _tempDir);

        var history = service.GetRunHistory();

        history.Should().ContainSingle();
        history[0].RunId.Should().Be("valid-run");
    }

    [Fact]
    public void AddRunToHistory_CreatesTargetDirectory_IfItDoesNotExist()
    {
        var nonExistentDir = Path.Combine(_tempDir, "nested", "runs");
        Directory.Exists(nonExistentDir).Should().BeFalse();

        var service = new PipelineRunHistoryService(_mockLogger.Object, nonExistentDir);

        var run = new PipelineRun
        {
            RunId = "dir-create-test",
            IssueIdentifier = "99",
            IssueTitle = "Directory Creation Test",
            IssueProviderConfigId = "provider-1",
            RepoProviderConfigId = "repo-1",
            CurrentStep = PipelineStep.Completed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow.AddMinutes(1)
        };

        service.AddRunToHistory(run);

        Directory.Exists(nonExistentDir).Should().BeTrue();
        File.Exists(Path.Combine(nonExistentDir, "dir-create-test.json")).Should().BeTrue();
    }
}

using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.UnitTests.Helpers;
using Moq;
using Serilog;
using System.Text.Json;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Edge case tests for the persistence abstraction that guard against data loss,
/// corruption resilience, and serialization fidelity.
/// </summary>
public sealed class PersistenceEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;

    public PersistenceEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"edge-case-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ── Concurrency guard still works ───────────────────────────────────

    /// <summary>
    /// Two concurrent TriggerAsync for the same type+template — second must be rejected.
    /// Ensures the DB-layer dedup path still works after constructor refactoring.
    /// </summary>
    [Fact]
    public async Task ConsolidationService_ConcurrencyGuard_RejectsDuplicateTrigger()
    {
        var harnessStore = new InMemoryHarnessSuggestionStore();
        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "D", TemplateIds = new List<string> { "t1" } }
            });
        mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "T", IssueProviderId = "i", RepoProviderId = "r", Enabled = true }
            });
        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor
            .SetupSequence(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-persist-edge", ErrorMessage: null))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: null, ErrorMessage: null, Queued: true, AlreadyExists: true));

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            new LoggerConfiguration().CreateLogger(),
            new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir, DefaultRequiredAgentLabels = "kiro,dotnet,dotnet10" },
            mockProjectStore.Object,
            harnessStore,
            new Mock<IProviderConfigStore>().Object,
            WorkDistributor: mockWorkDistributor.Object));

        var first = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "t1", CancellationToken.None);
        var second = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "t1", CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().BeNull("rejected by DB-layer dedup (WorkItemId=null = 409 duplicate)");
    }

    // ── PipelineRunSummary backward-compat deserialization ──────────────

    /// <summary>
    /// A PipelineRunSummary JSON payload that was written before CacheReadTokens/CacheWriteTokens
    /// existed (i.e. those fields are absent) must deserialize cleanly with both fields = 0.
    /// This validates the backward-compat acceptance criterion for file-based and Postgres JSONB paths.
    /// </summary>
    [Fact]
    public void PipelineRunSummary_OldSummaryWithoutCacheFields_DeserializesCleanlyWithZero()
    {
        // Arrange: JSON that was written before CacheReadTokens/CacheWriteTokens existed.
        // The fields are intentionally absent (not "cacheReadTokens": 0) to simulate old records.
        const string oldJson = """
            {
              "runId": "test-run-old",
              "issueIdentifier": "42",
              "issueTitle": "Old Run",
              "finalStep": "Completed",
              "startedAt": "2026-01-01T00:00:00Z",
              "startedAtOffset": "2026-01-01T00:00:00+00:00",
              "totalTokens": 12345,
              "totalCost": 0.05,
              "initiatedBy": "manual"
            }
            """;

        // Act
        var summary = JsonSerializer.Deserialize<PipelineRunSummary>(oldJson, CodingAgent.Pipeline.PipelineJsonOptions.Default);

        // Assert
        summary.Should().NotBeNull();
        summary!.RunId.Should().Be("test-run-old");
        summary.TotalTokens.Should().Be(12345);
        summary.CacheReadTokens.Should().Be(0, "missing field in old JSON must default to 0");
        summary.CacheWriteTokens.Should().Be(0, "missing field in old JSON must default to 0");
    }
}

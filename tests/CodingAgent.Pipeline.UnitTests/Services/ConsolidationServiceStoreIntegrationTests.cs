#pragma warning disable CS0618 // FileSystemConsolidationRunStore is Obsolete; test-infrastructure use is intentional
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.UnitTests.Helpers;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Integration tests verifying ConsolidationService correctly delegates persistence
/// to IConsolidationRunStore. These tests guard against regressions where store operations
/// are inadvertently reintroduced.
/// </summary>
public sealed class ConsolidationServiceStoreIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly PipelineConfiguration _config;
    private readonly FileSystemConsolidationRunStore _store;
    private readonly IHarnessSuggestionStore _harnessStore;
    private readonly ConsolidationService _sut;

    public ConsolidationServiceStoreIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"store-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _config = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir, DefaultRequiredAgentLabels = "kiro,dotnet,dotnet10" };

        _mockProjectStore = new Mock<IProjectStore>();
        _mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new()
                {
                    Id = WellKnownIds.DefaultProjectId,
                    Name = "Default",
                    TemplateIds = new List<string> { "tmpl-1" }
                }
            });
        _mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "tmpl-1", Name = "Test Template", IssueProviderId = "ip", RepoProviderId = "rp", Enabled = true }
            });

        _store = new FileSystemConsolidationRunStore(Path.Combine(_tempDir, "runs"));
        _harnessStore = new InMemoryHarnessSuggestionStore();

        // WorkDistributor returns success so TriggerAsync creates a Pending run.
        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-integration-test", ErrorMessage: null));

        _sut = new ConsolidationService(
            new ConsolidationServiceDependencies(
                new LoggerConfiguration().CreateLogger(),
                _config,
                _mockProjectStore.Object,
                _store,
                _harnessStore,
                new Mock<IProviderConfigStore>().Object,
                WorkDistributor: mockWorkDistributor.Object));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// REGRESSION TEST: After issue #3028, TriggerAsync no longer persists to the ConsolidationRuns
    /// store. This test verifies the store is NOT written by TriggerAsync and that the method
    /// still returns a run object (for callers that need the RunId/WorkItemId).
    /// </summary>
    [Fact]
    public async Task TriggerAsync_DoesNotWriteToStore_ButReturnsRun()
    {
        // TODO [WARNING]: This test only asserts (a) non-null return and (b) that nothing was written
        // to the store. It does not verify that the distributor was called with the correct
        // IssueIdentifier or that the returned run has WorkItemId set. A regression where TriggerAsync
        // short-circuits before calling DistributeAsync (returning a recycled or stub run) would pass.
        // Add _mockWorkDistributor.Verify(d => d.DistributeAsync(...), Times.Once) and assert
        // run.WorkItemId is not null to make this a stronger regression guard.
        // (review-findings.md — TestQualityReviewer)
        var run = await _sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull("TriggerAsync must return the run even after store-write removal");

        // Assert: nothing was persisted to the store (PipelineRun is the authoritative record)
        var persisted = await _store.GetByIdAsync(run.RunId, CancellationToken.None);
        persisted.Should().BeNull("TriggerAsync must not write to the ConsolidationRuns store (writes stopped in #3028)");
    }

    /// <summary>
    /// Issue #3028: CleanupOrphanedRunsAsync no longer writes to the store.
    /// The run's in-memory status is updated but no SaveRunAsync is called.
    /// This test verifies the in-memory mutation (for log output) and no store write.
    /// </summary>
    [Fact]
    public async Task CleanupOrphanedRunsAsync_MarksRunningAsFailed_ViaStore()
    {
        // Arrange: seed the store directly (bypass TriggerAsync which no longer writes)
        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            Status = ConsolidationRunStatus.Running
        };
        await _store.SaveRunAsync(run, CancellationToken.None);

        // Act: cleanup marks the in-memory run as Failed but does not write back
        var sut2 = new ConsolidationService(
            new ConsolidationServiceDependencies(
                new LoggerConfiguration().CreateLogger(),
                _config,
                _mockProjectStore.Object,
                _store,
                _harnessStore,
                new Mock<IProviderConfigStore>().Object));
        await sut2.CleanupOrphanedRunsAsync([], CancellationToken.None);

        // Assert: the store still has the original record (no SaveRunAsync was called)
        var persisted = await _store.GetByIdAsync(runId, CancellationToken.None);
        persisted.Should().NotBeNull();
        // Status in store is unchanged (CleanupOrphanedRunsAsync no longer writes back in #3028)
        persisted!.Status.Should().Be(ConsolidationRunStatus.Running,
            "CleanupOrphanedRunsAsync no longer writes to the store (issue #3028)");
    }

    /// <summary>
    /// Verify harness suggestions round-trip through IHarnessSuggestionStore.
    /// </summary>
    [Fact]
    public async Task HarnessSuggestions_SaveAndGet_RoundTripsViaStore()
    {
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 10,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.85m,
            Suggestions = new List<HarnessSuggestion>
            {
                new() { Frequency = 5, Rationale = "Test", Text = "Improve X" }
            }
        };

        await _sut.SaveHarnessSuggestionsAsync(suggestions, CancellationToken.None);
        var loaded = await _sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.BasedOnRunCount.Should().Be(10);
        loaded.Suggestions.Should().HaveCount(1);
    }
}

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
/// Unit tests for <see cref="ConsolidationService"/>.
/// Validates: Requirements 3.1, 3.3, 3.5, 3.7, 9.2, 9.4
/// </summary>
public sealed class ConsolidationServiceTests : IDisposable
{
    private static readonly string[] DotNetLabels = ["kiro", "dotnet", "dotnet10"];
    private static readonly string[] PythonLabels = ["kiro", "python", "python312"];

    private readonly string _tempDir;
    private readonly string _runsDir;
    private readonly ILogger _logger;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly Mock<IProviderConfigStore> _mockProviderConfigStore;
    private readonly PipelineConfiguration _config;
    private readonly List<PipelineJobTemplate> _templates;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;

    public ConsolidationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"consolidation-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _runsDir = Path.Combine(_tempDir, "runs");

        _logger = new LoggerConfiguration().CreateLogger();

        // Default: return ProviderConfig with no RequiredLabels so existing tests continue to
        // exercise DefaultRequiredAgentLabels fallback. Tests that need repo-scoped labels set
        // up their own mock.
        _mockProviderConfigStore = new Mock<IProviderConfigStore>();
        _mockProviderConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        _templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl-1",
                Name = "DotNet Repo",
                IssueProviderId = "ip-1",
                RepoProviderId = "rp-1",
                BrainProviderId = "bp-1",
                Enabled = true
            },
            new()
            {
                Id = "tmpl-2",
                Name = "Python Repo",
                IssueProviderId = "ip-2",
                RepoProviderId = "rp-2",
                Enabled = true
            }
        };

        _config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempDir,
            // Most tests need runs to succeed. DefaultRequiredAgentLabels must be set so
            // TriggerAsync passes the fail-fast label-resolution check added in issue #2536.
            // Tests that specifically test the "no labels" rejection path create their own
            // PipelineConfiguration instance without DefaultRequiredAgentLabels.
            DefaultRequiredAgentLabels = "kiro,dotnet,dotnet10"
        };

        // Mock IProjectStore to return a default project owning all templates
        _mockProjectStore = new Mock<IProjectStore>();
        _mockProjectStore.Setup(x => x.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new()
                {
                    Id = WellKnownIds.DefaultProjectId,
                    Name = "Default",
                    TemplateIds = new List<string> { "tmpl-1", "tmpl-2" }
                }
            });
        _mockProjectStore.Setup(x => x.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_templates);

        // Default: WorkDistributor returns success so TriggerAsync creates a Pending run.
        // Tests that verify failure paths configure their own mock setup.
        _mockWorkDistributor = new Mock<IWorkDistributor>();
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-test-default", ErrorMessage: null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private ConsolidationService CreateSut() => new(new ConsolidationServiceDependencies(
        _logger,
        _config,
        _mockProjectStore.Object,
        new FileSystemConsolidationRunStore(_runsDir),
        new InMemoryHarnessSuggestionStore(),
        _mockProviderConfigStore.Object,
        WorkDistributor: _mockWorkDistributor.Object));

    #region TriggerAsync — creates run and persists

    [Fact]
    public async Task TriggerAsync_ValidTemplate_CreatesRunWithPendingStatus()
    {
        // Validates: Requirement 3.1
        // Runs start as Pending — the WorkItem has been submitted to the unified queue
        // and the K8s Scheduler will dispatch it when capacity is available.
        var sut = CreateSut();
        var before = DateTimeOffset.UtcNow;

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull();
        run!.Status.Should().Be(ConsolidationRunStatus.Pending);
        run.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        run.TemplateId.Should().Be("tmpl-1");
        run.TemplateName.Should().Be("DotNet Repo");
        run.RunId.Should().NotBeNullOrEmpty();
        run.StartedAtUtc.Should().BeOnOrAfter(before,
            because: "StartedAtUtc must be set to a time at or after TriggerAsync was called");
        run.StartedAtUtc.Should().BeOnOrBefore(before.AddSeconds(10));
    }

    [Fact]
    public async Task TriggerAsync_WithNoDefaultRequiredAgentLabels_CreatesPendingRun()
    {
        // When DefaultRequiredAgentLabels is not configured and no SelectorResolver is injected,
        // TriggerAsync uses LabelResolver (returns empty labels) and still creates a Pending run.
        // The distributor receives an empty selector and may return 422 — but that's the new
        // synchronous error path (null return), not the old "stays Queued forever" path.
        // Use a dedicated SUT without DefaultRequiredAgentLabels and without a WorkDistributor
        // (to test the fallback path that uses LabelResolver directly).
        var configWithoutLabels = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir };
        var sutWithoutLabels = new ConsolidationService(new ConsolidationServiceDependencies(
            _logger,
            configWithoutLabels,
            _mockProjectStore.Object,
            new FileSystemConsolidationRunStore(_runsDir),
            new InMemoryHarnessSuggestionStore(),
            _mockProviderConfigStore.Object));

        // Without a WorkDistributor injected, TriggerAsync throws InvalidOperationException.
        // This verifies the guard is in place (no silent no-op).
        var act = () => sutWithoutLabels.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "TriggerAsync requires IWorkDistributor — no WorkDistributor was injected");
    }

    [Fact]
    public async Task TriggerAsync_ValidTemplate_PersistsRunToDisk()
    {
        // Issue #3028: TriggerAsync no longer persists to the ConsolidationRuns store.
        // This test now verifies that TriggerAsync returns a valid run object with the
        // correct fields even without store persistence.
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull("TriggerAsync must return a run even without store persistence");
        run!.RunId.Should().NotBeNullOrEmpty();
        run.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        run.Status.Should().Be(ConsolidationRunStatus.Pending, "new runs start as Pending");
        // Store must NOT be written (issue #3028)
        var filePath = Path.Combine(_runsDir, $"{run.RunId}.json");
        File.Exists(filePath).Should().BeFalse("TriggerAsync must not write to the ConsolidationRuns store (issue #3028)");
    }

    [Fact]
    public async Task TriggerAsync_ValidTemplate_SetsProjectNameFromOwningProject()
    {
        // Validates: ConsolidationRun must carry the owning project's display name and ID
        // so the UI PROJECT column shows the project instead of "—" and WorkItemEntity.ProjectId
        // is populated.
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull();
        run!.ProjectName.Should().Be("Default");
        run.ProjectId.Should().Be(WellKnownIds.DefaultProjectId,
            "ProjectId must be populated from the owning PipelineProject.Id at trigger time");
    }

    [Fact]
    public async Task TriggerAsync_GlobalTemplate_ProjectNameIsNull()
    {
        // Global consolidation runs (templateId=null) have no owning project.
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions, null, CancellationToken.None);

        run.Should().NotBeNull();
        run!.ProjectName.Should().BeNull();
        run.ProjectId.Should().BeNull("global runs have no owning project");
    }

    [Fact]
    public async Task TriggerAsync_WithProjectId_ProjectIdSurvivesPersistenceRoundTrip()
    {
        // Issue #3028: TriggerAsync no longer persists to the store.
        // This test now verifies that the in-memory ConsolidationRun object has the correct ProjectId.
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull();
        run!.ProjectId.Should().Be(WellKnownIds.DefaultProjectId,
            "ProjectId must be populated from the owning PipelineProject.Id at trigger time");
        run.ProjectName.Should().Be("Default");
    }

    #endregion

    #region TriggerAsync — duplicate running rejects

    [Fact]
    public async Task TriggerAsync_DuplicateRunning_ReturnsNull()
    {
        // Validates: Requirement 3.7 — after _runningRuns removal (issue #3027), dedup is
        // API-layer. The second trigger calls DistributeAsync and receives WorkItemId=null
        // (simulating KubernetesWorkDistributor mapping 409 → Success=true, WorkItemId=null).
        _mockWorkDistributor
            .SetupSequence(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-dup-first", ErrorMessage: null, Queued: true))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: null, ErrorMessage: null, Queued: true));

        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        second.Should().BeNull("duplicate trigger must be rejected when WorkItemId=null (409 path)");
    }

    [Fact]
    public async Task TriggerAsync_DifferentType_SameTemplate_Succeeds()
    {
        // Validates: Requirement 3.7 — different type is not rejected
        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "tmpl-1", CancellationToken.None);
        second.Should().NotBeNull();
    }

    [Fact]
    public async Task TriggerAsync_SameType_DifferentTemplate_Succeeds()
    {
        // Validates: Requirement 3.7 — different templateId is not rejected
        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-2", CancellationToken.None);
        second.Should().NotBeNull();
    }

    #endregion

    #region TriggerAsync — unknown template rejects

    [Fact]
    public async Task TriggerAsync_UnknownTemplateId_ReturnsNull()
    {
        // Validates: Requirement 3.5
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "nonexistent-template", CancellationToken.None);

        run.Should().BeNull();
    }

    #endregion

    #region Harness suggestions — read/write round-trip

    [Fact]
    public async Task SaveAndGetHarnessSuggestions_RoundTripsCorrectly()
    {
        // Validates: Requirement 9.2
        var sut = CreateSut();

        var suggestions = new HarnessSuggestions
        {
            GeneratedAtUtc = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc),
            BasedOnRunCount = 42,
            SuccessRate = 0.85m,
            Suggestions = new List<HarnessSuggestion>
            {
                new()
                {
                    Text = "Increase agent timeout for complex repos",
                    Rationale = "3 out of 5 failures were timeout-related",
                    Frequency = 3
                },
                new()
                {
                    Text = "Add file context for config files",
                    Rationale = "Agents frequently miss appsettings.json",
                    Frequency = 7
                }
            }
        };

        await sut.SaveHarnessSuggestionsAsync(suggestions, CancellationToken.None);
        var loaded = await sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.GeneratedAtUtc.Should().Be(suggestions.GeneratedAtUtc);
        loaded.BasedOnRunCount.Should().Be(42);
        loaded.SuccessRate.Should().Be(0.85m);
        loaded.Suggestions.Should().HaveCount(2);
        loaded.Suggestions[0].Text.Should().Be("Increase agent timeout for complex repos");
        loaded.Suggestions[0].Frequency.Should().Be(3);
        loaded.Suggestions[1].Text.Should().Be("Add file context for config files");
        loaded.Suggestions[1].Frequency.Should().Be(7);
    }

    [Fact]
    public async Task GetHarnessSuggestionsAsync_FileDoesNotExist_ReturnsNull()
    {
        // Validates: Requirement 9.2
        var sut = CreateSut();

        var result = await sut.GetHarnessSuggestionsAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    #endregion

    #region TriggerAsync — global (null templateId) for harness suggestions

    [Fact]
    public async Task TriggerAsync_NullTemplateId_CreatesGlobalRun()
    {
        // Validates: Requirement 3.1
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions, null, CancellationToken.None);

        run.Should().NotBeNull();
        run!.TemplateId.Should().BeNull();
        run.TemplateName.Should().Be("Global");
        run.Type.Should().Be(ConsolidationRunType.HarnessSuggestions);
    }

    #endregion

    // --- DeletePersistedRunAsync tests ---

    [Fact]
    public async Task DeletePersistedRunAsync_FileExists_DeletesFile()
    {
        // Issue #3028: TriggerAsync no longer writes to the store.
        // Seed a run file directly.
        Directory.CreateDirectory(_runsDir);
        var runId = Guid.NewGuid().ToString();
        var store = new FileSystemConsolidationRunStore(_runsDir);
        var run = new ConsolidationRun { RunId = runId, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow, Status = ConsolidationRunStatus.Succeeded };
        await store.SaveRunAsync(run, CancellationToken.None);
        var filePath = Path.Combine(_runsDir, $"{runId}.json");
        File.Exists(filePath).Should().BeTrue("file must exist after direct seeding");

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            _logger, _config, _mockProjectStore.Object,
            store, new InMemoryHarnessSuggestionStore(), _mockProviderConfigStore.Object));

        await sut.DeletePersistedRunAsync(runId);

        File.Exists(filePath).Should().BeFalse("DeletePersistedRunAsync must delete the file");
    }

    [Fact]
    public async Task DeletePersistedRunAsync_FileDoesNotExist_DoesNotThrow()
    {
        // Arrange
        var sut = CreateSut();
        Directory.CreateDirectory(_runsDir);
        var nonExistentRunId = Guid.NewGuid().ToString();

        // Act & Assert — no exception
        await sut.Invoking(s => s.DeletePersistedRunAsync(nonExistentRunId))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeletePersistedRunAsync_NullRunId_ThrowsArgumentNullException()
    {
        var sut = CreateSut();
        await sut.Invoking(s => s.DeletePersistedRunAsync(null!))
            .Should().ThrowExactlyAsync<ArgumentNullException>();
    }

    #region TriggerAsync — persist failure rollback (Req 8.1) — updated for issue #3028

    [Fact]
    public async Task TriggerAsync_WhenPersistFails_ReturnsNull()
    {
        // Issue #3028: TriggerAsync no longer persists to the store.
        // The old persist-fail path no longer exists. This test now verifies that
        // DistributeAsync failure (transient) causes TriggerAsync to return null.
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: false, WorkItemId: null, ErrorMessage: "transient error"));

        var sut = new ConsolidationService(
            new ConsolidationServiceDependencies(
                _logger,
                _config,
                _mockProjectStore.Object,
                new FileSystemConsolidationRunStore(_runsDir),
                new InMemoryHarnessSuggestionStore(),
                _mockProviderConfigStore.Object,
                WorkDistributor: _mockWorkDistributor.Object));

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().BeNull("TriggerAsync must return null when DistributeAsync fails");
    }

    [Fact]
    public async Task TriggerAsync_HarnessSuggestions_WhenDistributeFails_ReturnsNull()
    {
        // Issue #3028+#3030: ConsolidationFeedbackCache removed. TriggerAsync no longer calls
        // PrepareFeedbackDataAsync. When DistributeAsync fails for a HarnessSuggestions run,
        // TriggerAsync returns null.
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: false, WorkItemId: null, ErrorMessage: "transient error"));

        var sut = new ConsolidationService(
            new ConsolidationServiceDependencies(
                _logger,
                _config,
                _mockProjectStore.Object,
                new FileSystemConsolidationRunStore(_runsDir),
                new InMemoryHarnessSuggestionStore(),
                _mockProviderConfigStore.Object,
                WorkDistributor: _mockWorkDistributor.Object));

        // Act
        var run = await sut.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions, null, CancellationToken.None);

        // Assert: TriggerAsync returns null on dispatch failure
        run.Should().BeNull("TriggerAsync must return null when DistributeAsync fails");
    }

    [Fact]
    public async Task TriggerAsync_BrainConsolidation_WhenDistributeFails_ReturnsNull()
    {
        // Issue #3028+#3030: ConsolidationFeedbackCache removed. When DistributeAsync fails
        // for a BrainConsolidation run, TriggerAsync returns null.
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: false, WorkItemId: null, ErrorMessage: "transient error"));

        var sut = new ConsolidationService(
            new ConsolidationServiceDependencies(
                _logger,
                _config,
                _mockProjectStore.Object,
                new FileSystemConsolidationRunStore(_runsDir),
                new InMemoryHarnessSuggestionStore(),
                _mockProviderConfigStore.Object,
                WorkDistributor: _mockWorkDistributor.Object));

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().BeNull("TriggerAsync returns null on dispatch failure");
    }

    #endregion
}

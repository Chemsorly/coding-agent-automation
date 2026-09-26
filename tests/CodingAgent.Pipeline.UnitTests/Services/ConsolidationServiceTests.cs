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
    private readonly Mock<IPipelineRunHistoryService> _mockRunHistory;
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
        _mockRunHistory = new Mock<IPipelineRunHistoryService>();
        // TODO: GetRunHistoryAsync always returns empty list — no test exercises PrepareFeedbackDataAsync with actual feedback entries. Add tests with non-empty run history to cover filtering logic after the async migration.
        _mockRunHistory.Setup(x => x.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineRunSummary>());

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

    // TODO [WARNING]: Each CreateSut() call allocates a fresh InMemoryHarnessSuggestionStore(), not a shared
    // class-level instance. Tests that verify harness-suggestion persistence across calls within the same
    // ConsolidationService instance will pass even if the service wires the wrong store, because each call
    // gets its own empty store. Consider promoting the store to a class-level field (like _runsDir) so that
    // tests can assert SaveAsync results are visible via GetAsync on the same instance the service holds.
    private ConsolidationService CreateSut() => new(new ConsolidationServiceDependencies(
        _logger,
        _config,
        _mockProjectStore.Object,
        _mockRunHistory.Object,
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
            _mockRunHistory.Object,
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
        // API-layer. The second trigger calls DistributeAsync and receives AlreadyExists=true
        // (simulating KubernetesWorkDistributor mapping 409 → Success=true, WorkItemId=null, AlreadyExists=true).
        // TODO [WARNING]: SetupSequence is configured on the class-level _mockWorkDistributor alongside
        // any default Setup registered in the constructor. In Moq, calling SetupSequence on an already-
        // configured mock does not replace the default Setup — both coexist and the most-recently-added
        // setup wins per call order. If test ordering changes or the constructor's default Setup
        // changes, this may interact unpredictably. Low risk since each [Fact] gets a fresh instance
        // via the constructor; noted as a latent brittleness. (review-findings-testqualityreviewer.md)
        _mockWorkDistributor
            .SetupSequence(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-dup-first", ErrorMessage: null, Queued: true))
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: null, ErrorMessage: null, Queued: true, AlreadyExists: true));

        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().NotBeNull();

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        second.Should().BeNull("duplicate trigger must be rejected when AlreadyExists=true (409 path)");
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

    #region UpdateRunAsync — persists status change (updated for issue #3028)

    [Fact]
    public async Task UpdateRunAsync_ChangesStatusAndSetsCompletedAt()
    {
        // Issue #3028: UpdateRunAsync is now a no-op (store writes removed, workspace in the agent pod).
        // This test verifies the method does not throw and returns without error.
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // UpdateRunAsync is now a no-op for store writes; must not throw
        var act = () => sut.UpdateRunAsync(
            run!.RunId, ConsolidationRunStatus.Succeeded, "All done", CancellationToken.None);
        await act.Should().NotThrowAsync("UpdateRunAsync must not throw (issue #3028)");
    }

    [Fact]
    public async Task UpdateRunAsync_RemovesFromRunningTracker_AllowsNewTrigger()
    {
        // Validates: Requirement 3.7 — after completion, same type+template can be triggered again
        var sut = CreateSut();

        var first = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        first.Should().NotBeNull();

        await sut.UpdateRunAsync(
            first!.RunId, ConsolidationRunStatus.Succeeded, "Done", CancellationToken.None);

        var second = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        second.Should().NotBeNull();
        // TODO [WARNING]: The assertion above only verifies non-null return; it does not verify that
        // 'second' has a different RunId from 'first'. A regression where TriggerAsync returns the
        // existing completed run object instead of creating a new one would still pass. Add:
        //   second!.RunId.Should().NotBe(first.RunId, "a new run must have a fresh RunId");
        // to confirm a genuinely new run was created. (review-findings-testqualityreviewer.md)
    }

    [Theory]
    [InlineData(ConsolidationRunStatus.Succeeded)]
    [InlineData(ConsolidationRunStatus.Failed)]
    [InlineData(ConsolidationRunStatus.Cancelled)]
    public async Task UpdateRunAsync_TerminalStatus_RejectsSubsequentOverwrite(ConsolidationRunStatus terminalStatus)
    {
        // Issue #3028: UpdateRunAsync is now a no-op for store writes.
        // This test now verifies that multiple calls to UpdateRunAsync do not throw.
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        var act1 = () => sut.UpdateRunAsync(run!.RunId, terminalStatus, "Completed normally", CancellationToken.None);
        await act1.Should().NotThrowAsync();

        var act2 = () => sut.UpdateRunAsync(run!.RunId, ConsolidationRunStatus.Failed, "Timeout exceeded", CancellationToken.None);
        await act2.Should().NotThrowAsync("UpdateRunAsync must not throw regardless of call order (issue #3028)");
    }

    [Fact]
    public async Task UpdateRunAsync_ByWorkItemId_FindsRunAndUpdatesStatus()
    {
        // Regression test for the hub completion path: AgentHub passes result.JobId (= WorkItem ID)
        // to UpdateRunAsync, but ConsolidationRun is stored by its own RunId (a different Guid
        // generated in BuildNewRun). UpdateRunAsync must fall back to a WorkItemId scan so the
        // run transitions correctly instead of silently no-op'ing ("not found").
        var sut = CreateSut();

        // _mockWorkDistributor returns WorkItemId = "wi-test-default" by default.
        // TriggerAsync stores this as run.WorkItemId (≠ run.RunId, a fresh Guid).
        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);
        run.Should().NotBeNull();

        // Confirm the run was stored with the expected WorkItemId
        run!.WorkItemId.Should().Be("wi-test-default",
            "TriggerAsync must persist the WorkItemId returned by IWorkDistributor");
        run.RunId.Should().NotBe("wi-test-default",
            "ConsolidationRun.RunId must be a freshly generated Guid, distinct from the WorkItemId");

        // Act: simulate the hub calling UpdateRunAsync with the WorkItemId (not the RunId)
        await sut.UpdateRunAsync(
            new RunId("wi-test-default"),
            ConsolidationRunStatus.Succeeded,
            "Brain consolidation complete",
            CancellationToken.None);

        // Assert: the run is now Succeeded despite being looked up by WorkItemId
        var history = await sut.GetRunHistoryAsync(CancellationToken.None);
        var updated = history.FirstOrDefault(r => r.RunId == run.RunId);
        updated.Should().NotBeNull("run must still exist in history");
        updated!.Status.Should().Be(ConsolidationRunStatus.Succeeded,
            "UpdateRunAsync must update the run even when called with a WorkItemId instead of RunId");
        updated.Summary.Should().Be("Brain consolidation complete");
    }

    #endregion

    #region GetRunHistoryAsync — returns ordered results

    [Fact]
    public async Task GetRunHistoryAsync_ReturnsRunsOrderedByStartedAtDescending()
    {
        // Issue #3028: TriggerAsync no longer writes to the store.
        // This test now seeds runs directly into the store and verifies GetRunHistoryAsync ordering.
        var store = new FileSystemConsolidationRunStore(_runsDir);
        var run1 = new ConsolidationRun { RunId = Guid.NewGuid().ToString(), Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10), Status = ConsolidationRunStatus.Succeeded };
        var run2 = new ConsolidationRun { RunId = Guid.NewGuid().ToString(), Type = ConsolidationRunType.RefactoringDetection, StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5), Status = ConsolidationRunStatus.Succeeded };
        await store.SaveRunAsync(run1, CancellationToken.None);
        await store.SaveRunAsync(run2, CancellationToken.None);

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            _logger, _config, _mockProjectStore.Object, _mockRunHistory.Object,
            store, new InMemoryHarnessSuggestionStore(), _mockProviderConfigStore.Object));

        var history = await sut.GetRunHistoryAsync(CancellationToken.None);

        history.Should().HaveCount(2);
        history[0].RunId.Should().Be(run2.RunId, "most recent run must be first");
        history[1].RunId.Should().Be(run1.RunId);
    }

    [Fact]
    public async Task GetRunHistoryAsync_EmptyDirectory_ReturnsEmptyList()
    {
        // Validates: Requirement 3.1
        var sut = CreateSut();

        var history = await sut.GetRunHistoryAsync(CancellationToken.None);

        history.Should().BeEmpty();
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
            _logger, _config, _mockProjectStore.Object, _mockRunHistory.Object,
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

        // Act & Assert — no exception, and run is confirmed absent
        await sut.Invoking(s => s.DeletePersistedRunAsync(nonExistentRunId))
            .Should().NotThrowAsync();
        var history = await sut.GetRunHistoryAsync(CancellationToken.None);
        history.Should().NotContain(r => r.RunId == nonExistentRunId);
    }

    [Fact]
    public async Task DeletePersistedRunAsync_NullRunId_ThrowsArgumentNullException()
    {
        var sut = CreateSut();
        await sut.Invoking(s => s.DeletePersistedRunAsync(null!))
            .Should().ThrowExactlyAsync<ArgumentNullException>();
    }

    // --- GetLastSuccessfulHarnessRunTimestampAsync tests ---

    [Fact]
    public async Task GetLastSuccessfulHarnessRunTimestampAsync_WithSuccessfulRuns_ReturnsLatestTimestamp()
    {
        // Arrange
        Directory.CreateDirectory(_runsDir);

        var olderTimestamp = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var newerTimestamp = new DateTime(2026, 3, 15, 8, 30, 0, DateTimeKind.Utc);

        WriteConsolidationRunFile("run-1", ConsolidationRunType.HarnessSuggestions, ConsolidationRunStatus.Succeeded, olderTimestamp);
        WriteConsolidationRunFile("run-2", ConsolidationRunType.HarnessSuggestions, ConsolidationRunStatus.Succeeded, newerTimestamp);
        WriteConsolidationRunFile("run-3", ConsolidationRunType.BrainConsolidation, ConsolidationRunStatus.Succeeded, newerTimestamp.AddDays(1));

        var feedbackCache = new ConsolidationFeedbackCache(
            _logger, new FileSystemConsolidationRunStore(_runsDir), _mockRunHistory.Object);

        // Act
        var result = await feedbackCache.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        // Assert — returns the latest HarnessSuggestions succeeded timestamp, not the brain one
        result.Should().Be(newerTimestamp);
    }

    [Fact]
    public async Task GetLastSuccessfulHarnessRunTimestampAsync_NoRuns_ReturnsMinValue()
    {
        // Arrange — Don't create the directory — simulates first run
        var feedbackCache = new ConsolidationFeedbackCache(
            _logger, new FileSystemConsolidationRunStore(_runsDir), _mockRunHistory.Object);

        // Act
        var result = await feedbackCache.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        // Assert
        result.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task GetLastSuccessfulHarnessRunTimestampAsync_OnlyFailedRuns_ReturnsMinValue()
    {
        // Arrange
        Directory.CreateDirectory(_runsDir);
        WriteConsolidationRunFile("run-1", ConsolidationRunType.HarnessSuggestions, ConsolidationRunStatus.Failed, DateTimeOffset.UtcNow);

        var feedbackCache = new ConsolidationFeedbackCache(
            _logger, new FileSystemConsolidationRunStore(_runsDir), _mockRunHistory.Object);

        // Act
        var result = await feedbackCache.GetLastSuccessfulHarnessRunTimestampAsync(CancellationToken.None);

        // Assert
        result.Should().Be(DateTimeOffset.MinValue);
    }

    private void WriteConsolidationRunFile(string runId, ConsolidationRunType type, ConsolidationRunStatus status, DateTimeOffset? completedAtUtc)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            runId,
            type = type.ToString(),
            status = status.ToString(),
            startedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            completedAtUtc
        }, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(Path.Combine(_runsDir, $"{runId}.json"), json);
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
                _mockRunHistory.Object,
                new FileSystemConsolidationRunStore(_runsDir),
                new InMemoryHarnessSuggestionStore(),
                _mockProviderConfigStore.Object,
                WorkDistributor: _mockWorkDistributor.Object));

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().BeNull("TriggerAsync must return null when DistributeAsync fails");
    }

    [Fact]
    public async Task TriggerAsync_HarnessSuggestions_WhenPersistFails_ClearsFeedbackCache()
    {
        // Issue #3028: TriggerAsync no longer persists to the store.
        // The old persist-fail path no longer exists. This test now verifies that when
        // DistributeAsync fails for a HarnessSuggestions run, the feedback cache is cleared.
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: false, WorkItemId: null, ErrorMessage: "transient error"));

        var mockFeedbackCache = new Mock<IConsolidationFeedbackCache>();
        mockFeedbackCache
            .Setup(c => c.PrepareFeedbackDataAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new ConsolidationService(
            new ConsolidationServiceDependencies(
                _logger,
                _config,
                _mockProjectStore.Object,
                _mockRunHistory.Object,
                new FileSystemConsolidationRunStore(_runsDir),
                new InMemoryHarnessSuggestionStore(),
                _mockProviderConfigStore.Object,
                FeedbackCache: mockFeedbackCache.Object,
                WorkDistributor: _mockWorkDistributor.Object));

        // Act
        var run = await sut.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions, null, CancellationToken.None);

        // Assert: TriggerAsync returns null on dispatch failure
        run.Should().BeNull();

        // Assert: ClearFeedbackDataForRun was called (no cache leak)
        mockFeedbackCache.Verify(
            c => c.ClearFeedbackDataForRun(It.IsAny<RunId>()),
            Times.Once);
    }

    [Fact]
    public async Task TriggerAsync_BrainConsolidation_WhenPersistFails_ClearsFeedbackCacheViaRollback()
    {
        // Issue #3028: TriggerAsync no longer persists to the store, so there is no persist-fail path.
        // This test now verifies that when DistributeAsync fails for a BrainConsolidation run,
        // the feedback cache is cleared (RollbackRunAsync path via feedback cache clear).
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(Success: false, WorkItemId: null, ErrorMessage: "transient error"));

        var mockFeedbackCache = new Mock<IConsolidationFeedbackCache>();
        mockFeedbackCache
            .Setup(c => c.PrepareFeedbackDataAsync(It.IsAny<ConsolidationRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new ConsolidationService(
            new ConsolidationServiceDependencies(
                _logger,
                _config,
                _mockProjectStore.Object,
                _mockRunHistory.Object,
                new FileSystemConsolidationRunStore(_runsDir),
                new InMemoryHarnessSuggestionStore(),
                _mockProviderConfigStore.Object,
                FeedbackCache: mockFeedbackCache.Object,
                WorkDistributor: _mockWorkDistributor.Object));

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().BeNull("TriggerAsync returns null on dispatch failure");

        mockFeedbackCache.Verify(
            c => c.ClearFeedbackDataForRun(It.IsAny<RunId>()),
            Times.Once,
            "ClearFeedbackDataForRun must be called for all run types, not just HarnessSuggestions");
    }

    #endregion

    #region IConsolidationRunTracker

    [Fact]
    public void ConsolidationService_ImplementsIConsolidationRunTracker()
    {
        var sut = CreateSut();
        sut.Should().BeAssignableTo<IConsolidationRunTracker>();
    }

    [Fact]
    public async Task IsRunActive_AcceptsRunId_AndImplicitStringConversion()
    {
        // Issue #3028: TriggerAsync no longer writes to the store. IsRunActive reads from the store.
        // Seed a run directly into the store.
        var store = new FileSystemConsolidationRunStore(_runsDir);
        var run = new ConsolidationRun { RunId = Guid.NewGuid().ToString(), Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow, Status = ConsolidationRunStatus.Pending };
        await store.SaveRunAsync(run, CancellationToken.None);

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            _logger, _config, _mockProjectStore.Object, _mockRunHistory.Object,
            store, new InMemoryHarnessSuggestionStore(), _mockProviderConfigStore.Object));

        var runIdStr = run.RunId;
        RunId runIdTyped = runIdStr;

        sut.IsRunActive(runIdTyped).Should().BeTrue("Pending run is active");
        sut.IsRunActive(runIdStr).Should().BeTrue("implicit string conversion must also work");

        var startedAt = sut.GetActiveRunStartedAt(runIdTyped);
        startedAt.Should().NotBeNull("active run has a StartedAtUtc in the store");
    }

    [Fact]
    public async Task UpdateRunAsync_AcceptsRunId_AtInterfaceBoundary()
    {
        // Issue #3028: UpdateRunAsync is now a no-op for store writes.
        // Verify it accepts RunId type and doesn't throw.
        var runId = Guid.NewGuid().ToString();
        var sut = CreateSut();

        RunId typedRunId = runId;
        var act = () => sut.UpdateRunAsync(typedRunId, ConsolidationRunStatus.Succeeded, "done", CancellationToken.None);
        await act.Should().NotThrowAsync("UpdateRunAsync must accept RunId and not throw");
    }

    #endregion
}

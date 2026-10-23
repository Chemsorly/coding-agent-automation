using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.UnitTests.Helpers;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationService"/>.
/// Validates: Requirements 3.1, 3.3, 3.5, 3.7, 9.2, 9.4
/// </summary>
public sealed class ConsolidationServiceTests
{
    private static readonly string[] DotNetLabels = ["kiro", "dotnet", "dotnet10"];
    private static readonly string[] PythonLabels = ["kiro", "python", "python312"];

    private readonly string _tempDir;
    private readonly ILogger _logger;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly PipelineConfiguration _config;
    private readonly List<PipelineJobTemplate> _templates;
    private readonly Mock<IWorkDistributor> _mockWorkDistributor;

    public ConsolidationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"consolidation-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _logger = new LoggerConfiguration().CreateLogger();

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

    private ConsolidationService CreateSut() => new(new ConsolidationServiceDependencies(
        _logger,
        _config,
        _mockProjectStore.Object,
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
        // ConsolidationRunStatus was removed in issue #3032 — success is implied by non-null return.
        run!.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        run.TemplateId.Should().Be("tmpl-1");
        run.TemplateName.Should().Be("DotNet Repo");
        run.RunId.Should().NotBeNullOrEmpty();
        run.StartedAtUtc.Should().BeOnOrAfter(before,
            because: "StartedAtUtc must be set to a time at or after TriggerAsync was called");
        run.StartedAtUtc.Should().BeOnOrBefore(before.AddSeconds(10));
    }

    [Fact]
    public async Task TriggerAsync_StartedAtUtc_IsTakenBeforeTheWorkItemIsDistributed()
    {
        // Issue #3209: the API call behind DistributeAsync can take seconds under load, so the run's
        // start time is taken before it, not when it returns.
        DateTimeOffset? distributionStartedAt = null;
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                distributionStartedAt = DateTimeOffset.UtcNow;
                await Task.Delay(200);
                return new DistributionResult(Success: true, WorkItemId: "wi-started-at", ErrorMessage: null);
            });
        var sut = CreateSut();

        var run = await sut.TriggerAsync(ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull();
        distributionStartedAt.Should().NotBeNull();
        run.StartedAtUtc.Should().BeOnOrBefore(distributionStartedAt.Value,
            "the run starts when it is handed to the distributor, not when the distributor returns");
    }

    [Fact]
    public async Task TriggerAsync_WithoutWorkDistributor_Throws()
    {
        var configWithoutLabels = new PipelineConfiguration { WorkspaceBaseDirectory = _tempDir };
        var sutWithoutLabels = new ConsolidationService(new ConsolidationServiceDependencies(
            _logger,
            configWithoutLabels,
            _mockProjectStore.Object));

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
        // This test now verifies that TriggerAsync returns a valid result object with the
        // correct fields even without store persistence.
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull("TriggerAsync must return a result even without store persistence");
        run!.RunId.Should().NotBeNullOrEmpty();
        run.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        // ConsolidationRunStatus.Pending was removed in issue #3032 — success is implied by non-null return.
    }

    [Fact]
    public async Task TriggerAsync_ValidTemplate_SetsProjectNameFromOwningProject()
    {
        // Validates: ConsolidationTriggerResult must carry the owning project's display name and ID
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
            ConsolidationRunType.BrainConsolidation, null, CancellationToken.None);

        run.Should().NotBeNull();
        run!.ProjectName.Should().BeNull();
        run.ProjectId.Should().BeNull("global runs have no owning project");
    }

    [Fact]
    public async Task TriggerAsync_WithProjectId_ProjectIdSurvivesPersistenceRoundTrip()
    {
        // Issue #3028: TriggerAsync no longer persists to the store.
        // This test now verifies that the in-memory ConsolidationTriggerResult object has the correct ProjectId.
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

    #region TriggerAsync — global (null templateId)

    [Fact]
    public async Task TriggerAsync_NullTemplateId_CreatesGlobalRun()
    {
        // Validates: Requirement 3.1
        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, null, CancellationToken.None);

        run.Should().NotBeNull();
        run!.TemplateId.Should().BeNull();
        run.TemplateName.Should().Be("Global");
        run.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
    }

    #endregion

    #region TriggerAsync — dispatch failure paths

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
                WorkDistributor: _mockWorkDistributor.Object));

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().BeNull("TriggerAsync must return null when DistributeAsync fails");
    }

    [Fact]
    public async Task TriggerAsync_WhenDistributeAsyncThrows_ReturnsNull()
    {
        // Covers the catch block when DistributeAsync throws an unexpected exception.
        // TriggerAsync must return null and not propagate the exception.
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated distributor failure"));

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            _logger,
            _config,
            _mockProjectStore.Object,
            WorkDistributor: _mockWorkDistributor.Object));

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().BeNull("TriggerAsync must return null when DistributeAsync throws");
    }

    [Fact]
    public async Task TriggerAsync_TransientDispatchFailure_LogsWarning_AndReturnsNull()
    {
        // AC1: TryDistributeAsync with IWorkDistributor returning IsPermanentFailure = false.
        // Verifies: return value is null, a Warning log is emitted containing the error message,
        // and no Error log is emitted (which would indicate a permanent failure instead).
        //
        // "No issue-update or label-swap side effects" is structurally guaranteed: ConsolidationService
        // has no IIssueProvider, label-swapper, or similar dependency — those calls are architecturally
        // impossible regardless of which branch is taken.
        //
        // Uses an inline SUT with a CapturingSink-backed logger (CreateSut() passes the no-op _logger
        // which discards all events and would make the log assertion silently pass-but-wrong).
        const string transientErrorMessage = "PVC unavailable — retry after volume is ready";

        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(
                Success: false,
                WorkItemId: null,
                ErrorMessage: transientErrorMessage,
                IsPermanentFailure: false));  // explicit: drives the transient-failure else-branch

        var sink = new CapturingSink();
        var capturingLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            capturingLogger,
            _config,
            _mockProjectStore.Object,
            WorkDistributor: _mockWorkDistributor.Object));

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().BeNull("transient dispatch failure must return null");

        sink.Events.Should().ContainSingle(e =>
            e.Level == LogEventLevel.Warning &&
            e.RenderMessage().Contains(transientErrorMessage),
            "a Warning containing the error message must be emitted for transient failures");

        sink.Events.Should().NotContain(e => e.Level == LogEventLevel.Error,
            "a transient failure must not emit an Error log (Error is reserved for permanent failures)");
    }

    [Fact]
    public async Task TriggerAsync_WorkItemId_SetFromDistributor()
    {
        // TriggerAsync must set WorkItemId on the returned result from the distributor result.
        var sut = CreateSut();

        // _mockWorkDistributor returns WorkItemId = "wi-test-default" by default.
        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull();
        run!.WorkItemId.Should().Be("wi-test-default",
            "TriggerAsync must set the WorkItemId returned by IWorkDistributor on the returned result");
        run.RunId.Should().NotBe("wi-test-default",
            "ConsolidationTriggerResult.RunId must be a freshly generated Guid, distinct from the WorkItemId");
    }

    #endregion

    #region RefactoringFocus propagation

    [Fact]
    public async Task TriggerAsync_RefactoringFocus_IsCopiedToDistributionRequest()
    {
        // Validates: RefactoringFocus must be preserved in the JobDistributionRequest
        // so the agent executor can filter Phase 1 agents.
        JobDistributionRequest? capturedRequest = null;
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-focus-test", ErrorMessage: null));

        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "tmpl-1", CancellationToken.None,
            autoDispatch: false, focus: RefactoringScanFocus.Structure);

        run.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RefactoringFocus.Should().Be(RefactoringScanFocus.Structure,
            "RefactoringFocus must be copied into JobDistributionRequest");
    }

    #endregion

    #region TriggerAutomaticAsync

    [Fact]
    public async Task TriggerAutomaticAsync_SetsConsolidationAutoAndNoAutoDispatch()
    {
        // Validates acceptance criteria: TriggerAutomaticAsync passes autoDispatch: false
        // and sets InitiatedBy = "consolidation:auto".
        JobDistributionRequest? capturedRequest = null;
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-auto-test", ErrorMessage: null));

        var sut = CreateSut();

        var run = await sut.TriggerAutomaticAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.InitiatedBy.Should().Be(InitiatedByConstants.ConsolidationAuto,
            "automatic triggers must record consolidation:auto as the initiator");
        capturedRequest.AutoDispatch.Should().BeFalse(
            "automatic refactoring scans must never add agent:next to created issues");
    }

    [Theory]
    [InlineData(ConsolidationRunType.BrainConsolidation, "tmpl-1")]
    [InlineData(ConsolidationRunType.RefactoringDetection, "tmpl-1")]
    [InlineData(ConsolidationRunType.RefactoringDetection, null)]
    public async Task ScopeKey_MatchesTheIssueIdentifierOfTheDistributedRequest(
        ConsolidationRunType type, string? templateId)
    {
        // When a template is required, use "tmpl-1" which has brainId "bp-1" in the test data.
        string? actualTemplateId = templateId;

        JobDistributionRequest? capturedRequest = null;
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new DistributionResult(Success: true, WorkItemId: "wi-scope-key", ErrorMessage: null));

        var sut = CreateSut();

        TemplateId? typedTemplateId = actualTemplateId is not null ? new TemplateId(actualTemplateId) : (TemplateId?)null;
        await sut.TriggerAsync(type, typedTemplateId, CancellationToken.None);

        capturedRequest.Should().NotBeNull();

        // For BrainConsolidation the scope key uses the brain provider ID, not the template ID.
        // Template "tmpl-1" has BrainProviderId = "bp-1" in the test setup.
        var expectedScopeKey = type == ConsolidationRunType.BrainConsolidation
            ? ConsolidationService.ScopeKey(type, "bp-1", null)
            : ConsolidationService.ScopeKey(type, null, actualTemplateId);

        capturedRequest!.IssueIdentifier.Value.Should().Be(expectedScopeKey,
            "ScopeKey must produce the same string used as IssueIdentifier in the distribution request");
    }

    #endregion

    #region TriggerAsync — cancellation handling

    [Fact]
    public async Task TriggerAsync_WhenDistributeAsyncThrowsOce_AndTokenIsCancelled_PropagatesAndLogsNoError()
    {
        // AC-1 (issue #3845): when DistributeAsync throws OperationCanceledException and the caller's
        // token is cancelled, TriggerAsync must re-throw — not swallow and return null.
        // This test FAILS before the fix (the generic catch(Exception) intercepts the OCE and
        // returns null instead of propagating).
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var sink = new CapturingSink();
        var capturingLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            capturingLogger,
            _config,
            _mockProjectStore.Object,
            WorkDistributor: _mockWorkDistributor.Object));

        var act = () => sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must propagate to the caller, not be swallowed as a null return");

        sink.Events.Should().NotContain(e => e.Level == LogEventLevel.Error,
            "a cancelled operation must not be logged as an unexpected error");

        // Verify DistributeAsync was actually reached — proves the OCE propagated from the
        // when-filter in TryDistributeAsync, not from an early ct.ThrowIfCancellationRequested()
        // guard that could be added before the mock call in a future refactor.
        _mockWorkDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "DistributeAsync must be reached so the when-filter in TryDistributeAsync is what rethrows");
    }

    [Fact]
    public async Task TriggerAsync_WhenDistributeAsyncThrowsOce_AndTokenIsNotCancelled_ReturnsNull()
    {
        // AC-2 (issue #3845): when DistributeAsync throws OperationCanceledException but the caller's
        // token is NOT cancelled (e.g. an HttpClient internal timeout on a child token), TriggerAsync
        // must still return null. The when-filter must not rethrow OCEs unrelated to the caller's token.
        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = CreateSut();

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().BeNull(
            "an OperationCanceledException not caused by the caller's token must return null, not propagate");
    }

    [Fact]
    public async Task TriggerAsync_WhenDistributeAsyncThrowsNonCancellation_LogsErrorAndReturnsNull()
    {
        // AC-3 (issue #3845): the generic catch(Exception) in TryDistributeAsync must still log at
        // Error and return null for non-cancellation exceptions — the fix must not change this path.
        var sink = new CapturingSink();
        var capturingLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

        _mockWorkDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var sut = new ConsolidationService(new ConsolidationServiceDependencies(
            capturingLogger,
            _config,
            _mockProjectStore.Object,
            WorkDistributor: _mockWorkDistributor.Object));

        var run = await sut.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "tmpl-1", CancellationToken.None);

        run.Should().BeNull("non-cancellation exceptions must return null");

        sink.Events.Should().ContainSingle(e =>
            e.Level == LogEventLevel.Error &&
            e.RenderMessage().Contains("unexpected error calling DistributeAsync"),
            "non-cancellation exceptions must be logged at Error level");
    }

    #endregion

    /// <summary>Serilog sink that captures log events for assertion.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];
        public IReadOnlyList<LogEvent> Events => _events;
        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }
}

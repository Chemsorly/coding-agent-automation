using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationDispatcher.DispatchRunAsync"/>.
/// Tests the dispatcher in isolation — all dependencies are mocked.
/// </summary>
public sealed class ConsolidationDispatcherTests
{
    private readonly Mock<IWorkDistributor> _workDistributor = new();
    private readonly Mock<IAgentProfileStore> _profileStore = new();
    private readonly Mock<IConsolidationWorkspaceManager> _workspaceManager = new();
    private readonly Mock<IPipelineConfigStore> _configStore = new();
    private readonly Mock<IConsolidationService> _consolidationService = new();

    private ConsolidationDispatcher CreateSut() => new(
        _workDistributor.Object,
        _profileStore.Object,
        _workspaceManager.Object,
        _configStore.Object,
        _consolidationService.Object);

    private void SetupDefaults()
    {
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        // Provide a default enabled profile so ResolveSelector has a valid selector.
        // Tests that exercise the no-profile (startup race) path must override this explicitly.
        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentProfile
                {
                    Id = "default-profile",
                    DisplayName = "Default",
                    Enabled = true,
                    Priority = 0,
                    MatchLabels = ["kiro", "dotnet", "dotnet10"],
                    AgentProviderConfigId = "provider-1"
                }
            });

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");
    }

    // ── Happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchRunAsync_SuccessfulDispatch_CallsDistributeAsyncOnce()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, "wi-1", null));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        _workDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Failure paths — must not throw ────────────────────────────────────

    /// <summary>
    /// When DistributeAsync returns Success=false (e.g. no PVC, concurrency limit),
    /// DispatchRunAsync must not throw. The run stays Queued for rehydration to retry.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_DistributeReturnsFailed_DoesNotThrow()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null, "No PVC available (503)"));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        // Must complete without throwing — failure is swallowed and logged.
        // Verify DistributeAsync was still called (failure happened after the call, not before).
        await sut.DispatchRunAsync(run, CancellationToken.None);

        _workDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "DistributeAsync must be called even when the result is failure — swallow happens after the call");
    }

    /// <summary>
    /// When DistributeAsync throws (network error, unexpected exception),
    /// DispatchRunAsync must swallow the exception and not propagate it.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_DistributeThrows_DoesNotThrow()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("K8s API unreachable"));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        // Must complete without throwing — exception is swallowed and logged.
        // Verify DistributeAsync was called (throw happened inside it, not before).
        await sut.DispatchRunAsync(run, CancellationToken.None);

        _workDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "DistributeAsync must be called — the exception comes from inside it, not before");
    }

    /// <summary>
    /// When LoadPipelineConfigAsync throws, DispatchRunAsync must swallow it.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_ConfigLoadThrows_DoesNotThrow()
    {
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Config store unavailable"));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // Distributor must not have been called — exception happened before that
        _workDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Null guard ────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchRunAsync_NullRun_ThrowsArgumentNullException()
    {
        var sut = CreateSut();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.DispatchRunAsync(null!, CancellationToken.None));
    }

    // ── Request field correctness ─────────────────────────────────────────

    [Fact]
    public async Task DispatchRunAsync_SetsCorrectTaskType_AndProviderConfigId()
    {
        SetupDefaults();
        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, null, null));

        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "tmpl-99",
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            AutoDispatch = true,
            TraceParent = "00-abc123-def456-01"
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(WorkItemTaskType.Consolidation, captured!.TaskType);
        Assert.Equal(ConsolidationConstants.ProviderConfigId, captured.IssueProviderConfigId);
        Assert.Equal(ConsolidationConstants.InitiatedBy, captured.InitiatedBy);
        Assert.Equal("", captured.RepoProviderConfigId);
        Assert.Equal(runId, captured.IssueIdentifier);
        Assert.Equal(runId, captured.RunId);
        Assert.Equal("tmpl-99", captured.ConsolidationTemplateId);
        Assert.Equal(ConsolidationRunType.RefactoringDetection, captured.ConsolidationRunType);
        Assert.True(captured.AutoDispatch);
        Assert.NotNull(captured.TraceContext);
        Assert.Equal("00-abc123-def456-01", captured.TraceContext!["traceparent"]);
    }

    [Fact]
    public async Task DispatchRunAsync_NullTraceParent_SetsNullTraceContext()
    {
        SetupDefaults();
        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, null, null));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            TraceParent = null
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        Assert.Null(captured!.TraceContext);
    }

    // ── Selector fallback when QueuedRequiredLabels is null ───────────────

    /// <summary>
    /// Regression test for backward compat: old runs with QueuedRequiredLabels = null (persisted
    /// before the source fix) AND DefaultRequiredAgentLabels configured should use the default
    /// labels to resolve the selector via the dispatcher fallback.
    /// This exercises the actual fallback branch (profiles = [] → uses DefaultRequiredAgentLabels).
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_OldRunNullLabels_EmptyProfiles_WithDefault_UsesDefaultLabels()
    {
        // Arrange: empty profiles (startup race or pre-fix DB row), DefaultRequiredAgentLabels set
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { DefaultRequiredAgentLabels = "kiro,dotnet,dotnet10" });

        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>()); // empty — startup race

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, null, null));

        // An old persisted run — QueuedRequiredLabels was never set
        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = null // pre-fix run
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // The fallback branch must use DefaultRequiredAgentLabels as the raw selector
        // (no profile match possible since profiles = [], falls back to raw label strings)
        Assert.NotNull(captured);
        Assert.Equal(AgentSelectorKey.From(["kiro", "dotnet", "dotnet10"]), captured!.AgentSelector);
        Assert.NotEmpty(captured.AgentSelector);
    }

    /// <summary>
    /// When QueuedRequiredLabels is null and DefaultRequiredAgentLabels is not configured,
    /// the dispatcher falls back to the first enabled profile's MatchLabels.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_NullRequiredLabels_NoDefault_FallsBackToFirstProfile()
    {
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var profile = new AgentProfile
        {
            Id = "profile-1",
            DisplayName = "Kiro Dotnet",
            Enabled = true,
            Priority = 0,
            MatchLabels = ["kiro", "dotnet", "dotnet10"],
            AgentProviderConfigId = "provider-1"
        };
        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { profile });

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, null, null));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = null
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(AgentSelectorKey.From(profile.MatchLabels), captured!.AgentSelector);
        Assert.NotEmpty(captured.AgentSelector);
    }

    /// <summary>
    /// Regression test for issue #2584: when no profiles are available and no defaults are
    /// configured, dispatch is skipped entirely — DistributeAsync is NOT called.
    /// Previously the empty selector was passed to DistributeAsync which returned 422 →
    /// IsPermanentFailure=true → FailRunSafelyAsync cascaded the run to Failed.
    /// Now, the run stays Queued for ConsolidationRetryBackgroundService to retry on the next
    /// sweep, giving the profiles time to be loaded.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_OldRunNullLabels_EmptyProfiles_NoDefault_SkipsDispatch()
    {
        // Arrange: startup race — profiles empty AND no default labels
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration()); // DefaultRequiredAgentLabels = null

        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>()); // empty — startup race

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = null
        };

        var sut = CreateSut();
        // Must not throw — graceful degradation, run stays Queued for retry sweep
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // DistributeAsync must NOT be called — skipping prevents empty-selector 422 cascade to Failed
        _workDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Startup race (no profiles, no default labels) must skip dispatch entirely");

        // UpdateRunAsync must NOT be called — run stays Queued
        _consolidationService.Verify(
            s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()),
            Times.Never,
            "Startup race skip must not change run status");
    }

    /// <summary>
    /// When QueuedRequiredLabels is null and DefaultRequiredAgentLabels is configured,
    /// the dispatcher uses DefaultRequiredAgentLabels to resolve the profile.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_NullRequiredLabels_WithDefault_UsesDefaultLabels()
    {
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { DefaultRequiredAgentLabels = "kiro,dotnet" });

        var matchingProfile = new AgentProfile
        {
            Id = "profile-kiro",
            DisplayName = "Kiro Dotnet 10",
            Enabled = true,
            Priority = 0,
            MatchLabels = ["kiro", "dotnet", "dotnet10"],
            AgentProviderConfigId = "provider-1"
        };
        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { matchingProfile });

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, null, null));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = null
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        Assert.NotNull(captured);
        // Profile's full MatchLabels (superset of default) should be used
        Assert.Equal(AgentSelectorKey.From(matchingProfile.MatchLabels), captured!.AgentSelector);
    }

    // ── Permanent failure cascade ──────────────────────────────────────────

    /// <summary>
    /// Regression test for issue #2536: when DistributeAsync returns IsPermanentFailure=true
    /// (422 from the API — no job template for selector), the run must be cascaded to Failed
    /// via UpdateRunAsync rather than left Queued forever.
    /// </summary>
    // TODO [WARNING]: The DistributionResult constructed below uses the default Queued=false.
    // The production record can theoretically have both Queued=true and IsPermanentFailure=true
    // simultaneously, and no test covers what ConsolidationDispatcher does in that scenario.
    // Add an additional test case with Queued=true, IsPermanentFailure=true to lock in the
    // expected behavior. (review-findings.md TestQualityReviewer warning, line 435)
    [Fact]
    public async Task DispatchRunAsync_PermanentFailure_CascadesToFailed()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null,
                "No job template for agent selector 'kiro,python,python312': 422 Unprocessable Entity",
                IsPermanentFailure: true));

        _consolidationService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), ConsolidationRunStatus.Failed,
                It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // Must cascade to Failed — permanent dispatch failures must not leave runs Queued forever.
        // TODO [WARNING]: The Moq Setup and Verify below use a 5-argument overload (including the
        // optional 'totalTokens' long parameter). The production call in FailRunSafelyAsync omits
        // that optional parameter (4-argument call, default applied by compiler). Moq expands
        // default args so this currently matches, but if the signature changes (parameter removed
        // or reordered) the Verify will silently stop matching the production call — producing a
        // false negative. Fix: use a 4-argument Setup/Verify without the optional parameter to
        // match the actual production invocation surface. (review-findings.md TestQualityReviewer warning)
        _consolidationService.Verify(
            s => s.UpdateRunAsync(
                It.Is<RunId>(r => r.Value == runId),
                ConsolidationRunStatus.Failed,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<long>()),
            Times.Once,
            "Permanent dispatch failure (IsPermanentFailure=true) must cascade run to Failed");
    }

    /// <summary>
    /// When DistributeAsync returns Success=false but IsPermanentFailure=false (transient:
    /// capacity limit, PVC unavailable), the run must NOT be cascaded to Failed — it should
    /// remain Queued for the next restart rehydration attempt.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_TransientFailure_DoesNotCascadeToFailed()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null,
                "No capacity (409): Concurrency limit reached",
                IsPermanentFailure: false)); // transient — capacity

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // Transient failure: run stays Queued — UpdateRunAsync must NOT be called.
        // Use the 4-argument Verify (omitting optional totalTokens) to match the actual
        // production call in FailRunSafelyAsync, which omits totalTokens (compiler default).
        // A 5-arg Verify would vacuously pass even when the 4-arg production call fires —
        // Moq would never see it (CRITICAL fix — review-findings.md TestQualityReviewer, line 495).
        _consolidationService.Verify(
            s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Transient failure (IsPermanentFailure=false) must NOT cascade run to Failed");
    }

    /// <summary>
    /// When UpdateRunAsync throws during a permanent-failure cascade, DispatchRunAsync must
    /// not propagate the secondary exception (swallow and log).
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_PermanentFailure_UpdateRunAsyncThrows_DoesNotThrow()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null, "no template",
                IsPermanentFailure: true));

        _consolidationService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        // Must not throw — secondary failure in UpdateRunAsync is swallowed.
        await sut.DispatchRunAsync(run, CancellationToken.None);
    }

    // ── Unified dispatch path: Queued=true transition ─────────────────────

    /// <summary>
    /// On the unified dispatch path, when DistributeAsync returns Success=true and Queued=true,
    /// the ConsolidationRun must be transitioned to Pending via UpdateRunAsync.
    /// This prevents RehydrateQueuedRunsAsync from re-dispatching the run on every sweep
    /// (which would produce a recurring 409 loop against the existing Pending WorkItem).
    /// </summary>
    // TODO [WARNING]: Missing test: DispatchRunAsync_SuccessQueued_CancelledToken_StillTransitionsToPending.
    // When DistributeAsync returns Queued=true but the caller's CancellationToken is already cancelled
    // at that point (e.g. HTTP request aborted), the Pending transition must still complete because
    // TransitionToPendingSafelyAsync uses CancellationToken.None. A test that passes a pre-cancelled
    // token to DispatchRunAsync and asserts UpdateRunAsync is still called with Pending would lock in
    // this contract and prevent a regression if the CancellationToken.None is ever reverted.
    [Fact]
    public async Task DispatchRunAsync_SuccessQueued_TransitionsRunToPending()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, "wi-42", null, Queued: true));

        _consolidationService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // Must transition to Pending — not stay Queued, not cascade to Failed.
        _consolidationService.Verify(
            s => s.UpdateRunAsync(
                It.Is<RunId>(r => r.Value == runId),
                ConsolidationRunStatus.Pending,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<long>()),
            Times.Once,
            "Unified-path success (Queued=true) must transition ConsolidationRun to Pending " +
            "so the retry sweep does not re-dispatch it");
    }

    /// <summary>
    /// When DistributeAsync returns Success=true and Queued=false (legacy synchronous dispatch),
    /// UpdateRunAsync must NOT be called — the run stays as-is and the agent will transition it.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_SuccessNotQueued_DoesNotCallUpdateRunAsync()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, "wi-1", null, Queued: false));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // Legacy synchronous path: no state update — the agent transitions the run.
        _consolidationService.Verify(
            s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()),
            Times.Never,
            "Legacy synchronous success (Queued=false) must not call UpdateRunAsync");
    }

    /// <summary>
    /// When UpdateRunAsync throws during the Pending transition, DispatchRunAsync must not
    /// propagate the secondary exception (swallow and log).
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_SuccessQueued_UpdateRunAsyncThrows_DoesNotThrow()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, "wi-1", null, Queued: true));

        _consolidationService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var sut = CreateSut();
        // Must not throw — secondary failure in UpdateRunAsync is swallowed.
        await sut.DispatchRunAsync(run, CancellationToken.None);
    }

    // ── Empty selector: no profiles (startup race) ─────────────────────────

    /// <summary>
    /// When no profiles are available and no defaults are configured (startup race),
    /// DispatchRunAsync must NOT call DistributeAsync at all — the run stays Queued
    /// so the retry background service can attempt again on the next sweep.
    /// Previously, an empty selector was passed to DistributeAsync which returned 422 →
    /// IsPermanentFailure=true → FailRunSafelyAsync cascaded the run to Failed — wrong
    /// for a transient startup-race condition.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_NoProfiles_NoDefaultLabels_SkipsDispatch()
    {
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration()); // no DefaultRequiredAgentLabels

        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>()); // startup race — no profiles yet

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = null
        };

        var sut = CreateSut();
        // Must not throw
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // DistributeAsync must NOT be called — skipping prevents the empty-selector 422 cascade
        _workDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "When no profiles available (startup race), dispatch must be skipped entirely " +
            "to prevent empty-selector 422 from permanently cascading the run to Failed");

        // UpdateRunAsync must also NOT be called — run stays Queued
        _consolidationService.Verify(
            s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<long>()),
            Times.Never,
            "Startup-race skip must not update run status");
    }

    /// <summary>
    /// Regression test for issue #2536: when QueuedRequiredLabels is null AND multiple enabled
    /// profiles exist (including some with no job template), the dispatcher must NOT call
    /// ResolveByRequiredLabels with empty labels (which would pick an arbitrary profile via
    /// Superset matching). Instead, it must fall back to the first enabled profile explicitly,
    /// which is the expected deterministic behaviour when no required labels are configured.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_NullRequiredLabels_MultipleProfiles_NoDefault_UsesFirstEnabledProfile()
    {
        // Multiple enabled profiles — with the old bug, empty required labels would be passed
        // to ResolveByRequiredLabels, which matches ALL enabled profiles via Superset, and the
        // tiebreak (count desc, priority desc, Id asc) would pick the profile with the HIGHEST
        // priority. profileB has Priority=10 > profileA's Priority=0, so the old code would
        // pick profileB. The fix uses FirstOrDefault() in list order, which picks profileA
        // (first in list). The two paths now produce DIFFERENT results, making this test an
        // effective regression guard (CRITICAL fix — review-findings.md TestQualityReviewer, line 553).
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration()); // no DefaultRequiredAgentLabels

        var profileA = new AgentProfile
        {
            Id = "aaa-python",
            DisplayName = "Python",
            Enabled = true,
            Priority = 0,                                  // lower priority
            MatchLabels = ["kiro", "python", "python312"], // no job template for this
            AgentProviderConfigId = "provider-1"
        };
        var profileB = new AgentProfile
        {
            Id = "bbb-dotnet",
            DisplayName = "Dotnet",
            Enabled = true,
            Priority = 10,                                 // higher priority — old Superset tiebreak picks this
            MatchLabels = ["kiro", "dotnet", "dotnet10"],  // has a job template
            AgentProviderConfigId = "provider-1"
        };
        // Old buggy path: ResolveByRequiredLabels(profiles, []) via Superset matches both profiles;
        // tiebreak (count desc=tie, priority desc) picks profileB (Priority=10 wins over 0).
        // New path: profiles.FirstOrDefault(p => p.Enabled) picks profileA (first in list).
        // The assertion on profileA.MatchLabels will FAIL if the code reverts to the old path,
        // proving this is a genuine regression guard.
        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { profileA, profileB });

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, null, null));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = null // old run with no baked labels
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // Must use the first enabled profile in list order (profileA) — not the highest-priority
        // profile that the old Superset-with-empty-labels path would have selected (profileB).
        Assert.NotNull(captured);
        Assert.Equal(AgentSelectorKey.From(profileA.MatchLabels), captured!.AgentSelector);
    }

    // ── ProjectId and ProjectName forwarding ──────────────────────────────────

    /// <summary>
    /// Template-scoped consolidation run with a valid ProjectId must forward both
    /// ProjectId (parsed to Guid?) and ProjectName to the JobDistributionRequest.
    /// This is the acceptance criterion: WorkItemEntity.ProjectId will be non-null
    /// and the Work queue UI PROJECT column will show the project name.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_TemplateRunWithProjectId_ForwardsProjectIdToRequest()
    {
        SetupDefaults();
        var someGuid = Guid.NewGuid();
        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, "wi-1", null));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProjectId = someGuid.ToString(),
            ProjectName = "MyProject"
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(someGuid, captured!.ProjectId);
        Assert.Equal("MyProject", captured.ProjectName);
    }

    /// <summary>
    /// Global HarnessSuggestions run (no template, no project) must produce null ProjectId
    /// and null ProjectName in the request — the Work queue shows "—" as expected.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_GlobalRunWithNullProjectId_ProjectIdIsNullInRequest()
    {
        SetupDefaults();
        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, "wi-1", null));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.HarnessSuggestions,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProjectId = null,
            ProjectName = null
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured!.ProjectId);
        Assert.Null(captured.ProjectName);
    }

    /// <summary>
    /// When ConsolidationRun.ProjectId is not a valid GUID string, the Guid.TryParse guard
    /// must produce null without throwing — ProjectId in the request is null, not an exception.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_RunWithInvalidProjectIdGuid_ProjectIdIsNullInRequest()
    {
        SetupDefaults();
        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, "wi-1", null));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProjectId = "not-a-guid"
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured!.ProjectId); // Guid.TryParse returns false for "not-a-guid" → null, not an exception
    }
}

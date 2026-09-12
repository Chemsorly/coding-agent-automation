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

        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        _consolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);
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
    /// When QueuedRequiredLabels is null, no DefaultRequiredAgentLabels is configured, and
    /// only one profile exists, the dispatcher should dispatch with an empty selector (no baked
    /// labels and no default → cannot determine a correct selector).
    /// This test documents the single-profile degenerate case only; see
    /// DispatchRunAsync_NullRequiredLabels_MultipleProfiles_NoDefault_DoesNotPickArbitraryProfile
    /// for the multi-profile regression that exposed the Superset-wrong-pick bug.
    /// </summary>
    // TODO: The DistributeAsync mock returns DistributionResult(true, null, null) (success) for an
    // empty selector. This masks the cascade path: when the production code dispatches with an empty
    // selector to the real endpoint, it returns 422 (permanent failure) and IsPermanentFailure=true,
    // causing UpdateRunAsync(Failed) to be called. Using Success=true here hides whether the cascade
    // path is exercised. The mock should return IsPermanentFailure=true and the test should assert
    // UpdateRunAsync(Failed) is called to verify the intended failure cascade for this warning path.
    // See review-findings.md [WARNING] ConsolidationDispatcherTests.cs:341.
    [Fact]
    public async Task DispatchRunAsync_NullRequiredLabels_NoDefault_SingleProfile_DispatchesWithEmptySelector()
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

        _consolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

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

        // null QueuedRequiredLabels + no DefaultRequiredAgentLabels → empty selector warning path.
        // DistributeAsync is still called (graceful degradation, run stays Queued for retry).
        Assert.NotNull(captured);
        Assert.Equal(AgentSelectorKey.From([]), captured!.AgentSelector);
    }

    /// <summary>
    /// When profiles are empty (startup race) AND no DefaultRequiredAgentLabels configured,
    /// dispatch proceeds with an empty selector — the distributor call will 409,
    /// run stays Queued for rehydration. No throw.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_OldRunNullLabels_EmptyProfiles_NoDefault_ProducesEmptySelector()
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

        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null, "No agent selector"));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = null
        };

        var sut = CreateSut();
        // Must not throw — graceful degradation, run stays Queued for rehydration retry
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // DistributeAsync is still called (with empty selector), the 409 is swallowed
        _workDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// When QueuedRequiredLabels is null and DefaultRequiredAgentLabels is configured,
    /// the dispatcher uses DefaultRequiredAgentLabels to resolve the profile — bypassing
    /// the Superset-match that would otherwise pick an arbitrary profile.
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

        _consolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

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
        // Profile's full MatchLabels (superset of default) should be used.
        // This verifies the DefaultRequiredAgentLabels fallback path is actually taken
        // (not the Superset-match path that would silently pick an arbitrary profile).
        Assert.Equal(AgentSelectorKey.From(matchingProfile.MatchLabels), captured!.AgentSelector);
    }

    // ── Regression: multi-profile Superset-wrong-pick bug (Fix 1A) ───────────

    /// <summary>
    /// Regression test for the Superset-wrong-pick bug (Issue #2536).
    /// When QueuedRequiredLabels is null, no DefaultRequiredAgentLabels is configured, and
    /// multiple profiles exist, the dispatcher must NOT call DistributeAsync with the
    /// Id-lexicographically-smallest profile's MatchLabels (the result of the broken
    /// Superset-match on an empty required set). Instead it must use an empty selector
    /// and log a warning, since there is no deterministic way to resolve a label without
    /// baked labels or a default.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_NullRequiredLabels_MultipleProfiles_NoDefault_DoesNotPickArbitraryProfile()
    {
        // Arrange: 4 profiles — Id-sorted, the first alphabetically is "profile-java"
        // which would be picked by the broken Superset-match (Id asc tiebreak).
        // The correct behaviour after the fix is to use an empty selector (warning path).
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration()); // no DefaultRequiredAgentLabels

        var profiles = new[]
        {
            new AgentProfile { Id = "profile-dotnet", DisplayName = "Kiro Dotnet", Enabled = true, Priority = 0, MatchLabels = ["kiro", "dotnet", "dotnet10"], AgentProviderConfigId = "p1" },
            new AgentProfile { Id = "profile-java",   DisplayName = "Kiro Java",   Enabled = true, Priority = 0, MatchLabels = ["kiro", "java",   "java21"],   AgentProviderConfigId = "p1" },
            new AgentProfile { Id = "profile-python",  DisplayName = "Kiro Python", Enabled = true, Priority = 0, MatchLabels = ["kiro", "python", "python312"], AgentProviderConfigId = "p1" },
            new AgentProfile { Id = "profile-ruby",    DisplayName = "Kiro Ruby",   Enabled = true, Priority = 0, MatchLabels = ["kiro", "ruby",   "ruby33"],    AgentProviderConfigId = "p1" },
        };
        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles);

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        _consolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(false, null, "No job template for agent selector"));

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

        // After fix: null QueuedRequiredLabels + no default → empty selector (warning path).
        // Must NOT be "kiro,java,java21" (the broken Superset-picked wrong profile).
        Assert.NotNull(captured);
        Assert.NotEqual(AgentSelectorKey.From(["kiro", "java", "java21"]), captured!.AgentSelector);
        Assert.NotEqual(AgentSelectorKey.From(["kiro", "dotnet", "dotnet10"]), captured.AgentSelector);
        Assert.Equal(AgentSelectorKey.From([]), captured.AgentSelector);
    }

    /// <summary>
    /// Regression test for the Superset-wrong-pick bug (Issue #2536), with-default variant.
    /// When QueuedRequiredLabels is null, DefaultRequiredAgentLabels = "kiro,dotnet", and
    /// multiple profiles exist, the dispatcher must resolve via DefaultRequiredAgentLabels
    /// (finding the profile whose MatchLabels cover "kiro,dotnet") — NOT via the Superset-match
    /// on the empty required set that would pick the Id-smallest profile arbitrarily.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_NullRequiredLabels_MultipleProfiles_WithDefault_UsesDefaultProfile_NotArbitraryProfile()
    {
        // Arrange: 4 profiles. "kiro,dotnet" default should resolve to "profile-dotnet"
        // (MatchLabels ["kiro","dotnet","dotnet10"] covers ["kiro","dotnet"]).
        // The broken Superset-match would pick "profile-dotnet" only by accident if it
        // is Id-smallest — we assert the CORRECT profile is picked regardless of Id order.
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { DefaultRequiredAgentLabels = "kiro,dotnet" });

        var profiles = new[]
        {
            // Id "aaa-profile-java" sorts before "profile-dotnet" alphabetically,
            // so the broken code would pick java. The fix must pick dotnet.
            new AgentProfile { Id = "aaa-profile-java",  DisplayName = "Kiro Java",   Enabled = true, Priority = 0, MatchLabels = ["kiro", "java",   "java21"],   AgentProviderConfigId = "p1" },
            new AgentProfile { Id = "profile-dotnet",     DisplayName = "Kiro Dotnet", Enabled = true, Priority = 0, MatchLabels = ["kiro", "dotnet", "dotnet10"], AgentProviderConfigId = "p1" },
            new AgentProfile { Id = "profile-python",     DisplayName = "Kiro Python", Enabled = true, Priority = 0, MatchLabels = ["kiro", "python", "python312"], AgentProviderConfigId = "p1" },
            new AgentProfile { Id = "profile-ruby",       DisplayName = "Kiro Ruby",   Enabled = true, Priority = 0, MatchLabels = ["kiro", "ruby",   "ruby33"],    AgentProviderConfigId = "p1" },
        };
        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles);

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        _consolidationService
            .Setup(s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);

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

        // After fix: DefaultRequiredAgentLabels "kiro,dotnet" → profile-dotnet → ["kiro","dotnet","dotnet10"]
        // Must NOT be "kiro,java,java21" (the wrong Id-smallest Superset-match result).
        Assert.NotNull(captured);
        Assert.Equal(AgentSelectorKey.From(["kiro", "dotnet", "dotnet10"]), captured!.AgentSelector);
        Assert.NotEqual(AgentSelectorKey.From(["kiro", "java", "java21"]), captured.AgentSelector);
    }

    // ── Permanent failure → cascade to Failed (Fix 1C) ───────────────────────

    /// <summary>
    /// When DistributeAsync returns IsPermanentFailure=true (no job template for the selector),
    /// the dispatcher must call IConsolidationService.UpdateRunAsync with Failed status so the
    /// run surfaces in the Attention view rather than silently staying Queued forever.
    /// </summary>
    // TODO: This test only verifies UpdateRunAsync(Failed) is called Times.Once. It does not assert
    // that DistributeAsync was called exactly once before the cascade. Without that assertion, a
    // regression where DistributeAsync is short-circuited (zero calls) would still satisfy the
    // UpdateRunAsync(Failed) expectation if the code cascaded immediately without dispatching.
    // Add: _workDistributor.Verify(d => d.DistributeAsync(...), Times.Once) to pin the dispatch
    // happening before the cascade. See review-findings.md [WARNING] ConsolidationDispatcherTests.cs:593.
    [Fact]
    public async Task DispatchRunAsync_PermanentFailure_CascadesRunToFailed()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null, "Permanent: No job template for agent selector", IsPermanentFailure: true));

        var runId = Guid.NewGuid().ToString();
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = ["kiro", "dotnet", "dotnet10"]
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // Permanent failure must cascade to Failed — run must not stay Queued forever.
        _consolidationService.Verify(
            s => s.UpdateRunAsync(
                It.Is<RunId>(r => r == new RunId(runId)),
                ConsolidationRunStatus.Failed,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<long>()),
            Times.Once,
            "UpdateRunAsync(Failed) must be called for a permanent dispatch failure");
    }

    /// <summary>
    /// When DistributeAsync returns IsPermanentFailure=false (transient: concurrency limit or
    /// PVC unavailable), the dispatcher must NOT cascade to Failed — the run must stay Queued
    /// for rehydration to retry on the next orchestrator restart.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_TransientFailure_LeavesRunQueued_DoesNotCascadeToFailed()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null, "Transient (409): concurrency limit", IsPermanentFailure: false));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = ["kiro", "dotnet", "dotnet10"]
        };

        var sut = CreateSut();
        await sut.DispatchRunAsync(run, CancellationToken.None);

        // Transient failure — run must stay Queued; UpdateRunAsync must NOT be called.
        _consolidationService.Verify(
            s => s.UpdateRunAsync(It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<long>()),
            Times.Never,
            "UpdateRunAsync must NOT be called for a transient dispatch failure — run must stay Queued");
    }
}

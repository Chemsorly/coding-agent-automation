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

    private ConsolidationDispatcher CreateSut() => new(
        _workDistributor.Object,
        _profileStore.Object,
        _workspaceManager.Object,
        _configStore.Object);

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
}

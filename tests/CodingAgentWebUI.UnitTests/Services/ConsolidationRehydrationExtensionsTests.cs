using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationRehydrationExtensions.RunConsolidationStartupAsync"/>.
///
/// Uses a raw <c>WebApplication.CreateBuilder()</c> host to avoid Program.cs fast-fail
/// env-var checks. All services consumed by the extension method are registered as mocks.
/// </summary>
public sealed class ConsolidationRehydrationExtensionsTests
{
    // ── Test fixture ──────────────────────────────────────────────────────

    private readonly Mock<IConsolidationService> _consolidationService = new();
    private readonly Mock<IPipelineApiAgentClient> _apiAgentClient = new();
    private readonly Mock<IPipelineConfigStore> _configStore = new();
    private readonly Mock<IWorkDistributor> _workDistributor = new();
    private readonly Mock<IAgentProfileStore> _profileStore = new();
    private readonly Mock<IConsolidationWorkspaceManager> _workspaceManager = new();

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddSingleton(_consolidationService.Object);
        builder.Services.AddSingleton(_apiAgentClient.Object);
        builder.Services.AddSingleton<IPipelineConfigStore>(_configStore.Object);
        builder.Services.AddSingleton(_workDistributor.Object);
        builder.Services.AddSingleton(_profileStore.Object);
        builder.Services.AddSingleton(_workspaceManager.Object);

        return builder.Build();
    }

    private void SetupDefaults(IReadOnlyList<AgentEntry>? agents = null,
        IReadOnlyList<ConsolidationRun>? queuedRuns = null)
    {
        _apiAgentClient
            .Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(agents ?? Array.Empty<AgentEntry>());

        _consolidationService
            .Setup(s => s.CleanupOrphanedRunsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(queuedRuns ?? Array.Empty<ConsolidationRun>());

        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
    }

    // ── Guard tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunConsolidationStartupAsync_NullApp_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ConsolidationRehydrationExtensions
                .RunConsolidationStartupAsync(null!, new PipelineConfiguration()));
    }

    [Fact]
    public async Task RunConsolidationStartupAsync_NullConfig_Throws()
    {
        await using var app = BuildApp();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => app.RunConsolidationStartupAsync(null!));
    }

    // ── Orphan cleanup ────────────────────────────────────────────────────

    [Fact]
    public async Task RunConsolidationStartupAsync_NoLiveAgents_CallsCleanupWithEmptySet()
    {
        SetupDefaults(agents: Array.Empty<AgentEntry>());
        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync(new PipelineConfiguration());

        _consolidationService.Verify(
            s => s.CleanupOrphanedRunsAsync(
                It.Is<IReadOnlyCollection<string>>(set => set.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunConsolidationStartupAsync_AgentWithActiveJob_ExcludesItFromOrphanSet()
    {
        // An agent actively running a job should NOT be treated as orphaned
        var activeAgent = new AgentEntry
        {
            AgentId = "agent-1",
            ConnectionId = "conn-1",
            Hostname = "k8s-pod",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            ActiveJobId = "job-abc-123"
        };
        SetupDefaults(agents: [activeAgent]);
        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync(new PipelineConfiguration());

        // The active job ID should NOT be in the orphan set — runs with that job ID are preserved
        _consolidationService.Verify(
            s => s.CleanupOrphanedRunsAsync(
                It.Is<IReadOnlyCollection<string>>(set =>
                    set.Count == 1 && set.Contains("job-abc-123")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunConsolidationStartupAsync_ApiAgentClientThrows_TreatsAllRunsAsOrphaned()
    {
        // If the API is unreachable at startup, all running runs become orphan candidates
        _apiAgentClient
            .Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        _consolidationService
            .Setup(s => s.CleanupOrphanedRunsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _consolidationService
            .Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        await using var app = BuildApp();

        // Should not throw — exception is swallowed and treated as "no live agents"
        await app.RunConsolidationStartupAsync(new PipelineConfiguration());

        _consolidationService.Verify(
            s => s.CleanupOrphanedRunsAsync(
                It.Is<IReadOnlyCollection<string>>(set => set.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Rehydration ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunConsolidationStartupAsync_NoQueuedRuns_DoesNotCallDistributor()
    {
        SetupDefaults(queuedRuns: Array.Empty<ConsolidationRun>());
        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync(new PipelineConfiguration());

        // No runs to rehydrate → distributor never touched
        _workDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunConsolidationStartupAsync_WithQueuedRuns_DispatchesEachRun()
    {
        var runId1 = Guid.NewGuid().ToString();
        var runId2 = Guid.NewGuid().ToString();

        var queuedRuns = new List<ConsolidationRun>
        {
            new() { RunId = runId1, Type = ConsolidationRunType.BrainConsolidation,
                    TemplateId = "tmpl-1", Status = ConsolidationRunStatus.Queued,
                    StartedAtUtc = DateTimeOffset.UtcNow },
            new() { RunId = runId2, Type = ConsolidationRunType.RefactoringDetection,
                    TemplateId = "tmpl-2", Status = ConsolidationRunStatus.Queued,
                    StartedAtUtc = DateTimeOffset.UtcNow }
        };

        SetupDefaults(queuedRuns: queuedRuns);

        _workspaceManager
            .Setup(w => w.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns<RunId>(r => $"/workspaces/{r}");

        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, null, null));

        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync(new PipelineConfiguration());

        // One dispatch per queued run
        _workDistributor.Verify(
            d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunConsolidationStartupAsync_QueuedRunHasRequiredLabels_ResolvesSelectorFromProfile()
    {
        var runId = Guid.NewGuid().ToString();
        var requiredLabels = new List<string> { "kiro", "dotnet" };

        var queuedRun = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "tmpl-1",
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = requiredLabels
        };

        var matchingProfile = new AgentProfile
        {
            Id = "profile-dotnet",
            DisplayName = "DotNet Profile",
            AgentProviderConfigId = "provider-1",
            MatchLabels = ["kiro", "dotnet", "dotnet10"]
        };

        SetupDefaults(queuedRuns: [queuedRun]);

        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { matchingProfile });

        _workspaceManager
            .Setup(w => w.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        JobDistributionRequest? capturedRequest = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new DistributionResult(true, null, null));

        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync(new PipelineConfiguration());

        Assert.NotNull(capturedRequest);
        // AgentSelector should be built from profile's MatchLabels (broader set), not requiredLabels
        Assert.Equal(AgentSelectorKey.From(matchingProfile.MatchLabels), capturedRequest!.AgentSelector);
    }

    [Fact]
    public async Task RunConsolidationStartupAsync_QueuedRunNoMatchingProfile_FallsBackToRequiredLabels()
    {
        var runId = Guid.NewGuid().ToString();
        var requiredLabels = new List<string> { "opencode", "python" };

        var queuedRun = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.HarnessSuggestions,
            TemplateId = "tmpl-3",
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            QueuedRequiredLabels = requiredLabels
        };

        // No profile matches "opencode,python"
        SetupDefaults(queuedRuns: [queuedRun]);

        _workspaceManager
            .Setup(w => w.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        JobDistributionRequest? capturedRequest = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new DistributionResult(true, null, null));

        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync(new PipelineConfiguration());

        Assert.NotNull(capturedRequest);
        // No profile match → falls back to requiredLabels themselves as the selector
        Assert.Equal(AgentSelectorKey.From(requiredLabels), capturedRequest!.AgentSelector);
    }

    [Fact]
    public async Task RunConsolidationStartupAsync_DispatchRequest_HasCorrectConsolidationFields()
    {
        var runId = Guid.NewGuid().ToString();
        const string workspacePath = "/workspaces/consolidation";

        var queuedRun = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "tmpl-refactor",
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow,
            AutoDispatch = true
        };

        SetupDefaults(queuedRuns: [queuedRun]);
        _workspaceManager.Setup(w => w.GetWorkspacePath(It.IsAny<RunId>())).Returns(workspacePath);

        JobDistributionRequest? captured = null;
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new DistributionResult(true, null, null));

        await using var app = BuildApp();
        await app.RunConsolidationStartupAsync(new PipelineConfiguration());

        Assert.NotNull(captured);
        Assert.Equal(runId, captured!.IssueIdentifier);
        Assert.Equal(WorkItemTaskType.Consolidation, captured.TaskType);
        Assert.Equal(ConsolidationRunType.RefactoringDetection, captured.ConsolidationRunType);
        Assert.Equal("tmpl-refactor", captured.ConsolidationTemplateId);
        Assert.Equal(workspacePath, captured.ConsolidationWorkspacePath);
        Assert.Equal(runId, captured.RunId);
        Assert.True(captured.AutoDispatch);
    }
}

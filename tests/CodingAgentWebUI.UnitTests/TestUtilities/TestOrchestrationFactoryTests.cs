using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.UnitTests.TestUtilitiesTests;

/// <summary>
/// Tests for <see cref="TestOrchestrationFactory"/> helper utilities.
/// Exercises <see cref="TestOrchestrationFactory.NoOpLabelService"/>,
/// <see cref="TestOrchestrationFactory.NullHistoryService"/>, and
/// <see cref="CreateMinimalOptions"/> to ensure they're constructed and invoked correctly.
/// </summary>
public class TestOrchestrationFactoryTests
{
    // ── NoOpLabelService ──────────────────────────────────────────────────

    [Fact]
    public void NoOpLabelService_Instance_IsNotNull()
    {
        TestOrchestrationFactory.NoOpLabelService.Instance.Should().NotBeNull();
    }

    [Fact]
    public async Task NoOpLabelService_SwapLabelAsync_WithTargetKind_Completes()
    {
        var svc = TestOrchestrationFactory.NoOpLabelService.Instance;
        var act = () => svc.SwapLabelAsync("ip-1", "owner/repo#1", "agent:done",
            LabelTargetKind.Issue, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NoOpLabelService_SwapLabelAsync_WithExpectedLabel_Completes()
    {
        var svc = TestOrchestrationFactory.NoOpLabelService.Instance;
        var act = () => svc.SwapLabelAsync("ip-1", "owner/repo#1", "agent:done",
            LabelTargetKind.Issue, "agent:in-progress", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NoOpLabelService_SwapLabelStrictAsync_Completes()
    {
        var svc = TestOrchestrationFactory.NoOpLabelService.Instance;
        var act = () => svc.SwapLabelStrictAsync("ip-1", "owner/repo#1", "agent:done",
            LabelTargetKind.Issue, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NoOpLabelService_EnsureAgentLabelsAsync_ReturnsTrue()
    {
        var svc = TestOrchestrationFactory.NoOpLabelService.Instance;
        var result = await svc.EnsureAgentLabelsAsync("ip-1", LabelTargetKind.Issue, CancellationToken.None);
        result.Should().BeTrue();
    }

    // ── NullHistoryService ────────────────────────────────────────────────

    [Fact]
    public async Task NullHistoryService_GetRunHistoryAsync_InitiallyEmpty()
    {
        var svc = new TestOrchestrationFactory.NullHistoryService();
        var result = await svc.GetRunHistoryAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task NullHistoryService_AddRunToHistoryAsync_PersistsRun()
    {
        var svc = new TestOrchestrationFactory.NullHistoryService();
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "test"
        });
        run.CurrentStep = PipelineStep.Completed;
        run.MarkCompleted();

        await svc.AddRunToHistoryAsync(run, CancellationToken.None);

        var history = await svc.GetRunHistoryAsync();
        history.Should().HaveCount(1);
        history[0].IssueIdentifier.Should().Be((IssueIdentifier)"owner/repo#1");
    }

    [Fact]
    public async Task NullHistoryService_GetRunHistoryAsync_Paged_ReturnsCorrectPage()
    {
        var svc = new TestOrchestrationFactory.NullHistoryService();

        // Add 3 runs
        for (int i = 1; i <= 3; i++)
        {
            var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = $"owner/repo#{i}",
                IssueTitle = $"Issue {i}",
                IssueProviderConfigId = "ip-1",
                RepoProviderConfigId = "rp-1",
                InitiatedBy = "test"
            });
            run.CurrentStep = PipelineStep.Completed;
            run.MarkCompleted();
            await svc.AddRunToHistoryAsync(run);
        }

        var page1 = await svc.GetRunHistoryAsync(page: 1, pageSize: 2);
        page1.Items.Should().HaveCount(2);
        page1.HasMore.Should().BeTrue();
        page1.Page.Should().Be(1);

        var page2 = await svc.GetRunHistoryAsync(page: 2, pageSize: 2);
        page2.Items.Should().HaveCount(1);
        page2.HasMore.Should().BeFalse();
    }

    [Fact]
    public void NullHistoryService_TryDeleteWorkspace_DoesNotThrow()
    {
        var svc = new TestOrchestrationFactory.NullHistoryService();
        var act = () => svc.TryDeleteWorkspace("/tmp/workspace", "run-1", "/tmp");
        act.Should().NotThrow();
    }

    [Fact]
    public void NullHistoryService_CleanupExpiredWorkspaces_DoesNotThrow()
    {
        var svc = new TestOrchestrationFactory.NullHistoryService();
        var act = () => svc.CleanupExpiredWorkspaces(new PipelineConfiguration());
        act.Should().NotThrow();
    }

    // ── CreateMinimalOptions ──────────────────────────────────────────────

    [Fact]
    public void CreateMinimalOptions_DefaultsAllNull()
    {
        var opts = new CreateMinimalOptions();
        opts.ConfigStore.Should().BeNull();
        opts.ProviderFactory.Should().BeNull();
        opts.CancellationFacade.Should().BeNull();
        opts.Lifecycle.Should().BeNull();
        opts.LabelService.Should().BeNull();
        opts.Logger.Should().BeNull();
        opts.HistoryService.Should().BeNull();
        opts.RunService.Should().BeNull();
        opts.OrchestrationService.Should().BeNull();
    }

    [Fact]
    public void CreateMinimalOptions_WithProperties_StoresValues()
    {
        var logger = Mock.Of<Serilog.ILogger>();
        var historyService = new TestOrchestrationFactory.NullHistoryService();

        var opts = new CreateMinimalOptions
        {
            Logger = logger,
            HistoryService = historyService
        };

        opts.Logger.Should().BeSameAs(logger);
        opts.HistoryService.Should().BeSameAs(historyService);
    }

    // ── TestOrchestrationFactory.CreateMinimal — exception for missing deps ─

    [Fact]
    public void CreateMinimal_NullConfigStore_ThrowsArgumentNullException()
    {
        var act = () => TestOrchestrationFactory.CreateMinimal(new CreateMinimalOptions
        {
            ProviderFactory = Mock.Of<IProviderFactory>()
            // ConfigStore intentionally null
        });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateMinimal_NullProviderFactory_ThrowsArgumentNullException()
    {
        var act = () => TestOrchestrationFactory.CreateMinimal(new CreateMinimalOptions
        {
            ConfigStore = Mock.Of<IConfigurationStore>()
            // ProviderFactory intentionally null
        });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateMinimal_WithRequiredDeps_ReturnsInstance()
    {
        var svc = TestOrchestrationFactory.CreateMinimal(new CreateMinimalOptions
        {
            ConfigStore = Mock.Of<IConfigurationStore>(),
            ProviderFactory = Mock.Of<IProviderFactory>()
        });
        svc.Should().NotBeNull();
    }

    // ── TestOrchestrationFactory.CreateMinimalRunCreator ─────────────────

    [Fact]
    public void CreateMinimalRunCreator_NullConfigStore_Throws()
    {
        var act = () => TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: null,
            providerFactory: Mock.Of<IProviderFactory>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateMinimalRunCreator_NullProviderFactory_Throws()
    {
        var act = () => TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: Mock.Of<IConfigurationStore>(),
            providerFactory: null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateMinimalRunCreator_WithRequiredDeps_ReturnsInstance()
    {
        var creator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: Mock.Of<IConfigurationStore>(),
            providerFactory: Mock.Of<IProviderFactory>());
        creator.Should().NotBeNull();
    }
}

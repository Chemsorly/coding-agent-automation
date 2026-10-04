using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Moq;

namespace CodingAgent.Web.UnitTests.TestUtilitiesTests;

/// <summary>
/// Tests for <see cref="TestOrchestrationFactory"/> helper utilities.
/// Exercises <see cref="TestOrchestrationFactory.NoOpLabelService"/>
/// and <see cref="TestOrchestrationFactory.NullHistoryService"/>.
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

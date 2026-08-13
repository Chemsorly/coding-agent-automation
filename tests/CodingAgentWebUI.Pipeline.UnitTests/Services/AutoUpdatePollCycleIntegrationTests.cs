using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Integration tests for the auto-branch-updater poll cycle additions (spec 040, task 7.5).
/// Tests that AutoUpdatePrBranches flag correctly controls agent:done PR fetching and
/// AutoUpdatePrBranchService.ExecuteAsync invocation.
/// </summary>
public class AutoUpdatePollCycleIntegrationTests
{
    private const string RepoProviderId = "rp-auto";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (TemplatePoller Poller,
                    Mock<IRepositoryProvider> RepoProviderMock,
                    Mock<IAutoUpdatePrBranchService> AutoUpdateMock)
        CreatePoller(bool supportsUpdate = true, bool autoUpdatePrBranches = true)
    {
        var repoProviderMock = new Mock<IRepositoryProvider>();
        repoProviderMock.Setup(r => r.SupportsServerSideBranchUpdate).Returns(supportsUpdate);
        repoProviderMock.Setup(r => r.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.Done)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new List<PullRequestSummary>().AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });

        var mockFactory = new Mock<IProviderFactory>();
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, logger);
        cacheManager.RepoProviders[RepoProviderId] = repoProviderMock.Object;

        var autoUpdateMock = new Mock<IAutoUpdatePrBranchService>();
        autoUpdateMock.Setup(s => s.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var poller = new TemplatePoller(cacheManager, logger, autoUpdateMock.Object);
        return (poller, repoProviderMock, autoUpdateMock);
    }

    private static PipelineJobTemplate MakeTemplate(
        bool autoUpdatePrBranches = true,
        bool reviewEnabled = false,
        int? concurrencyLimit = null) =>
        new()
        {
            Id = "t-auto",
            Name = "AutoUpdate Template",
            IssueProviderId = "ip-1",
            RepoProviderId = RepoProviderId,
            Enabled = true,
            ReviewEnabled = reviewEnabled,
            AutoUpdatePrBranches = autoUpdatePrBranches,
            AutoUpdatePrBranchConcurrencyLimit = concurrencyLimit
        };

    private static (ConcurrentDictionary<string, ConfigStatusSnapshot> Statuses,
                    Action<int> ReportIdx,
                    Action<string> ReportStatus,
                    Action NotifyChange)
        MakeCallbacks()
    {
        var statuses = new ConcurrentDictionary<string, ConfigStatusSnapshot>();
        return (statuses, _ => { }, _ => { }, () => { });
    }

    // ── AutoUpdatePrBranches = false → agent:done fetch NOT called ────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_AutoUpdateDisabled_DoesNotFetchAgentDonePrs()
    {
        var (poller, repoProviderMock, _) = CreatePoller();
        var template = MakeTemplate(autoUpdatePrBranches: false);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        repoProviderMock.Verify(r => r.ListOpenPullRequestsAsync(
            It.IsAny<int>(), It.IsAny<int>(),
            It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.Done)),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── SupportsServerSideBranchUpdate = false → agent:done fetch NOT called ──

    [Fact]
    public async Task PollTemplateQueuesAsync_ProviderNotSupported_DoesNotFetchAgentDonePrs()
    {
        var (poller, repoProviderMock, _) = CreatePoller(supportsUpdate: false);
        var template = MakeTemplate(autoUpdatePrBranches: true);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        repoProviderMock.Verify(r => r.ListOpenPullRequestsAsync(
            It.IsAny<int>(), It.IsAny<int>(),
            It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.Done)),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── ReviewEnabled = false but AutoUpdatePrBranches = true → fetch IS called

    [Fact]
    public async Task PollTemplateQueuesAsync_ReviewDisabledButAutoUpdateEnabled_FetchesAgentDonePrs()
    {
        var (poller, repoProviderMock, _) = CreatePoller();
        // ReviewEnabled = false, AutoUpdatePrBranches = true
        var template = MakeTemplate(autoUpdatePrBranches: true, reviewEnabled: false);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        repoProviderMock.Verify(r => r.ListOpenPullRequestsAsync(
            It.IsAny<int>(), It.IsAny<int>(),
            It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.Done)),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ── agentDonePrQueues returns correct 4-tuple element ─────────────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_ReturnsAgentDonePrQueues_AsFourthTupleElement()
    {
        var (poller, _, _) = CreatePoller();
        var template = MakeTemplate(autoUpdatePrBranches: true);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        var (_, _, _, agentDonePrQueues) = await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        agentDonePrQueues.Should().ContainKey("t-auto");
    }

    // ── AutoUpdatePrBranches = false → agentDonePrQueues returns empty list ──

    [Fact]
    public async Task PollTemplateQueuesAsync_AutoUpdateDisabled_AgentDonePrQueuesIsEmpty()
    {
        var (poller, _, _) = CreatePoller();
        var template = MakeTemplate(autoUpdatePrBranches: false);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        var (_, _, _, agentDonePrQueues) = await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        agentDonePrQueues.Should().ContainKey("t-auto");
        agentDonePrQueues["t-auto"].Should().BeEmpty();
    }
}

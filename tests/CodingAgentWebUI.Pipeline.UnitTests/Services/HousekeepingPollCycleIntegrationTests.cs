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
/// Integration tests for the housekeeping poll cycle additions (spec 040, task 7.5).
///
/// Key design note: the housekeeping fetch uses NO label filter (passes null) because
/// agent:done is applied to the GitHub *issue*, not the PR. PRs are identified as
/// agent-created by their branch name prefix (PipelineConstants.BranchPrefix = "feature/auto-").
/// </summary>
public class HousekeepingPollCycleIntegrationTests
{
    private const string RepoProviderId = "rp-hk";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (TemplatePoller Poller,
                    Mock<IRepositoryProvider> RepoProviderMock,
                    Mock<IHousekeepingService> HousekeepingMock)
        CreatePoller(bool supportsUpdate = true, IReadOnlyList<PullRequestSummary>? returnedPrs = null)
    {
        var repoProviderMock = new Mock<IRepositoryProvider>();
        repoProviderMock.Setup(r => r.SupportsServerSideBranchUpdate).Returns(supportsUpdate);

        // The fetch uses null labels (fetch all open PRs), then filters client-side by branch prefix
        repoProviderMock.Setup(r => r.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),  // ← null label filter
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = (returnedPrs ?? []).ToList().AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });

        var mockFactory = new Mock<IProviderFactory>();
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, logger);
        cacheManager.RepoProviders[RepoProviderId] = repoProviderMock.Object;

        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(s => s.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var poller = new TemplatePoller(cacheManager, logger);
        return (poller, repoProviderMock, housekeepingMock);
    }

    private static PipelineJobTemplate MakeTemplate(
        bool housekeepingEnabled = true,
        bool reviewEnabled = false,
        int? concurrencyLimit = null) =>
        new()
        {
            Id = "t-hk",
            Name = "Housekeeping Template",
            IssueProviderId = "ip-1",
            RepoProviderId = RepoProviderId,
            Enabled = true,
            ReviewEnabled = reviewEnabled,
            HousekeepingEnabled = housekeepingEnabled,
            HousekeepingConcurrencyLimit = concurrencyLimit
        };

    private static PullRequestSummary MakePr(int number, string branch) => new()
    {
        Number = number,
        Identifier = number.ToString(),
        Title = $"PR #{number}",
        Description = string.Empty,
        Labels = Array.Empty<string>(),
        BranchName = branch,
        TargetBranch = "main",
        Url = $"https://example.com/pr/{number}",
        IsDraft = false
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

    // ── HousekeepingEnabled = false → fetch NOT called ────────────────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_HousekeepingDisabled_DoesNotFetchPrs()
    {
        var (poller, repoProviderMock, _) = CreatePoller();
        var template = MakeTemplate(housekeepingEnabled: false);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        // Fetch should not be called at all when housekeeping is disabled
        repoProviderMock.Verify(r => r.ListOpenPullRequestsAsync(
            It.IsAny<int>(), It.IsAny<int>(),
            It.Is<IReadOnlyList<string>?>(l => l == null),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── SupportsServerSideBranchUpdate = false → fetch NOT called ─────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_ProviderNotSupported_DoesNotFetchPrs()
    {
        var (poller, repoProviderMock, _) = CreatePoller(supportsUpdate: false);
        var template = MakeTemplate(housekeepingEnabled: true);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        repoProviderMock.Verify(r => r.ListOpenPullRequestsAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── ReviewEnabled = false, HousekeepingEnabled = true → fetch IS called ──

    [Fact]
    public async Task PollTemplateQueuesAsync_ReviewDisabledButHousekeepingEnabled_FetchesPrs()
    {
        var (poller, repoProviderMock, _) = CreatePoller();
        var template = MakeTemplate(housekeepingEnabled: true, reviewEnabled: false);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        // Fetch must be called with null labels (fetch all open PRs, filter by branch prefix client-side)
        repoProviderMock.Verify(r => r.ListOpenPullRequestsAsync(
            It.IsAny<int>(), It.IsAny<int>(),
            It.Is<IReadOnlyList<string>?>(l => l == null),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ── Branch prefix filter: only agent PRs in agentDonePrQueues ─────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_FiltersToAgentBranchPrefix()
    {
        // One agent PR, one human PR
        var agentPr = MakePr(10, $"{PipelineConstants.BranchPrefix}123-fix-login");
        var humanPr = MakePr(20, "feature/human-work");
        var (poller, _, _) = CreatePoller(returnedPrs: [agentPr, humanPr]);
        var template = MakeTemplate(housekeepingEnabled: true);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        var (_, _, _, agentDonePrQueues) = await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        agentDonePrQueues["t-hk"].Should().ContainSingle()
            .Which.BranchName.Should().Be(agentPr.BranchName,
                "only agent-created PRs (branch prefix 'feature/auto-') should be included");
    }

    // ── agentDonePrQueues is returned as 4th tuple element ────────────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_ReturnsAgentDonePrQueues_AsFourthTupleElement()
    {
        var (poller, _, _) = CreatePoller();
        var template = MakeTemplate(housekeepingEnabled: true);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        var (_, _, _, agentDonePrQueues) = await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        agentDonePrQueues.Should().ContainKey("t-hk");
    }

    // ── HousekeepingEnabled = false → agentDonePrQueues is empty ─────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_HousekeepingDisabled_AgentDonePrQueuesIsEmpty()
    {
        var (poller, _, _) = CreatePoller();
        var template = MakeTemplate(housekeepingEnabled: false);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        var (_, _, _, agentDonePrQueues) = await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        agentDonePrQueues.Should().ContainKey("t-hk");
        agentDonePrQueues["t-hk"].Should().BeEmpty();
    }
}

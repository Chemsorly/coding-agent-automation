using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

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
                Page = 1,
                PageSize = 100,
                HasMore = false
            });

        var mockFactory = new Mock<IProviderFactory>();
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, logger);
        cacheManager.RepoProviders[RepoProviderId] = repoProviderMock.Object;

        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(s => s.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<int>(),
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

        var (_, _, _, agentDonePrQueues, _) = await poller.PollTemplateQueuesAsync(
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

        var (_, _, _, agentDonePrQueues, _) = await poller.PollTemplateQueuesAsync(
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

        var (_, _, _, agentDonePrQueues, _) = await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        agentDonePrQueues.Should().ContainKey("t-hk");
        agentDonePrQueues["t-hk"].Should().BeEmpty();
    }

    // ── Polling failure → agentDonePrTruncated is set to true ────────────────

    /// <summary>
    /// When <c>ListOpenPullRequestsAsync</c> throws during <c>PollAgentDonePrQueueAsync</c>,
    /// <c>agentDonePrTruncated</c> must be set to <c>true</c> so that
    /// <see cref="StaleBranchCleaner"/> skips the cleanup cycle rather than treating the
    /// resulting empty PR list as a complete (non-truncated) authoritative set and deleting
    /// every agent branch whose issue has a terminal label.
    ///
    /// This replaces the safety guarantee previously provided by
    /// <c>FetchAllOpenAgentPrBranchesAsync</c>'s own throw path in <c>StaleBranchCleaner</c>,
    /// which skipped cleanup when the independent PR scan failed.
    /// </summary>
    [Fact]
    public async Task PollTemplateQueuesAsync_FetchPrsFails_AgentDonePrTruncatedIsTrue()
    {
        // Arrange: provider throws a transient error when listing open PRs
        var repoProviderMock = new Mock<IRepositoryProvider>();
        repoProviderMock.Setup(r => r.SupportsServerSideBranchUpdate).Returns(true);
        repoProviderMock.Setup(r => r.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("503 Service Unavailable"));

        var mockFactory = new Mock<IProviderFactory>();
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, logger);
        cacheManager.RepoProviders[RepoProviderId] = repoProviderMock.Object;

        var poller = new TemplatePoller(cacheManager, logger);
        var template = MakeTemplate(housekeepingEnabled: true);
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        // Act
        var (_, _, _, agentDonePrQueues, agentDonePrTruncated) = await poller.PollTemplateQueuesAsync(
            [template], 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        // Assert: the poll must not throw (failure is swallowed with a Warning)
        // and agentDonePrTruncated must be true so StaleBranchCleaner skips cleanup
        agentDonePrQueues["t-hk"].Should().BeEmpty(
            "no PRs were fetched because the provider threw");
        agentDonePrTruncated["t-hk"].Should().BeTrue(
            "a polling failure is equivalent to a truncated result — StaleBranchCleaner must skip " +
            "cleanup rather than treating the empty list as a complete authoritative set");
    }
}

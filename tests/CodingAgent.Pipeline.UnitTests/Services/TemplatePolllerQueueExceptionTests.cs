using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TemplatePoller.PollTemplateQueuesAsync"/> exception handler paths:
/// <see cref="RateLimitExceededException"/>, auth errors, and generic exceptions.
/// Exercises HandleRateLimitException, HandleAuthErrorExceptionAsync, and HandleGenericPollException.
/// </summary>
public class TemplatePolllerQueueExceptionTests
{
    private static TemplatePoller CreatePollerWithThrowingProvider(
        string providerId,
        Exception exceptionToThrow)
    {
        var mockFactory = new Mock<IProviderFactory>();
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, logger);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(exceptionToThrow);

        cacheManager.IssueProviders[providerId] = mockProvider.Object;
        return new TemplatePoller(cacheManager, logger);
    }

    private static PipelineJobTemplate MakeTemplate(string id, string issueProviderId) =>
        new()
        {
            Id = id,
            Name = $"Template-{id}",
            IssueProviderId = issueProviderId,
            RepoProviderId = "rp-1",
            Enabled = true,
            ImplementationEnabled = true,
            DecompositionEnabled = false, // keep it simple — only trigger issue queue
        };

    private static (
        ConcurrentDictionary<string, ConfigStatusSnapshot> Statuses,
        Action<int> ReportIndex,
        Action<string> ReportStatus,
        Action NotifyChange) MakeCallbacks()
    {
        var statuses = new ConcurrentDictionary<string, ConfigStatusSnapshot>();
        return (statuses, _ => { }, _ => { }, () => { });
    }

    // ── RateLimitExceededException ─────────────────────────────────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_WhenProviderThrowsRateLimit_ReturnsEmptyQueues()
    {
        var rateLimitEx = new RateLimitExceededException(DateTimeOffset.UtcNow.AddMinutes(1));
        var poller = CreatePollerWithThrowingProvider("ip-rl", rateLimitEx);
        var template = MakeTemplate("t1", "ip-rl");
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        var (issueQueues, prQueues, decompQueues, _) = await poller.PollTemplateQueuesAsync(
            new[] { template }, 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        // HandleRateLimitException clears queues — no exception thrown
        issueQueues.Should().ContainKey("t1");
        issueQueues["t1"].Should().BeEmpty();
    }

    [Fact]
    public async Task PollTemplateQueuesAsync_WhenProviderThrowsRateLimit_SetsStatusError()
    {
        var resetAt = DateTimeOffset.UtcNow.AddSeconds(30);
        var rateLimitEx = new RateLimitExceededException(resetAt);
        var poller = CreatePollerWithThrowingProvider("ip-rl2", rateLimitEx);
        var template = MakeTemplate("t2", "ip-rl2");
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        await poller.PollTemplateQueuesAsync(
            new[] { template }, 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        statuses.Should().ContainKey("t2");
        statuses["t2"].RateLimitResetAt.Should().NotBeNull();
        statuses["t2"].IsCurrentlyPolling.Should().BeFalse();
    }

    // ── Auth error (401/403 HttpRequestException) ─────────────────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_WhenProviderThrows401_ReturnsEmptyQueues()
    {
        var authEx = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        var poller = CreatePollerWithThrowingProvider("ip-auth", authEx);
        var template = MakeTemplate("t3", "ip-auth");
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        var (issueQueues, prQueues, decompQueues, _) = await poller.PollTemplateQueuesAsync(
            new[] { template }, 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        issueQueues.Should().ContainKey("t3");
        issueQueues["t3"].Should().BeEmpty();
    }

    [Fact]
    public async Task PollTemplateQueuesAsync_WhenProviderThrows403_SetsStatusError()
    {
        var authEx = new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden);
        var poller = CreatePollerWithThrowingProvider("ip-auth2", authEx);
        var template = MakeTemplate("t4", "ip-auth2");
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        await poller.PollTemplateQueuesAsync(
            new[] { template }, 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        statuses.Should().ContainKey("t4");
        statuses["t4"].LastError.Should().NotBeNullOrEmpty();
    }

    // ── Generic exception ─────────────────────────────────────────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_WhenProviderThrowsGenericException_ReturnsEmptyQueues()
    {
        var genericEx = new InvalidOperationException("Something went wrong");
        var poller = CreatePollerWithThrowingProvider("ip-gen", genericEx);
        var template = MakeTemplate("t5", "ip-gen");
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        var (issueQueues, prQueues, decompQueues, _) = await poller.PollTemplateQueuesAsync(
            new[] { template }, 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        issueQueues.Should().ContainKey("t5");
        issueQueues["t5"].Should().BeEmpty();
    }

    [Fact]
    public async Task PollTemplateQueuesAsync_WhenProviderThrowsGenericException_SetsStatusError()
    {
        var genericEx = new TimeoutException("Connection timed out");
        var poller = CreatePollerWithThrowingProvider("ip-gen2", genericEx);
        var template = MakeTemplate("t6", "ip-gen2");
        var (statuses, reportIdx, reportStatus, notifyChange) = MakeCallbacks();

        await poller.PollTemplateQueuesAsync(
            new[] { template }, 3, statuses, reportIdx, reportStatus, notifyChange,
            CancellationToken.None);

        statuses.Should().ContainKey("t6");
        statuses["t6"].LastError.Should().NotBeNullOrEmpty();
    }

    // ── Cancellation ───────────────────────────────────────────────────────

    [Fact]
    public async Task PollTemplateQueuesAsync_WhenCancelledMidPoll_StopsProcessing()
    {
        using var cts = new CancellationTokenSource();

        var mockFactory = new Mock<IProviderFactory>();
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, logger);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (int _, int __, IReadOnlyList<string>? ___, CancellationToken ct) =>
            {
                await Task.Delay(10, ct); // simulate async
                throw new OperationCanceledException(ct);
            });

        cacheManager.IssueProviders["ip-cancel"] = mockProvider.Object;
        var poller = new TemplatePoller(cacheManager, logger);

        var template1 = MakeTemplate("t-cancel", "ip-cancel");
        var template2 = MakeTemplate("t-not-reached", "ip-other"); // should not be processed

        var statuses = new ConcurrentDictionary<string, ConfigStatusSnapshot>();
        cts.Cancel(); // cancel immediately

        // Should not throw — cancellation is handled gracefully
        var act = () => poller.PollTemplateQueuesAsync(
            new[] { template1, template2 }, 3, statuses, _ => { }, _ => { }, () => { }, cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ── PR polling failure — key removal (fail-open for queue sweep) ───────

    /// <summary>
    /// Critical regression guard for the PR-poll-failure fail-open path.
    ///
    /// When <c>PollPrQueueAsync</c> throws a generic (transient) exception, it must
    /// <em>remove</em> the template's key from <c>prQueues</c> rather than leaving it as an
    /// empty list. <see cref="PipelineLoopService.BuildPrEligibilityMap"/> uses key-absence as
    /// the fail-open signal; an empty list is interpreted as "zero eligible PRs" and would cause
    /// the sweep to cancel every Pending Review WorkItem for the affected provider.
    ///
    /// This test invokes <c>PollTemplateQueuesAsync</c> with a repo provider that throws, and
    /// asserts that the resulting <c>prQueues</c> dictionary does NOT contain the template's key.
    /// If <c>prQueues.Remove(template.Id)</c> were accidentally removed from
    /// <c>PollPrQueueAsync</c>'s catch block, this test would fail.
    /// </summary>
    [Fact]
    public async Task PollTemplateQueuesAsync_WhenPrPollThrows_RemovesPrQueueKeyForFailOpen()
    {
        // Arrange: issue provider succeeds, repo provider throws a generic exception.
        var mockFactory = new Mock<IProviderFactory>();
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, logger);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [new IssueSummary { Identifier = "42", Title = "Open issue", Labels = [AgentLabels.Next] }],
                Page = 1, PageSize = 100, HasMore = false
            });

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider
            .Setup(p => p.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated PR poll failure"));

        cacheManager.IssueProviders["ip-1"] = mockIssueProvider.Object;
        cacheManager.RepoProviders["rp-1"] = mockRepoProvider.Object;

        var poller = new TemplatePoller(cacheManager, logger);

        // Template with ReviewEnabled = true so PollPrQueueAsync actually calls the repo provider.
        var template = new PipelineJobTemplate
        {
            Id = "t-1",
            Name = "Template-PR-Fail",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            Enabled = true,
            ImplementationEnabled = true,
            ReviewEnabled = true
        };

        var statuses = new ConcurrentDictionary<string, ConfigStatusSnapshot>();

        // Act
        var (issueQueues, prQueues, _, _) = await poller.PollTemplateQueuesAsync(
            [template], maxPagesToFetch: 3, statuses,
            reportTemplateIndex: _ => { }, reportStatus: _ => { }, notifyChange: () => { },
            CancellationToken.None);

        // Assert: issue poll succeeded so the issue queue is present
        issueQueues.Should().ContainKey("t-1",
            "issue polling succeeded — the issue queue must be populated even when PR polling fails");
        issueQueues["t-1"].Should().HaveCount(1);

        // Critical assertion: the PR queue key MUST be absent after a PR-poll exception.
        // An absent key causes BuildPrEligibilityMap to omit the provider (fail-open) rather
        // than treating the result as "zero eligible PRs" (which would incorrectly cancel all
        // Pending Review WorkItems for this provider).
        prQueues.Should().NotContainKey("t-1",
            "PollPrQueueAsync must remove the key from prQueues when the PR poll throws, so " +
            "BuildPrEligibilityMap treats the provider as not-polled-this-cycle (fail-open) " +
            "rather than as having zero eligible PRs");
    }

    /// <summary>
    /// When <c>ReviewEnabled = false</c>, <c>PollPrQueueAsync</c> sets the key to an empty list
    /// (intentional "no eligible PRs") rather than omitting it. This differs from the
    /// exception path (key removed) and is the correct signal for <see cref="PipelineLoopService.BuildPrEligibilityMap"/>
    /// to include the provider with an empty set, causing Pending Review WorkItems to be cancelled.
    /// This is the intended behaviour: if review is disabled, no PRs should be dispatched.
    /// </summary>
    [Fact]
    public async Task PollTemplateQueuesAsync_WhenReviewDisabled_SetsPrQueueKeyToEmptyList()
    {
        var mockFactory = new Mock<IProviderFactory>();
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, logger);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 100, HasMore = false });

        cacheManager.IssueProviders["ip-1"] = mockIssueProvider.Object;

        var poller = new TemplatePoller(cacheManager, logger);

        var template = new PipelineJobTemplate
        {
            Id = "t-review-off",
            Name = "NoReview",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            Enabled = true,
            ImplementationEnabled = true,
            ReviewEnabled = false  // <-- disabled
        };

        var statuses = new ConcurrentDictionary<string, ConfigStatusSnapshot>();

        var (_, prQueues, _, _) = await poller.PollTemplateQueuesAsync(
            [template], maxPagesToFetch: 3, statuses,
            reportTemplateIndex: _ => { }, reportStatus: _ => { }, notifyChange: () => { },
            CancellationToken.None);

        // Key is present but empty — distinguishable from the key-absent (PR-poll-failed) case.
        prQueues.Should().ContainKey("t-review-off",
            "ReviewEnabled=false must set the key to an empty list (intentional zero-PR state), " +
            "not omit it; this tells BuildPrEligibilityMap to include the provider with an empty set");
        prQueues["t-review-off"].Should().BeEmpty(
            "no PRs should be eligible when ReviewEnabled is false");
    }
}

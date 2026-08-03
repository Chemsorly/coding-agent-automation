using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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

        var (issueQueues, prQueues, decompQueues) = await poller.PollTemplateQueuesAsync(
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

        var (issueQueues, prQueues, decompQueues) = await poller.PollTemplateQueuesAsync(
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

        var (issueQueues, prQueues, decompQueues) = await poller.PollTemplateQueuesAsync(
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
}

using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Scheduler.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Scheduler.UnitTests;

/// <summary>
/// Unit tests for WorkItemCountsPoller — validates leader gating and error handling.
/// Uses a fast tick interval (1ms) so the service fires within the test window.
/// </summary>
public sealed class WorkItemCountsPollerTests
{
    private readonly Mock<ISchedulerApiClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    public WorkItemCountsPollerTests()
    {
        _mockClient = new Mock<ISchedulerApiClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<WorkItemCountsPoller>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    private WorkItemCountsPoller CreatePoller()
        => new WorkItemCountsPoller(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            interval: TimeSpan.FromMilliseconds(1));

    private static async Task RunPollerForDurationAsync(WorkItemCountsPoller poller, TimeSpan duration)
    {
        using var cts = new CancellationTokenSource();
        await poller.StartAsync(cts.Token);
        await Task.Delay(duration, CancellationToken.None);
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await poller.StopAsync(stopCts.Token); } catch { }
        poller.Dispose();
    }

    [Fact]
    public async Task WhenLeader_CallsGetWorkItemCountsAsync()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await RunPollerForDurationAsync(CreatePoller(), TimeSpan.FromMilliseconds(50));

        _mockClient.Verify(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(), "leader must poll work item counts");
    }

    [Fact]
    public async Task WhenNotLeader_DoesNotCallApi()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(false);

        await RunPollerForDurationAsync(CreatePoller(), TimeSpan.FromMilliseconds(50));

        _mockClient.Verify(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()),
            Times.Never(), "non-leader must not poll");
    }

    [Fact]
    public async Task WhenApiThrows_LogsWarningAndDoesNotCrash()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        await RunPollerForDurationAsync(CreatePoller(), TimeSpan.FromMilliseconds(50));

        _mockLogger.Verify(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.AtLeastOnce(), "API failure must log a warning");
        _mockLogger.Verify(l => l.Error(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never(), "API failure must not log an error");
    }

    [Fact]
    public async Task WhenNullGate_PollsUnconditionally()
    {
        // null gate = dev / single-replica mode
        var poller = new WorkItemCountsPoller(
            _mockClient.Object,
            leaderGate: null,
            _mockLogger.Object,
            interval: TimeSpan.FromMilliseconds(1));

        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await RunPollerForDurationAsync(poller, TimeSpan.FromMilliseconds(50));

        _mockClient.Verify(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(), "null gate must not suppress polling");
    }
}

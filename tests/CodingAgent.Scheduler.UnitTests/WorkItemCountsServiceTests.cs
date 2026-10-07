using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Scheduler.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for WorkItemCountsService — validates leader gating and error handling.
/// Uses a fast tick interval (1ms) so the service fires within the test window.
/// </summary>
[Collection("Metrics")]
public sealed class WorkItemCountsServiceTests
{
    private readonly Mock<ISchedulerApiClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    public WorkItemCountsServiceTests()
    {
        _mockClient = new Mock<ISchedulerApiClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<WorkItemCountsService>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    private WorkItemCountsService CreatePoller()
        => new WorkItemCountsService(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            interval: TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task WhenLeader_CallsGetWorkItemCountsAsync()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        var polled = new CallSignal();
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => polled.Hit())
            .ReturnsAsync(new WorkItemCountsResponseDto([], null));

        await BackgroundServiceRunner.RunUntilAsync(CreatePoller(), polled.Reached,
            "the leader must poll work item counts");

        _mockClient.Verify(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(), "leader must poll work item counts");
    }

    [Fact]
    public async Task WhenNotLeader_DoesNotCallApi()
    {
        var secondCheck = new CallSignal(2);
        _mockLeaderGate.SetupGet(g => g.IsLeader).Callback(() => secondCheck.Hit()).Returns(false);

        await BackgroundServiceRunner.RunUntilAsync(CreatePoller(), secondCheck.Reached,
            "the poller must keep checking leadership on every tick");

        _mockClient.Verify(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()),
            Times.Never(), "non-leader must not poll");
    }

    [Fact]
    public async Task WhenApiThrows_LogsWarningAndDoesNotCrash()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        var secondCall = new CallSignal(2);
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => secondCall.Hit())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        await BackgroundServiceRunner.RunUntilAsync(CreatePoller(), secondCall.Reached,
            "the poller must keep polling after an API failure");

        _mockLogger.Verify(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.AtLeastOnce(), "API failure must log a warning");
        _mockLogger.Verify(l => l.Error(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never(), "API failure must not log an error");
    }

    [Fact]
    public async Task WhenNullGate_PollsUnconditionally()
    {
        // null gate = dev / single-replica mode
        // Set up the mock before constructing the service so the background task never sees
        // an unconfigured mock on the first poll (which fires immediately in ExecuteAsync).
        var polled = new CallSignal();
        _mockClient
            .Setup(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => polled.Hit())
            .ReturnsAsync(new WorkItemCountsResponseDto([], null));

        var poller = new WorkItemCountsService(
            _mockClient.Object,
            leaderGate: null,
            _mockLogger.Object,
            interval: TimeSpan.FromMilliseconds(1));

        await BackgroundServiceRunner.RunUntilAsync(poller, polled.Reached,
            "null gate = dev mode — the poller must poll unconditionally");

        _mockClient.Verify(c => c.GetWorkItemCountsAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(), "null gate must not suppress polling");
    }
}

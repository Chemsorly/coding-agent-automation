using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Scheduler.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Tests for <see cref="RetentionSweepSchedulerService"/>.
/// Uses a fast tick interval (1ms) and runs the service until a mock signals the call each test
/// asserts on (see <see cref="BackgroundServiceRunner"/>).
/// </summary>
[Collection("Metrics")]
public sealed class RetentionSweepSchedulerServiceTests
{
    private readonly Mock<ISchedulerApiClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    public RetentionSweepSchedulerServiceTests()
    {
        _mockClient = new Mock<ISchedulerApiClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<RetentionSweepSchedulerService>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    private RetentionSweepSchedulerService CreateService()
        => new RetentionSweepSchedulerService(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            interval: TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task WhenLeaderAndApiReturns200_ShouldCallApiAndNotLogError()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        // The second sweep call proves the first tick, including its logging, ran to completion.
        var secondSweep = new CallSignal(2);
        _mockClient.Setup(c => c.TriggerRetentionSweepAsync(It.IsAny<CancellationToken>()))
            .Callback(() => secondSweep.Hit())
            .ReturnsAsync(new RetentionSweepResultDto(5, 3, 2, 4));

        await BackgroundServiceRunner.RunUntilAsync(CreateService(), secondSweep.Reached,
            "the leader must trigger the retention sweep on every tick");

        _mockClient.Verify(c => c.TriggerRetentionSweepAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(), "leader should trigger the retention sweep");

        _mockLogger.Verify(l => l.Error(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never(), "successful sweep must not log Error");
        // TODO [WARNING]: This test does not assert that the log message contains the correct field values from
        // RetentionSweepResultDto. If the log format string in RetentionSweepSchedulerService were accidentally
        // left referencing a removed named property (e.g. staleConsolidation=), a FormatException or missing
        // structured-log property would go undetected here. Consider asserting on the Information log message
        // content (e.g. verify _mockLogger.Information was called with a string containing "retentionRuns=").
    }

    [Fact]
    public async Task WhenLeaderAndNetworkError_ShouldLogWarningAndNotThrow()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        // The second sweep call proves the first failed tick, including its Warning, ran to completion.
        var secondSweep = new CallSignal(2);
        _mockClient.Setup(c => c.TriggerRetentionSweepAsync(It.IsAny<CancellationToken>()))
            .Callback(() => secondSweep.Hit())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        await BackgroundServiceRunner.RunUntilAsync(CreateService(), secondSweep.Reached,
            "a network error must not stop the sweep loop");

        _mockLogger.Verify(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.AtLeastOnce(), "network error should log Warning");
        _mockLogger.Verify(l => l.Error(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never(), "network error should not log Error");
    }

    [Fact]
    public async Task WhenNotLeader_ShouldNotCallApi()
    {
        // The second leader check proves the first skipped tick ran to completion.
        var secondCheck = new CallSignal(2);
        _mockLeaderGate.SetupGet(g => g.IsLeader).Callback(() => secondCheck.Hit()).Returns(false);

        await BackgroundServiceRunner.RunUntilAsync(CreateService(), secondCheck.Reached,
            "the service must keep checking leadership on every tick");

        _mockClient.Verify(c => c.TriggerRetentionSweepAsync(It.IsAny<CancellationToken>()),
            Times.Never(), "non-leader should not trigger the sweep");
    }
}

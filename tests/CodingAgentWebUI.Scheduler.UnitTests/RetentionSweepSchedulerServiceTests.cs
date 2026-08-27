using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Scheduler.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Scheduler.UnitTests;

/// <summary>
/// Tests for <see cref="RetentionSweepSchedulerService"/>.
/// Uses a fast tick interval (1ms) so the service fires within the test window.
/// </summary>
[Collection("SchedulerTiming")]
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

    private static async Task RunServiceForDurationAsync(RetentionSweepSchedulerService svc, TimeSpan duration)
    {
        using var hostCts = new CancellationTokenSource();
        await svc.StartAsync(hostCts.Token);
        await Task.Delay(duration, CancellationToken.None);
        // Stop with a timeout — BackgroundService.StopAsync can hang if ExecuteAsync doesn't respond
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await svc.StopAsync(stopCts.Token); } catch { /* timeout or cancellation — ignore */ }
        svc.Dispose();
    }

    [Fact]
    public async Task WhenLeaderAndApiReturns200_ShouldCallApiAndNotLogError()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient.Setup(c => c.TriggerRetentionSweepAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetentionSweepResultDto(5, 3, 1, 2, 4));

        // Use 500ms window (up from 50ms) so PeriodicTimer(1ms) reliably fires
        // at least once even on a loaded CI host where thread-pool scheduling is delayed.
        await RunServiceForDurationAsync(CreateService(), TimeSpan.FromMilliseconds(500));

        _mockClient.Verify(c => c.TriggerRetentionSweepAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(), "leader should trigger the retention sweep");

        _mockLogger.Verify(l => l.Error(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never(), "successful sweep must not log Error");
    }

    [Fact]
    public async Task WhenLeaderAndNetworkError_ShouldLogWarningAndNotThrow()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient.Setup(c => c.TriggerRetentionSweepAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        await RunServiceForDurationAsync(CreateService(), TimeSpan.FromMilliseconds(500));

        _mockLogger.Verify(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.AtLeastOnce(), "network error should log Warning");
        _mockLogger.Verify(l => l.Error(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never(), "network error should not log Error");
    }

    [Fact]
    public async Task WhenNotLeader_ShouldNotCallApi()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(false);

        await RunServiceForDurationAsync(CreateService(), TimeSpan.FromMilliseconds(500));

        _mockClient.Verify(c => c.TriggerRetentionSweepAsync(It.IsAny<CancellationToken>()),
            Times.Never(), "non-leader should not trigger the sweep");
    }
}

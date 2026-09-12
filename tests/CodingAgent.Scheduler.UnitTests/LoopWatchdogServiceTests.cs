using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for <see cref="LoopWatchdogService"/> — dormant-but-leader self-heal.
///
/// Tests use a 1ms interval so the PeriodicTimer fires immediately without real-time waiting.
/// The <see cref="TimingTestCollection"/> serializes all timing-sensitive tests to avoid
/// thread-pool starvation on loaded CI hosts.
/// </summary>
[Collection("SchedulerTiming")]
public sealed class LoopWatchdogServiceTests
{
    private readonly Mock<IPipelineLoopService> _mockLoopService;
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    public LoopWatchdogServiceTests()
    {
        _mockLoopService = new Mock<IPipelineLoopService>();
        _mockConfigClient = new Mock<IPipelineApiConfigClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();

        _mockLogger.Setup(l => l.ForContext<LoopWatchdogService>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);

        // Default: auto-start enabled
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { ClosedLoopAutoStart = true });
    }

    private LoopWatchdogService CreateWatchdog(ILeaderGate? leaderGate)
        => new LoopWatchdogService(
            _mockLoopService.Object,
            _mockConfigClient.Object,
            leaderGate,
            _mockLogger.Object,
            interval: TimeSpan.FromMilliseconds(1));

    private static async Task RunWatchdogForDurationAsync(LoopWatchdogService watchdog, TimeSpan duration)
    {
        using var cts = new CancellationTokenSource();
        await watchdog.StartAsync(cts.Token);
        await Task.Delay(duration, CancellationToken.None);
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await watchdog.StopAsync(stopCts.Token); } catch { }
        watchdog.Dispose();
    }

    /// <summary>
    /// When all three heal conditions are met (leader, loop dormant, auto-start true),
    /// the watchdog must call StartLoopAsync().
    /// </summary>
    [Fact]
    public async Task WhenLeaderAndLoopDormantAndAutoStartTrue_WatchdogCallsStartLoopAsync()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(false);
        _mockLoopService.Setup(s => s.StartLoopAsync()).ReturnsAsync(true);

        // TODO: the mock always returns IsLoopActive=false, so the watchdog fires on every 1ms tick
        //   for the full 500ms duration and StartLoopAsync is called hundreds of times. In production,
        //   a successful StartLoopAsync would flip IsLoopActive to true, causing the watchdog to back
        //   off. The current setup does not verify that the watchdog stops calling StartLoopAsync after
        //   a successful heal — a call-storm on StartLoopAsync would not be caught by this test.
        //   Consider sequencing the mock to return IsLoopActive=true after the first StartLoopAsync
        //   call to verify the back-off behaviour.
        await RunWatchdogForDurationAsync(CreateWatchdog(_mockLeaderGate.Object), TimeSpan.FromMilliseconds(500));

        _mockLoopService.Verify(s => s.StartLoopAsync(),
            Times.AtLeastOnce(),
            "watchdog must call StartLoopAsync when leader + dormant + auto-start enabled");
    }

    /// <summary>
    /// When this pod is NOT the leader, the watchdog must not restart the loop.
    /// Another pod holds leadership and will self-heal its own loop.
    /// </summary>
    [Fact]
    public async Task WhenNotLeader_WatchdogDoesNotCallStartLoopAsync()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(false);
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(false);

        await RunWatchdogForDurationAsync(CreateWatchdog(_mockLeaderGate.Object), TimeSpan.FromMilliseconds(500));

        _mockLoopService.Verify(s => s.StartLoopAsync(),
            Times.Never(),
            "non-leader must not restart the loop — another pod holds leadership");

        // Positive check: the leader check was evaluated (not a vacuous pass)
        _mockLeaderGate.VerifyGet(g => g.IsLeader, Times.AtLeastOnce());
    }

    /// <summary>
    /// When the loop is already active, the watchdog must not call StartLoopAsync.
    /// </summary>
    [Fact]
    public async Task WhenLoopAlreadyActive_WatchdogDoesNotCallStartLoopAsync()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(true);

        await RunWatchdogForDurationAsync(CreateWatchdog(_mockLeaderGate.Object), TimeSpan.FromMilliseconds(500));

        _mockLoopService.Verify(s => s.StartLoopAsync(),
            Times.Never(),
            "loop is already active — watchdog must not call StartLoopAsync");

        // Positive check: IsLoopActive was evaluated
        _mockLoopService.VerifyGet(s => s.IsLoopActive, Times.AtLeastOnce());
    }

    /// <summary>
    /// When ClosedLoopAutoStart=false (operator deliberately stopped the loop),
    /// the watchdog must not resurrect it.
    /// </summary>
    [Fact]
    public async Task WhenAutoStartFalse_WatchdogDoesNotCallStartLoopAsync()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(false);
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { ClosedLoopAutoStart = false });

        await RunWatchdogForDurationAsync(CreateWatchdog(_mockLeaderGate.Object), TimeSpan.FromMilliseconds(500));

        _mockLoopService.Verify(s => s.StartLoopAsync(),
            Times.Never(),
            "ClosedLoopAutoStart=false means operator deliberately stopped the loop — must not be healed");
    }

    /// <summary>
    /// When ILeaderGate is null (dev/single-replica mode with no K8s leader election),
    /// the watchdog treats this instance as the leader and heals unconditionally when
    /// the loop is dormant and auto-start is enabled.
    /// Mirrors the WorkItemCountsPoller.WhenNullGate_PollsUnconditionally pattern.
    /// </summary>
    [Fact]
    public async Task WhenNullLeaderGate_WatchdogHealsUnconditionally()
    {
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(false);
        _mockLoopService.Setup(s => s.StartLoopAsync()).ReturnsAsync(true);

        // Pass null leader gate — simulates dev mode / single-replica deployment
        await RunWatchdogForDurationAsync(CreateWatchdog(leaderGate: null), TimeSpan.FromMilliseconds(500));

        _mockLoopService.Verify(s => s.StartLoopAsync(),
            Times.AtLeastOnce(),
            "null gate = dev mode = treat as leader — watchdog must call StartLoopAsync");
    }

    /// <summary>
    /// When StartLoopAsync returns false (e.g. no valid templates), the watchdog should
    /// log a warning but not throw or crash.
    /// </summary>
    [Fact]
    public async Task WhenStartLoopAsyncReturnsFalse_WatchdogLogsWarningDoesNotCrash()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(false);
        _mockLoopService.Setup(s => s.StartLoopAsync()).ReturnsAsync(false);

        // Must not throw even when StartLoopAsync returns false
        var act = async () =>
            await RunWatchdogForDurationAsync(CreateWatchdog(_mockLeaderGate.Object), TimeSpan.FromMilliseconds(500));

        await act.Should().NotThrowAsync(
            "StartLoopAsync returning false must not crash the watchdog");

        _mockLoopService.Verify(s => s.StartLoopAsync(),
            Times.AtLeastOnce(),
            "StartLoopAsync must still be called — the watchdog attempted a heal");

        // TODO: the test name claims "LogsWarning" but no assertion verifies a Warning-level log
        //   was emitted when StartLoopAsync returns false. Without a log assertion, this test would
        //   pass even if the warning branch were deleted entirely. Add a log assertion via a real
        //   in-memory Serilog sink or a mock Warning verification to confirm the "no valid templates"
        //   scenario is correctly diagnosed.
    }

    /// <summary>
    /// When the config client throws (transient API error), the watchdog must
    /// log a warning and continue running — not crash.
    /// </summary>
    [Fact]
    public async Task WhenConfigClientThrows_WatchdogContinuesRunning()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(false);
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        var act = async () =>
            await RunWatchdogForDurationAsync(CreateWatchdog(_mockLeaderGate.Object), TimeSpan.FromMilliseconds(500));

        await act.Should().NotThrowAsync(
            "a transient config-fetch failure must not crash the watchdog");

        // StartLoopAsync must NOT be called because config fetch failed
        _mockLoopService.Verify(s => s.StartLoopAsync(),
            Times.Never(),
            "StartLoopAsync must not be called when config cannot be fetched");
    }
}

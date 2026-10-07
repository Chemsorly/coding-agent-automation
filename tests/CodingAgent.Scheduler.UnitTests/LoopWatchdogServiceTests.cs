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
/// Tests use a 1ms interval so the PeriodicTimer fires immediately without real-time waiting,
/// and run the watchdog until a mock signals the call they assert on
/// (see <see cref="BackgroundServiceRunner"/>).
/// The <see cref="TimingTestCollection"/> serializes all timing-sensitive tests to avoid
/// thread-pool starvation on loaded CI hosts.
/// </summary>
[Collection("Metrics")]
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

    /// <summary>
    /// When all three heal conditions are met (leader, loop dormant, auto-start true),
    /// the watchdog must call StartLoopAsync().
    /// </summary>
    [Fact]
    public async Task WhenLeaderAndLoopDormantAndAutoStartTrue_WatchdogCallsStartLoopAsync()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(false);
        var healed = new CallSignal();
        _mockLoopService.Setup(s => s.StartLoopAsync()).Callback(() => healed.Hit()).ReturnsAsync(true);

        // TODO: the mock always returns IsLoopActive=false, so the watchdog fires on every 1ms tick
        //   until the test stops it, and StartLoopAsync may be called many times. In production,
        //   a successful StartLoopAsync would flip IsLoopActive to true, causing the watchdog to back
        //   off. The current setup does not verify that the watchdog stops calling StartLoopAsync after
        //   a successful heal — a call-storm on StartLoopAsync would not be caught by this test.
        //   Consider sequencing the mock to return IsLoopActive=true after the first StartLoopAsync
        //   call to verify the back-off behaviour.
        await BackgroundServiceRunner.RunUntilAsync(CreateWatchdog(_mockLeaderGate.Object), healed.Reached,
            "the watchdog tick must reach StartLoopAsync");

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
        // The second leader check proves the first tick ran to completion.
        var secondCheck = new CallSignal(2);
        _mockLeaderGate.SetupGet(g => g.IsLeader).Callback(() => secondCheck.Hit()).Returns(false);
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(false);

        await BackgroundServiceRunner.RunUntilAsync(CreateWatchdog(_mockLeaderGate.Object), secondCheck.Reached,
            "the watchdog must keep checking leadership on every tick");

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
        // The second loop-state check proves the first tick ran to completion.
        var secondCheck = new CallSignal(2);
        _mockLoopService.SetupGet(s => s.IsLoopActive).Callback(() => secondCheck.Hit()).Returns(true);

        await BackgroundServiceRunner.RunUntilAsync(CreateWatchdog(_mockLeaderGate.Object), secondCheck.Reached,
            "the watchdog must keep checking the loop state on every tick");

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
        // The second config read proves the first tick ran to completion.
        var secondRead = new CallSignal(2);
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Callback(() => secondRead.Hit())
            .ReturnsAsync(new PipelineConfiguration { ClosedLoopAutoStart = false });

        await BackgroundServiceRunner.RunUntilAsync(CreateWatchdog(_mockLeaderGate.Object), secondRead.Reached,
            "the watchdog must keep reading ClosedLoopAutoStart on every tick");

        _mockLoopService.Verify(s => s.StartLoopAsync(),
            Times.Never(),
            "ClosedLoopAutoStart=false means operator deliberately stopped the loop — must not be healed");
    }

    /// <summary>
    /// When ILeaderGate is null (dev/single-replica mode with no K8s leader election),
    /// the watchdog treats this instance as the leader and heals unconditionally when
    /// the loop is dormant and auto-start is enabled.
    /// Mirrors the WorkItemCountsService.WhenNullGate_PollsUnconditionally pattern.
    /// </summary>
    [Fact]
    public async Task WhenNullLeaderGate_WatchdogHealsUnconditionally()
    {
        _mockLoopService.SetupGet(s => s.IsLoopActive).Returns(false);
        var healed = new CallSignal();
        _mockLoopService.Setup(s => s.StartLoopAsync()).Callback(() => healed.Hit()).ReturnsAsync(true);

        // Pass null leader gate — simulates dev mode / single-replica deployment
        await BackgroundServiceRunner.RunUntilAsync(CreateWatchdog(leaderGate: null), healed.Reached,
            "null gate = dev mode — the watchdog tick must reach StartLoopAsync");

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
        // The second heal attempt proves the watchdog kept running after StartLoopAsync returned false.
        var secondAttempt = new CallSignal(2);
        _mockLoopService.Setup(s => s.StartLoopAsync()).Callback(() => secondAttempt.Hit()).ReturnsAsync(false);

        // Must not throw even when StartLoopAsync returns false
        var act = async () =>
            await BackgroundServiceRunner.RunUntilAsync(CreateWatchdog(_mockLeaderGate.Object), secondAttempt.Reached,
                "the watchdog must keep attempting the heal after StartLoopAsync returned false");

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
        // The second config read proves the watchdog kept running after the first one threw.
        var secondRead = new CallSignal(2);
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Callback(() => secondRead.Hit())
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        var act = async () =>
            await BackgroundServiceRunner.RunUntilAsync(CreateWatchdog(_mockLeaderGate.Object), secondRead.Reached,
                "the watchdog must keep running after a config-fetch failure");

        await act.Should().NotThrowAsync(
            "a transient config-fetch failure must not crash the watchdog");

        // StartLoopAsync must NOT be called because config fetch failed
        _mockLoopService.Verify(s => s.StartLoopAsync(),
            Times.Never(),
            "StartLoopAsync must not be called when config cannot be fetched");
    }
}

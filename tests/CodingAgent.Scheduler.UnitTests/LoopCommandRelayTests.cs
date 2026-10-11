using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using CodingAgent.Web.TestUtilities;
using Moq;
using System.Text.Json;
using Xunit;
using ILeaderGate = CodingAgent.Pipeline.Interfaces.ILeaderGate;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Integration-style unit tests for the relay round-trip:
/// non-leader endpoint writes to Redis, leader handler reads and executes,
/// relay poll reads the result.
/// Uses a shared <see cref="CodingAgent.Web.TestUtilities.FakeRedisStore"/> so the relay and handler
/// operate on the same in-memory state without a real Redis server.
/// </summary>
[Collection("Metrics")]
public sealed class LoopCommandRelayTests
{
    private readonly FakeRedisStore _store = new();
    private readonly Mock<IPipelineLoopService> _leaderLoopService = new();
    private readonly Mock<IPipelineLoopService> _nonLeaderLoopService = new();
    private readonly Mock<IPipelineApiConfigClient> _configClient = new();
    private readonly Mock<ILeaderGate> _leaderGate = new();
    private readonly Mock<ILogger> _logger = new();

    private readonly LoopCommandExecutor _executor = new();

    public LoopCommandRelayTests()
    {
        _logger.Setup(l => l.ForContext<LoopCommandHandlerService>()).Returns(_logger.Object);
        _logger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_logger.Object);

        _leaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _leaderLoopService.Setup(l => l.ValidationErrors).Returns([]);
        _leaderLoopService.Setup(l => l.IsLoopActive).Returns(false);
        _nonLeaderLoopService.Setup(l => l.ValidationErrors).Returns([]);
        _configClient
            .Setup(c => c.UpdatePipelineConfigAsync(
                It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private LoopCommandHandlerService CreateLeaderHandler()
        => new LoopCommandHandlerService(
            _executor,
            _leaderLoopService.Object,
            _configClient.Object,
            _store,
            _leaderGate.Object,
            _logger.Object,
            interval: TimeSpan.FromMilliseconds(1));

    /// <summary>
    /// Creates a relay with a short timeout so tests don't wait 10 s for timeout scenarios.
    /// </summary>
    private LoopCommandRelay CreateRelay(TimeSpan? timeout = null)
        => new LoopCommandRelay(_store, timeout: timeout, pollInterval: TimeSpan.FromMilliseconds(10));

    // ── Test 5: Resume reaches the leader via relay ─────────────────────────

    [Fact]
    public async Task Resume_ReachesLeader_ViaRelay()
    {
        var resumeCalled = new CallSignal();
        _leaderLoopService.Setup(l => l.ResumeLoop()).Callback(() => resumeCalled.Hit());

        var handler = CreateLeaderHandler();
        await handler.StartAsync(CancellationToken.None);
        try
        {
            var relay = CreateRelay();
            var result = await relay.SendAsync(LoopCommand.Resume, CancellationToken.None);

            result.Success.Should().BeTrue("leader must handle the Resume command");
            _leaderLoopService.Verify(l => l.ResumeLoop(), Times.Once,
                "leader's ResumeLoop must be called exactly once via relay");
            _nonLeaderLoopService.Verify(l => l.ResumeLoop(), Times.Never,
                "non-leader's ResumeLoop must never be called");
        }
        finally
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await handler.StopAsync(cts.Token);
            handler.Dispose();
        }
    }

    // ── Test 6: Start reaches the leader, non-leader's StartLoopAsync not called ──

    [Fact]
    public async Task Start_ReachesLeader_NonLeaderStartNotCalled()
    {
        _leaderLoopService.Setup(l => l.StartLoopAsync()).ReturnsAsync(true);

        var handler = CreateLeaderHandler();
        await handler.StartAsync(CancellationToken.None);
        try
        {
            var relay = CreateRelay();
            var result = await relay.SendAsync(LoopCommand.Start, CancellationToken.None);

            result.Success.Should().BeTrue();
            result.StartResult.Should().NotBeNull();
            result.StartResult!.Started.Should().BeTrue("leader's StartLoopAsync returned true");

            _leaderLoopService.Verify(l => l.StartLoopAsync(), Times.Once,
                "leader's StartLoopAsync must be called via relay");
            _nonLeaderLoopService.Verify(l => l.StartLoopAsync(), Times.Never,
                "non-leader's StartLoopAsync must never be called");
        }
        finally
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await handler.StopAsync(cts.Token);
            handler.Dispose();
        }
    }

    // ── Test 7: Timeout — start returns Started=false ───────────────────────

    [Fact]
    public async Task Start_Timeout_ReturnsStartedFalse()
    {
        // No handler running — relay will time out.
        var relay = CreateRelay(timeout: TimeSpan.FromMilliseconds(150));
        var result = await relay.SendAsync(LoopCommand.Start, CancellationToken.None);

        result.Success.Should().BeFalse("no leader is handling commands — must time out");
        result.StartResult.Should().BeNull();
        result.Error.Should().Contain("10 s",
            "timeout error must mention the 10 s threshold");
    }

    // ── Test 8: Timeout — resume returns failure ─────────────────────────────

    [Fact]
    public async Task Resume_Timeout_ReturnsFailure()
    {
        var relay = CreateRelay(timeout: TimeSpan.FromMilliseconds(150));
        var result = await relay.SendAsync(LoopCommand.Resume, CancellationToken.None);

        result.Success.Should().BeFalse("no leader is handling commands — must time out");
        result.Error.Should().NotBeNullOrEmpty("a timeout error message must be set");
    }

    // ── Test: Timeout — stop returns failure ─────────────────────────────────
    // TODO [WARNING]: This test was missing — Start and Resume timeouts were tested but not Stop.
    // The Stop timeout code path in the relay is exercised here (see Test Quality review).

    [Fact]
    public async Task Stop_Timeout_ReturnsFailure()
    {
        // No handler running — relay will time out.
        var relay = CreateRelay(timeout: TimeSpan.FromMilliseconds(150));
        var result = await relay.SendAsync(LoopCommand.Stop, CancellationToken.None);

        result.Success.Should().BeFalse("no leader is handling commands — must time out");
        result.Error.Should().NotBeNullOrEmpty("a timeout error message must be set");
    }

    // ── Test: Stop command relayed to leader, leader calls StopLoopOnly ──────

    [Fact]
    public async Task Stop_ReachesLeader_CallsStopLoopOnly()
    {
        var handler = CreateLeaderHandler();
        await handler.StartAsync(CancellationToken.None);
        try
        {
            var relay = CreateRelay();
            var result = await relay.SendAsync(LoopCommand.Stop, CancellationToken.None);

            result.Success.Should().BeTrue();
            _leaderLoopService.Verify(l => l.StopLoop(), Times.Once,
                "leader's StopLoop must be called exactly once via relay");
            // Config persistence must NOT be triggered by the relay (it happens on the endpoint side).
            _configClient.Verify(c => c.UpdatePipelineConfigAsync(
                It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
                It.IsAny<CancellationToken>()), Times.Never,
                "relay must not trigger config persistence (that is the non-leader endpoint's responsibility)");
        }
        finally
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await handler.StopAsync(cts.Token);
            handler.Dispose();
        }
    }

    // ── Test: Same command id only handled once across relay ─────────────────

    [Fact]
    public async Task RelaySendsCommand_SameIdHandledOnce()
    {
        var callCount = 0;
        _leaderLoopService.Setup(l => l.ResumeLoop()).Callback(() => callCount++);

        // Manually seed the same id twice to test idempotency.
        var msg = new LoopCommandMessage("fixed-id", LoopCommand.Resume);
        var json = JsonSerializer.Serialize(msg, PipelineJsonOptions.Default);
        await _store.SetAsync(LoopCommandRelay.CommandKey, json, TimeSpan.FromSeconds(60));

        var handler = CreateLeaderHandler();
        var signal = new CallSignal();
        _leaderLoopService.Setup(l => l.ResumeLoop()).Callback(() => { callCount++; signal.Hit(); });

        await BackgroundServiceRunner.RunUntilAsync(handler, signal.Reached, "handler must process the command");

        // Seed again — idempotency guard must prevent a second execution.
        await _store.SetAsync(LoopCommandRelay.CommandKey, json, TimeSpan.FromSeconds(60));

        // TODO [WARNING]: The synchronisation below is racy. twoTicks is declared but never awaited —
        // it was the correct primitive but was replaced by an unconditional Task.Delay(50). If the
        // handler's 1 ms interval does not fire within 50 ms on a loaded CI host, the idempotency
        // guard is never exercised but callCount.Should().Be(1) still passes trivially. Replace the
        // Task.Delay with a BackgroundServiceRunner.RunUntilAsync on a twoTicks CallSignal hooked
        // to the leader gate or the store's GetAsync, so the assertion only runs after a confirmed
        // second tick (see Test Quality review).
        var twoTicks = new CallSignal(2);
        // We can't easily hook the handler's internal IsLeader check after the fact;
        // just wait a moment for the handler to have ticked again by sleeping briefly.
        await Task.Delay(50);

        callCount.Should().Be(1, "the same command id must be handled exactly once");
    }
}

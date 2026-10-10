using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Redis;
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
/// Unit tests for <see cref="LoopCommandHandlerService"/>.
/// Uses <c>interval: TimeSpan.FromMilliseconds(1)</c> and
/// <see cref="BackgroundServiceRunner.RunUntilAsync"/> + <see cref="CallSignal"/>
/// — the same pattern as <see cref="WorkItemCountsServiceTests"/>.
/// </summary>
[Collection("Metrics")]
public sealed class LoopCommandHandlerServiceTests
{
    private readonly Mock<ILoopCommandExecutor> _executor = new();
    private readonly Mock<IPipelineLoopService> _loopService = new();
    private readonly Mock<IPipelineApiConfigClient> _configClient = new();
    private readonly Mock<ILeaderGate> _leaderGate = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly FakeRedisStore _store = new();

    public LoopCommandHandlerServiceTests()
    {
        _logger.Setup(l => l.ForContext<LoopCommandHandlerService>()).Returns(_logger.Object);
        _logger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_logger.Object);

        // Default executor stubs so tests only need to override the method under test.
        _executor.Setup(e => e.ExecuteResumeAsync(It.IsAny<IPipelineLoopService>()))
            .Returns(Task.CompletedTask);
        _executor.Setup(e => e.ExecuteStopLoopOnlyAsync(It.IsAny<IPipelineLoopService>()))
            .Returns(Task.CompletedTask);
        _executor.Setup(e => e.ExecuteStartAsync(
                It.IsAny<IPipelineLoopService>(),
                It.IsAny<IPipelineApiConfigClient>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoopStartResultDto(true, null));
    }

    private LoopCommandHandlerService CreateHandler(ILeaderGate? leaderGate = null)
        => new LoopCommandHandlerService(
            _executor.Object,
            _loopService.Object,
            _configClient.Object,
            _store,
            leaderGate,
            _logger.Object,
            interval: TimeSpan.FromMilliseconds(1));

    private void SeedCommand(string id, LoopCommand command)
    {
        var msg = new LoopCommandMessage(id, command);
        var json = JsonSerializer.Serialize(msg, PipelineJsonOptions.Default);
        _store.SetAsync(LoopCommandRelay.CommandKey, json, TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    }

    // ── Test 1 (TDD anchor): Leader handles pending Resume command ──────────

    [Fact]
    public async Task Leader_HandlesPendingResumeCommand()
    {
        _leaderGate.SetupGet(g => g.IsLeader).Returns(true);
        SeedCommand("cmd-resume-1", LoopCommand.Resume);

        var signal = new CallSignal();
        _executor.Setup(e => e.ExecuteResumeAsync(It.IsAny<IPipelineLoopService>()))
            .Callback(() => signal.Hit())
            .Returns(Task.CompletedTask);

        await BackgroundServiceRunner.RunUntilAsync(
            CreateHandler(_leaderGate.Object), signal.Reached,
            "leader must execute the Resume command written to Redis");

        _executor.Verify(e => e.ExecuteResumeAsync(_loopService.Object), Times.Once,
            "leader must call ExecuteResumeAsync exactly once");

        // Result key must be written.
        var resultKey = LoopCommandRelay.ResultKeyPrefix + "cmd-resume-1";
        var resultJson = await _store.GetAsync(resultKey);
        resultJson.Should().NotBeNull("leader must write the result to Redis");
    }

    // ── Test 2: Non-leader does nothing ─────────────────────────────────────

    [Fact]
    public async Task NonLeader_DoesNothing()
    {
        var secondCheck = new CallSignal(2);
        _leaderGate.SetupGet(g => g.IsLeader).Callback(() => secondCheck.Hit()).Returns(false);
        SeedCommand("cmd-1", LoopCommand.Resume);

        await BackgroundServiceRunner.RunUntilAsync(
            CreateHandler(_leaderGate.Object), secondCheck.Reached,
            "non-leader must keep checking leadership on every tick");

        _executor.Verify(e => e.ExecuteResumeAsync(It.IsAny<IPipelineLoopService>()), Times.Never,
            "non-leader must not execute any command");
        _executor.Verify(e => e.ExecuteStartAsync(
            It.IsAny<IPipelineLoopService>(),
            It.IsAny<IPipelineApiConfigClient>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _executor.Verify(e => e.ExecuteStopLoopOnlyAsync(It.IsAny<IPipelineLoopService>()), Times.Never);
    }

    // ── Test 3: Same id not handled twice (idempotency guard) ───────────────

    [Fact]
    public async Task SameCommandId_HandledOnce()
    {
        _leaderGate.SetupGet(g => g.IsLeader).Returns(true);
        SeedCommand("cmd-idempotent", LoopCommand.Resume);

        var callCount = 0;
        // TODO [WARNING]: secondTick is dead code — the executor callback can only fire once due to
        // the idempotency guard, so secondTick can never reach count 2 and is never awaited. It
        // misleads readers into thinking the executor is expected to be called twice. The actual
        // termination mechanism is twoTicks (a CallSignal on the leader gate check), which is
        // correct. Remove secondTick once this is cleaned up (see Test Quality review).
        var secondTick = new CallSignal(2);
        _executor.Setup(e => e.ExecuteResumeAsync(It.IsAny<IPipelineLoopService>()))
            .Callback(() =>
            {
                callCount++;
                secondTick.Hit(); // hit twice (once per poll), but executor only once
            })
            .Returns(Task.CompletedTask);

        // Use a separate signal that fires after two handler ticks to confirm idempotency.
        var twoTicks = new CallSignal(2);
        _leaderGate.SetupGet(g => g.IsLeader).Callback(() => twoTicks.Hit()).Returns(true);

        await BackgroundServiceRunner.RunUntilAsync(
            CreateHandler(_leaderGate.Object), twoTicks.Reached,
            "handler must tick at least twice so idempotency can be observed");

        _executor.Verify(e => e.ExecuteResumeAsync(It.IsAny<IPipelineLoopService>()), Times.Once,
            "executor must be called exactly once for the same command id");
    }

    // ── Test 4: Null leader gate handles command (single-replica path) ───────

    [Fact]
    public async Task NullLeaderGate_HandlesCommand()
    {
        SeedCommand("cmd-null-gate", LoopCommand.Resume);

        var signal = new CallSignal();
        _executor.Setup(e => e.ExecuteResumeAsync(It.IsAny<IPipelineLoopService>()))
            .Callback(() => signal.Hit())
            .Returns(Task.CompletedTask);

        await BackgroundServiceRunner.RunUntilAsync(
            CreateHandler(leaderGate: null), signal.Reached,
            "null leader gate (single-replica) must handle the command unconditionally");

        _executor.Verify(e => e.ExecuteResumeAsync(_loopService.Object), Times.Once);
    }

    // ── Test 5: Leader handles Start command and returns result ─────────────

    [Fact]
    public async Task Leader_HandlesStartCommand_WritesResult()
    {
        _leaderGate.SetupGet(g => g.IsLeader).Returns(true);
        SeedCommand("cmd-start", LoopCommand.Start);

        var signal = new CallSignal();
        _executor.Setup(e => e.ExecuteStartAsync(
                It.IsAny<IPipelineLoopService>(),
                It.IsAny<IPipelineApiConfigClient>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => signal.Hit())
            .ReturnsAsync(new LoopStartResultDto(true, null));

        await BackgroundServiceRunner.RunUntilAsync(
            CreateHandler(_leaderGate.Object), signal.Reached,
            "leader must execute Start and write the result");

        var resultJson = await _store.GetAsync(LoopCommandRelay.ResultKeyPrefix + "cmd-start");
        resultJson.Should().NotBeNull();
        var result = JsonSerializer.Deserialize<LoopCommandResultMessage>(resultJson!, PipelineJsonOptions.Lenient);
        result!.Success.Should().BeTrue();
        result.StartResult!.Started.Should().BeTrue();
    }

    // ── Test 6: Leader handles Stop command ─────────────────────────────────

    [Fact]
    public async Task Leader_HandlesStopCommand()
    {
        _leaderGate.SetupGet(g => g.IsLeader).Returns(true);
        SeedCommand("cmd-stop", LoopCommand.Stop);

        var signal = new CallSignal();
        _executor.Setup(e => e.ExecuteStopLoopOnlyAsync(It.IsAny<IPipelineLoopService>()))
            .Callback(() => signal.Hit())
            .Returns(Task.CompletedTask);

        await BackgroundServiceRunner.RunUntilAsync(
            CreateHandler(_leaderGate.Object), signal.Reached,
            "leader must execute StopLoopOnly on Stop command");

        _executor.Verify(e => e.ExecuteStopLoopOnlyAsync(_loopService.Object), Times.Once);
        // Config persistence must NOT be called by the handler — it is the endpoint's responsibility.
        _executor.Verify(e => e.ExecuteStopPersistAsync(
            It.IsAny<IPipelineApiConfigClient>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test 7: Exception during command execution — error result written, loop continues ──

    [Fact]
    public async Task ExecutorThrows_WritesErrorResult_ServiceContinues()
    {
        _leaderGate.SetupGet(g => g.IsLeader).Returns(true);
        SeedCommand("cmd-fail", LoopCommand.Resume);

        // Use a signal on the leader gate check: after two ticks we know the handler ran at
        // least once (first poll) and continued (second tick started).
        var twoTicks = new CallSignal(2);
        _leaderGate.SetupGet(g => g.IsLeader).Callback(() => twoTicks.Hit()).Returns(true);
        _executor.Setup(e => e.ExecuteResumeAsync(It.IsAny<IPipelineLoopService>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await BackgroundServiceRunner.RunUntilAsync(
            CreateHandler(_leaderGate.Object), twoTicks.Reached,
            "service must continue polling after an executor exception");

        // The error result must have been written to Redis.
        var resultKey = LoopCommandRelay.ResultKeyPrefix + "cmd-fail";
        var resultJson = await _store.GetAsync(resultKey);
        resultJson.Should().NotBeNull("error result must be written to Redis even when executor throws");
        var result = JsonSerializer.Deserialize<LoopCommandResultMessage>(resultJson!, PipelineJsonOptions.Lenient);
        result!.Success.Should().BeFalse("a thrown exception must produce a failure result");
        result.Error.Should().Contain("boom");
    }
}

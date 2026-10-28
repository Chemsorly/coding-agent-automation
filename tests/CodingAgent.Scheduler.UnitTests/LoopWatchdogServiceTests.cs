using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for <see cref="LoopWatchdogService"/>: the leader reconciles the loop with the desired state
/// in the pipeline config and pushes its status to the API (spec 051 D11). Most tests call
/// <see cref="LoopWatchdogService.TickAsync"/> directly; one runs the hosted service to check that it ticks.
/// </summary>
[Collection("Metrics")]
public sealed class LoopWatchdogServiceTests
{
    private readonly Mock<IPipelineLoopService> _loop = new();
    private readonly Mock<IPipelineApiConfigClient> _config = new();
    private readonly Mock<ISchedulerApiClient> _statusClient = new();
    private readonly Mock<ILeaderGate> _leaderGate = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly FakeTimeProvider _time = new();
    private PipelineConfiguration _desired = new() { ClosedLoopAutoStart = true };

    public LoopWatchdogServiceTests()
    {
        _logger.Setup(l => l.ForContext<LoopWatchdogService>()).Returns(_logger.Object);
        _config.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _desired);
        _leaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _loop.SetupGet(s => s.StatusMessage).Returns("");
        _loop.SetupGet(s => s.ValidationErrors).Returns([]);
        _loop.SetupGet(s => s.TemplateStatuses).Returns(new Dictionary<string, ConfigStatusSnapshot>());
    }

    /// <summary>
    /// Builds an <see cref="IServiceScopeFactory"/> backed by a minimal ServiceCollection that
    /// returns the test mocks when the service types are resolved inside a scope.
    /// Using <c>AddSingleton(instance)</c> means every scope resolution returns the same mock
    /// object, keeping <c>Mock.Verify()</c> assertions working correctly.
    /// </summary>
    /// <remarks>
    /// TODO [WARNING]: Because the mocks are registered as singletons, the same mock object is
    /// returned regardless of how many scopes are created. This means a regression where the
    /// service caches its first scope (e.g. <c>_cachedScope ??= _scopeFactory.CreateAsyncScope()</c>)
    /// instead of creating one per tick would not be detected by any <c>Verify()</c> call. To
    /// catch such a regression, this helper would need to be replaced by a custom
    /// <c>IServiceScopeFactory</c> implementation that counts scope-creation calls and can
    /// assert exactly one scope was created per tick/sweep invocation.
    /// </remarks>
    private IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_config.Object);
        services.AddSingleton(_statusClient.Object);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private LoopWatchdogService CreateWatchdog(ILeaderGate? leaderGate) =>
        new(_loop.Object, CreateScopeFactory(), leaderGate, _logger.Object, TimeSpan.FromMilliseconds(1), _time);

    private LoopWatchdogService CreateWatchdog() => CreateWatchdog(_leaderGate.Object);

    // ── Start and stop ──────────────────────────────────────────────────────

    [Fact]
    public async Task Tick_LoopShouldRunAndIsDormant_StartsTheLoop()
    {
        _loop.Setup(s => s.StartLoopAsync()).ReturnsAsync(true);

        await CreateWatchdog().TickAsync(CancellationToken.None);

        _loop.Verify(s => s.StartLoopAsync(), Times.Once());
        _loop.Verify(s => s.StopLoop(), Times.Never());
    }

    [Fact]
    public async Task Tick_LoopShouldRunAndRuns_DoesNothing()
    {
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);

        await CreateWatchdog().TickAsync(CancellationToken.None);

        _loop.Verify(s => s.StartLoopAsync(), Times.Never());
        _loop.Verify(s => s.StopLoop(), Times.Never());
    }

    [Fact]
    public async Task Tick_LoopShouldNotRunAndRuns_StopsTheLoop()
    {
        _desired = new PipelineConfiguration { ClosedLoopAutoStart = false };
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);

        await CreateWatchdog().TickAsync(CancellationToken.None);

        _loop.Verify(s => s.StopLoop(), Times.Once());
        _loop.Verify(s => s.StartLoopAsync(), Times.Never());
    }

    [Fact]
    public async Task Tick_LoopShouldNotRunAndIsDormant_DoesNothing()
    {
        _desired = new PipelineConfiguration { ClosedLoopAutoStart = false };

        await CreateWatchdog().TickAsync(CancellationToken.None);

        _loop.Verify(s => s.StartLoopAsync(), Times.Never());
        _loop.Verify(s => s.StopLoop(), Times.Never());
    }

    [Fact]
    public async Task Tick_NotLeader_NeitherReconcilesNorPushes()
    {
        _leaderGate.SetupGet(g => g.IsLeader).Returns(false);

        await CreateWatchdog().TickAsync(CancellationToken.None);

        _config.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Never());
        _loop.Verify(s => s.StartLoopAsync(), Times.Never());
        _statusClient.Verify(c => c.ReportLoopStatusAsync(It.IsAny<LoopStatusDto>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Tick_NoLeaderGate_ActsAsTheLeader()
    {
        _loop.Setup(s => s.StartLoopAsync()).ReturnsAsync(true);

        await CreateWatchdog(leaderGate: null).TickAsync(CancellationToken.None);

        _loop.Verify(s => s.StartLoopAsync(), Times.Once());
        _statusClient.Verify(c => c.ReportLoopStatusAsync(It.IsAny<LoopStatusDto>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Tick_StartRefused_RetriesEveryTickAndWarnsOnce()
    {
        _loop.Setup(s => s.StartLoopAsync()).ReturnsAsync(false);
        _loop.SetupGet(s => s.ValidationErrors).Returns(["No enabled pipeline job templates configured."]);
        var watchdog = CreateWatchdog();

        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.TickAsync(CancellationToken.None);

        _loop.Verify(s => s.StartLoopAsync(), Times.Exactly(2));
        _logger.Verify(l => l.Warning(It.Is<string>(m => m.Contains("did not start")), It.IsAny<string>()), Times.Once());
    }

    [Fact]
    public async Task Tick_ConfigUnreadable_LeavesTheLoopAndStillPushes()
    {
        _config.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        var act = () => CreateWatchdog().TickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _loop.Verify(s => s.StartLoopAsync(), Times.Never());
        _loop.Verify(s => s.StopLoop(), Times.Never());
        _statusClient.Verify(c => c.ReportLoopStatusAsync(It.IsAny<LoopStatusDto>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    // ── Resume ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tick_ResumeRequestedBeforeTheFirstTick_IsNotApplied()
    {
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);
        _desired = new PipelineConfiguration { ClosedLoopAutoStart = true, LoopResumeRequestedAt = DateTimeOffset.UtcNow };

        await CreateWatchdog().TickAsync(CancellationToken.None);

        _loop.Verify(s => s.ResumeLoop(), Times.Never());
    }

    [Fact]
    public async Task Tick_NewResumeRequest_ResumesOnce()
    {
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);
        var watchdog = CreateWatchdog();
        await watchdog.TickAsync(CancellationToken.None);

        _desired = new PipelineConfiguration { ClosedLoopAutoStart = true, LoopResumeRequestedAt = DateTimeOffset.UtcNow };
        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.TickAsync(CancellationToken.None);

        _loop.Verify(s => s.ResumeLoop(), Times.Once());
    }

    [Fact]
    public async Task Tick_ResumeRequestNewerThanTheHandledOne_ResumesAgain()
    {
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);
        var first = DateTimeOffset.UtcNow;
        _desired = new PipelineConfiguration { ClosedLoopAutoStart = true, LoopResumeRequestedAt = first };
        var watchdog = CreateWatchdog();
        await watchdog.TickAsync(CancellationToken.None);

        _desired = _desired with { LoopResumeRequestedAt = first.AddMinutes(1) };
        await watchdog.TickAsync(CancellationToken.None);
        _desired = _desired with { LoopResumeRequestedAt = first.AddMinutes(2) };
        await watchdog.TickAsync(CancellationToken.None);

        _loop.Verify(s => s.ResumeLoop(), Times.Exactly(2));
    }

    // ── Status push ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Tick_PushesTheLoopStatus()
    {
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);
        _loop.SetupGet(s => s.StatusMessage).Returns("🔄 Cycle complete.");
        _loop.SetupGet(s => s.ProcessedCount).Returns(3);

        await CreateWatchdog().TickAsync(CancellationToken.None);

        _statusClient.Verify(c => c.ReportLoopStatusAsync(
            It.Is<LoopStatusDto>(d => d.IsLoopActive && d.StatusMessage == "🔄 Cycle complete." && d.ProcessedCount == 3),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Tick_StatusUnchanged_PushesAgainOnlyAfterTheRefreshInterval()
    {
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);
        var watchdog = CreateWatchdog();

        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.TickAsync(CancellationToken.None);
        _statusClient.Verify(c => c.ReportLoopStatusAsync(It.IsAny<LoopStatusDto>(), It.IsAny<CancellationToken>()), Times.Once());

        _time.Advance(LoopWatchdogService.StatusRefreshInterval);
        await watchdog.TickAsync(CancellationToken.None);
        _statusClient.Verify(c => c.ReportLoopStatusAsync(It.IsAny<LoopStatusDto>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Tick_StatusChanged_PushesAtOnce()
    {
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);
        var watchdog = CreateWatchdog();
        await watchdog.TickAsync(CancellationToken.None);

        _loop.SetupGet(s => s.ProcessedCount).Returns(1);
        await watchdog.TickAsync(CancellationToken.None);

        _statusClient.Verify(c => c.ReportLoopStatusAsync(It.IsAny<LoopStatusDto>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Tick_PushFailed_RetriesOnTheNextTick()
    {
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);
        _statusClient.SetupSequence(c => c.ReportLoopStatusAsync(It.IsAny<LoopStatusDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"))
            .Returns(Task.CompletedTask);
        var watchdog = CreateWatchdog();

        var act = () => watchdog.TickAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
        await watchdog.TickAsync(CancellationToken.None);

        _statusClient.Verify(c => c.ReportLoopStatusAsync(It.IsAny<LoopStatusDto>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Tick_LeaderAgainAfterLosingLeadership_PushesAtOnce()
    {
        _loop.SetupGet(s => s.IsLoopActive).Returns(true);
        var watchdog = CreateWatchdog();
        await watchdog.TickAsync(CancellationToken.None);

        _leaderGate.SetupGet(g => g.IsLeader).Returns(false);
        await watchdog.TickAsync(CancellationToken.None);
        _leaderGate.SetupGet(g => g.IsLeader).Returns(true);
        await watchdog.TickAsync(CancellationToken.None);

        _statusClient.Verify(c => c.ReportLoopStatusAsync(It.IsAny<LoopStatusDto>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── Hosted service ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TicksUntilStopped()
    {
        var started = new CallSignal();
        _loop.Setup(s => s.StartLoopAsync()).Callback(() => started.Hit()).ReturnsAsync(true);

        await BackgroundServiceRunner.RunUntilAsync(CreateWatchdog(), started.Reached,
            "the first tick must run without waiting for the interval");

        _loop.Verify(s => s.StartLoopAsync(), Times.AtLeastOnce());
    }
}

using AwesomeAssertions;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Pipeline.LeaderElection;
using Microsoft.Extensions.Hosting;
using Moq;
using ILogger = Serilog.ILogger;
using CodingAgent.Web.TestUtilities;
using Serilog;
using StackExchange.Redis;

namespace CodingAgent.Orchestration.UnitTests.Registry;

/// <summary>
/// Unit tests for <see cref="CodingAgent.Orchestration.RunServiceCleanupService"/>.
/// Directly invokes the <c>internal SweepAsync</c> method — no wall-clock timers needed.
///
/// The service sweeps stale entries from <c>runs:active</c> whose <c>run:{id}</c>
/// Redis key has expired (run completed and TTL elapsed).
/// </summary>
public sealed class RunServiceCleanupServiceTests
{
    private readonly Mock<IRedisStore> _store = new();
    private readonly Mock<ILogger> _logger = new();

    private readonly FakeRedisStore _fakeStore = new();
    private readonly Mock<ILeaderElectionService> _leaderMock = new();

    private CodingAgent.Orchestration.RunServiceCleanupService CreateService(
        ILeaderElectionService? leaderElection = null)
        => new(_store.Object, _logger.Object, leaderElection);

    // ── SweepAsync: core removal behavior ───────────────────────────────

    [Fact]
    public async Task SweepAsync_ExpiredRun_RemovedFromActiveSet()
    {
        const string staleRunId = "run-stale-001";
        _store.Setup(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>())).ReturnsAsync([staleRunId]);
        _store.Setup(s => s.ExistsAsync($"run:{staleRunId}")).ReturnsAsync(false);
        _store.Setup(s => s.SetRemoveAsync("runs:active", staleRunId)).ReturnsAsync(1L);

        var svc = CreateService();

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetRemoveAsync("runs:active", staleRunId), Times.Once,
            "expired run (key gone) must be removed from runs:active");
    }

    [Fact]
    public async Task SweepAsync_ActiveRun_NotRemoved()
    {
        const string activeRunId = "run-active-001";
        _store.Setup(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>())).ReturnsAsync([activeRunId]);
        _store.Setup(s => s.ExistsAsync($"run:{activeRunId}")).ReturnsAsync(true);

        var svc = CreateService();

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "active run (key still present) must not be removed");
    }

    [Fact]
    public async Task SweepAsync_MixedRuns_OnlyExpiredRemoved()
    {
        const string staleId = "run-stale-002";
        const string activeId = "run-active-002";
        _store.Setup(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>())).ReturnsAsync([staleId, activeId]);
        _store.Setup(s => s.ExistsAsync($"run:{staleId}")).ReturnsAsync(false);
        _store.Setup(s => s.ExistsAsync($"run:{activeId}")).ReturnsAsync(true);
        _store.Setup(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(1L);

        var svc = CreateService();

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetRemoveAsync("runs:active", staleId), Times.Once);
        _store.Verify(s => s.SetRemoveAsync("runs:active", activeId), Times.Never,
            "active run must not be removed");
    }

    [Fact]
    public async Task SweepAsync_EmptySet_NoRemovalCalls()
    {
        _store.Setup(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var svc = CreateService();

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SweepAsync_AfterSweep_LiveRunAddedBetweenSweeps_NotRemovedOnSecondSweep()
    {
        // First sweep removes expired run. A run added between sweeps must survive.
        await _fakeStore.SetAddAsync("runs:active", "run-old-expired");

        var sut = CreateServiceWithFakeStore();
        await sut.SweepAsync(CancellationToken.None);

        // New live run added after first sweep
        await _fakeStore.SetAddAsync("runs:active", "run-new-live");
        await _fakeStore.HashSetAsync("run:run-new-live",
            [new HashEntry("runId", "run-new-live")]);

        await sut.SweepAsync(CancellationToken.None);

        var active = await _fakeStore.SetMembersAsync("runs:active");
        Assert.DoesNotContain("run-old-expired", active);
        Assert.Contains("run-new-live", active);
    }

    [Fact]
    public async Task SweepAsync_CancelledToken_ThrowsOperationCancelledException()
    {
        // Arrange: put something in runs:active so the foreach body is entered
        await _fakeStore.SetAddAsync("runs:active", "run-1");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateServiceWithFakeStore().SweepAsync(cts.Token));
    }

    // ── Leader gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_NotLeader_SkipsSweepEntirely()
    {
        var leaderElection = new Mock<ILeaderElectionService>();
        leaderElection.SetupGet(l => l.IsLeader).Returns(false);

        _store.Setup(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>())).ReturnsAsync(["run-001"]);

        var svc = CreateService(leaderElection.Object);

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "non-leader must skip sweep entirely");
    }

    [Fact]
    public async Task SweepAsync_IsLeader_RunsSweep()
    {
        var leaderElection = new Mock<ILeaderElectionService>();
        leaderElection.SetupGet(l => l.IsLeader).Returns(true);

        _store.Setup(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var svc = CreateService(leaderElection.Object);

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_NullLeaderElection_AlwaysSweeps()
    {
        _store.Setup(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var svc = CreateService(leaderElection: null);

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_LeadershipLostBetweenSweeps_SecondSweepSkipped()
    {
        await _fakeStore.SetAddAsync("runs:active", "run-a");

        _leaderMock.SetupSequence(l => l.IsLeader)
            .Returns(true)   // first sweep: leader
            .Returns(false); // second sweep: no longer leader

        var sut = CreateServiceWithFakeStore(_leaderMock.Object);

        // First sweep (leader): removes run-a
        await sut.SweepAsync(CancellationToken.None);
        Assert.DoesNotContain("run-a", await _fakeStore.SetMembersAsync("runs:active"));

        // Expired run added AFTER first sweep — second sweep must not touch it
        await _fakeStore.SetAddAsync("runs:active", "run-b");

        // Second sweep (not leader): skips
        await sut.SweepAsync(CancellationToken.None);
        Assert.Contains("run-b", await _fakeStore.SetMembersAsync("runs:active"));
    }

    // ── Cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_Cancellation_AbortsMidLoop()
    {
        const string id1 = "run-001";
        const string id2 = "run-002";
        _store.Setup(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>())).ReturnsAsync([id1, id2]);

        var cts = new CancellationTokenSource();
        _store.Setup(s => s.ExistsAsync($"run:{id1}"))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(false);
        _store.Setup(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(0L);

        var svc = CreateService();

        var act = () => svc.SweepAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        _store.Verify(s => s.ExistsAsync($"run:{id2}"), Times.Never);
        // id1 removal was already in flight when cancellation was signalled; it completes
        _store.Verify(s => s.SetRemoveAsync("runs:active", id1), Times.Once,
            "the member whose check triggered cancellation is already being removed when OCE fires");
    }

    // ── ExecuteAsync: timer loop ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OnTick_CallsSweepAsync()
    {
        _store.Setup(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var svc = new CodingAgent.Orchestration.RunServiceCleanupService(
            _store.Object, _logger.Object, leaderElection: null,
            sweepInterval: TimeSpan.FromMilliseconds(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ((IHostedService)svc).StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try { _store.Verify(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>()), Times.AtLeastOnce()); break; }
            catch (MockException) { await Task.Delay(20); }
        }

        cts.Cancel();
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync("runs:active", It.IsAny<CancellationToken>()), Times.AtLeastOnce(),
            "ExecuteAsync timer loop must call SweepAsync on each tick");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private RunServiceCleanupService CreateServiceWithFakeStore(ILeaderElectionService? leaderElection = null)
        => new(_fakeStore, Log.Logger, leaderElection);
}

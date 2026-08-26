using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry;

/// <summary>
/// Unit tests for <see cref="CodingAgentWebUI.Orchestration.RunServiceCleanupService"/>.
/// Directly invokes the <c>internal SweepAsync</c> method — no wall-clock timers needed.
///
/// The service sweeps stale entries from <c>runs:active</c> whose <c>run:{id}</c>
/// Redis key has expired (run completed and TTL elapsed).
/// </summary>
public sealed class RunServiceCleanupServiceTests
{
    private readonly Mock<IRedisStore> _store = new();
    private readonly Mock<ILogger> _logger = new();

    private CodingAgentWebUI.Orchestration.RunServiceCleanupService CreateService(
        ILeaderElectionService? leaderElection = null)
        => new(_store.Object, _logger.Object, leaderElection);

    // ── SweepAsync: core removal behavior ───────────────────────────────

    [Fact]
    public async Task SweepAsync_ExpiredRun_RemovedFromActiveSet()
    {
        const string staleRunId = "run-stale-001";
        _store.Setup(s => s.SetMembersAsync("runs:active")).ReturnsAsync([staleRunId]);
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
        _store.Setup(s => s.SetMembersAsync("runs:active")).ReturnsAsync([activeRunId]);
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
        _store.Setup(s => s.SetMembersAsync("runs:active")).ReturnsAsync([staleId, activeId]);
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
        _store.Setup(s => s.SetMembersAsync("runs:active")).ReturnsAsync([]);

        var svc = CreateService();

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ── Leader gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_NotLeader_SkipsSweepEntirely()
    {
        var leaderElection = new Mock<ILeaderElectionService>();
        leaderElection.SetupGet(l => l.IsLeader).Returns(false);

        _store.Setup(s => s.SetMembersAsync("runs:active")).ReturnsAsync(["run-001"]);

        var svc = CreateService(leaderElection.Object);

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync(It.IsAny<string>()), Times.Never,
            "non-leader must skip sweep entirely");
    }

    [Fact]
    public async Task SweepAsync_IsLeader_RunsSweep()
    {
        var leaderElection = new Mock<ILeaderElectionService>();
        leaderElection.SetupGet(l => l.IsLeader).Returns(true);

        _store.Setup(s => s.SetMembersAsync("runs:active")).ReturnsAsync([]);

        var svc = CreateService(leaderElection.Object);

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync("runs:active"), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_NullLeaderElection_AlwaysSweeps()
    {
        _store.Setup(s => s.SetMembersAsync("runs:active")).ReturnsAsync([]);

        var svc = CreateService(leaderElection: null);

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync("runs:active"), Times.Once);
    }

    // ── Cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_Cancellation_AbortsMidLoop()
    {
        const string id1 = "run-001";
        const string id2 = "run-002";
        _store.Setup(s => s.SetMembersAsync("runs:active")).ReturnsAsync([id1, id2]);

        var cts = new CancellationTokenSource();
        _store.Setup(s => s.ExistsAsync($"run:{id1}"))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(false);
        _store.Setup(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(0L);

        var svc = CreateService();

        var act = () => svc.SweepAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        _store.Verify(s => s.ExistsAsync($"run:{id2}"), Times.Never);
    }
}

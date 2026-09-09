using AwesomeAssertions;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.LeaderElection;
using Microsoft.Extensions.Hosting;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.UnitTests.Registry;

/// <summary>
/// Unit tests for <see cref="AgentRegistryCleanupService"/>.
/// Directly invokes the <c>internal SweepAsync</c> method — no wall-clock timers needed.
///
/// The service sweeps stale entries from <c>agents:all</c> and <c>agents:idle</c>
/// whose <c>agent:{id}</c> Redis key has expired (TTL elapsed without heartbeat).
/// </summary>
public sealed class AgentRegistryCleanupServiceTests
{
    private readonly Mock<IRedisStore> _store = new();
    private readonly Mock<ILogger> _logger = new();

    private AgentRegistryCleanupService CreateService(ILeaderElectionService? leaderElection = null)
        => new(_store.Object, _logger.Object, leaderElection);

    // ── SweepAsync: core removal behavior ───────────────────────────────

    [Fact]
    public async Task SweepAsync_StaleAgent_RemovedFromBothSets()
    {
        // Arrange: one member in agents:all, its agent:{id} key does NOT exist
        const string staleId = "agent-stale-001";
        _store.Setup(s => s.SetMembersAsync("agents:all")).ReturnsAsync([staleId]);
        _store.Setup(s => s.ExistsAsync($"agent:{staleId}")).ReturnsAsync(false);
        _store.Setup(s => s.SetRemoveAsync("agents:all", staleId)).ReturnsAsync(1L);
        _store.Setup(s => s.SetRemoveAsync("agents:idle", staleId)).ReturnsAsync(1L);

        var svc = CreateService();

        // Act
        await svc.SweepAsync(CancellationToken.None);

        // Assert: both sets cleared
        _store.Verify(s => s.SetRemoveAsync("agents:all", staleId), Times.Once,
            "stale agent must be removed from agents:all");
        _store.Verify(s => s.SetRemoveAsync("agents:idle", staleId), Times.Once,
            "stale agent must be removed from agents:idle");
    }

    [Fact]
    public async Task SweepAsync_ActiveAgent_NotRemoved()
    {
        // Arrange: member exists AND its agent:{id} key also exists
        const string activeId = "agent-active-001";
        _store.Setup(s => s.SetMembersAsync("agents:all")).ReturnsAsync([activeId]);
        _store.Setup(s => s.ExistsAsync($"agent:{activeId}")).ReturnsAsync(true);

        var svc = CreateService();

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "active agent (key exists) must not be removed");
    }

    [Fact]
    public async Task SweepAsync_MixedMembers_OnlyStaleRemoved()
    {
        // Arrange: two members — one stale, one active
        const string staleId = "agent-stale-002";
        const string activeId = "agent-active-002";
        _store.Setup(s => s.SetMembersAsync("agents:all")).ReturnsAsync([staleId, activeId]);
        _store.Setup(s => s.ExistsAsync($"agent:{staleId}")).ReturnsAsync(false);
        _store.Setup(s => s.ExistsAsync($"agent:{activeId}")).ReturnsAsync(true);
        _store.Setup(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(1L);

        var svc = CreateService();

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetRemoveAsync("agents:all", staleId), Times.Once);
        _store.Verify(s => s.SetRemoveAsync("agents:idle", staleId), Times.Once);
        _store.Verify(s => s.SetRemoveAsync(It.IsAny<string>(), activeId), Times.Never,
            "active agent must not be touched");
    }

    [Fact]
    public async Task SweepAsync_EmptySet_NoRemovalCalls()
    {
        _store.Setup(s => s.SetMembersAsync("agents:all")).ReturnsAsync([]);

        var svc = CreateService();

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ── Leader gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_NotLeader_SkipsEntireSweep()
    {
        // Arrange: leader election says not leader
        var leaderElection = new Mock<ILeaderElectionService>();
        leaderElection.SetupGet(l => l.IsLeader).Returns(false);

        _store.Setup(s => s.SetMembersAsync("agents:all")).ReturnsAsync(["agent-001"]);

        var svc = CreateService(leaderElection.Object);

        await svc.SweepAsync(CancellationToken.None);

        // SMEMBERS must not even be called (leader gate fires before Redis reads)
        _store.Verify(s => s.SetMembersAsync(It.IsAny<string>()), Times.Never,
            "non-leader must skip sweep entirely — no Redis reads");
    }

    [Fact]
    public async Task SweepAsync_IsLeader_RunsSweep()
    {
        // Arrange: leader election says IS leader
        var leaderElection = new Mock<ILeaderElectionService>();
        leaderElection.SetupGet(l => l.IsLeader).Returns(true);

        _store.Setup(s => s.SetMembersAsync("agents:all")).ReturnsAsync([]);

        var svc = CreateService(leaderElection.Object);

        await svc.SweepAsync(CancellationToken.None);

        // SMEMBERS was called — sweep ran
        _store.Verify(s => s.SetMembersAsync("agents:all"), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_NullLeaderElection_AlwaysSweeps()
    {
        // Arrange: null leaderElection = single-replica / local dev mode — always sweeps
        _store.Setup(s => s.SetMembersAsync("agents:all")).ReturnsAsync([]);

        var svc = CreateService(leaderElection: null);

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync("agents:all"), Times.Once,
            "null leader election service means always sweep");
    }

    // ── Cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_CancellationRequested_ThrowsBeforeProcessingAllMembers()
    {
        // Arrange: two members; cancellation fired after first
        const string id1 = "agent-001";
        const string id2 = "agent-002";
        _store.Setup(s => s.SetMembersAsync("agents:all")).ReturnsAsync([id1, id2]);

        // First member check triggers cancellation
        var cts = new CancellationTokenSource();
        _store.Setup(s => s.ExistsAsync($"agent:{id1}"))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(false);
        _store.Setup(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(0L);

        var svc = CreateService();

        var act = () => svc.SweepAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // id2 was never reached — cancellation fired before next iteration
        _store.Verify(s => s.ExistsAsync($"agent:{id2}"), Times.Never,
            "cancellation must abort the loop before processing remaining members");
        // id1 removal was already in flight when cancellation was signalled; it completes
        _store.Verify(s => s.SetRemoveAsync("agents:all", id1), Times.Once,
            "the member whose check triggered cancellation is already being removed when OCE fires");
    }

    // ── ExecuteAsync: timer loop ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OnTick_CallsSweepAsync()
    {
        // Arrange: configure store so a sweep can complete
        _store.Setup(s => s.SetMembersAsync("agents:all")).ReturnsAsync([]);

        // Use a very short interval so the tick fires immediately in the test
        var svc = new AgentRegistryCleanupService(_store.Object, _logger.Object,
            leaderElection: null, sweepInterval: TimeSpan.FromMilliseconds(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act: start the background service and wait for at least one sweep
        await ((IHostedService)svc).StartAsync(cts.Token);

        // Poll until SetMembersAsync is called (= at least one tick executed SweepAsync)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try { _store.Verify(s => s.SetMembersAsync("agents:all"), Times.AtLeastOnce()); break; }
            catch (MockException) { await Task.Delay(20); }
        }

        cts.Cancel();
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync("agents:all"), Times.AtLeastOnce(),
            "ExecuteAsync timer loop must call SweepAsync on each tick");
    }

    [Fact]
    public async Task ExecuteAsync_SweepThrowsNonCancellation_ExceptionSwallowedAndLoopContinues()
    {
        // Arrange: first call throws, second returns empty — loop must continue
        var callCount = 0;
        _store.Setup(s => s.SetMembersAsync("agents:all"))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("transient Redis error");
                return Task.FromResult(Array.Empty<string>());
            });

        var svc = new AgentRegistryCleanupService(_store.Object, _logger.Object,
            leaderElection: null, sweepInterval: TimeSpan.FromMilliseconds(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ((IHostedService)svc).StartAsync(cts.Token);

        // Wait until the second tick completes successfully (callCount >= 2)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && callCount < 2)
            await Task.Delay(20);

        cts.Cancel();
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        callCount.Should().BeGreaterThanOrEqualTo(2,
            "a non-cancellation exception during sweep must be swallowed and the loop must continue");
        // Verify the warning was logged — ServiceName is passed as a string property value
        _logger.Verify(
            l => l.Warning(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("sweep error")),
                It.IsAny<string>()),
            Times.AtLeastOnce(),
            "the swallowed exception must be logged as a warning");
    }
}

using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry;

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

        // id2 was never reached
        _store.Verify(s => s.ExistsAsync($"agent:{id2}"), Times.Never,
            "cancellation must abort the loop before processing remaining members");
    }
}

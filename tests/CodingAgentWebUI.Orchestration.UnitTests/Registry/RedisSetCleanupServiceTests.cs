using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry;

/// <summary>
/// Unit tests for <see cref="RedisSetCleanupService"/> base class logic, exercised via a minimal
/// inline test double. Directly invokes the <c>internal SweepAsync</c> method — no wall-clock
/// timers needed.
///
/// Regression coverage for the base-class iteration loop and leader gate is additionally
/// provided by <see cref="AgentRegistryCleanupServiceTests"/> and
/// <see cref="RunServiceCleanupServiceTests"/> which exercise the same code paths through the
/// concrete implementations.
/// </summary>
public sealed class RedisSetCleanupServiceTests
{
    private readonly Mock<IRedisStore> _store = new();
    private readonly Mock<ILogger> _logger = new();

    private TestableCleanupService CreateService(ILeaderElectionService? leaderElection = null)
        => new(_store.Object, _logger.Object, leaderElection);

    // ── Leader gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_NotLeader_SkipsSweepEntirely()
    {
        // Arrange: leader election says not leader
        var leaderElection = new Mock<ILeaderElectionService>();
        leaderElection.SetupGet(l => l.IsLeader).Returns(false);

        _store.Setup(s => s.SetMembersAsync("test:members")).ReturnsAsync(["item-001"]);

        var svc = CreateService(leaderElection.Object);

        await svc.SweepAsync(CancellationToken.None);

        // SMEMBERS must not even be called — leader gate fires before any Redis reads
        _store.Verify(s => s.SetMembersAsync(It.IsAny<string>()), Times.Never,
            "non-leader must skip sweep entirely — no Redis reads");
    }

    [Fact]
    public async Task SweepAsync_IsLeader_RunsSweep()
    {
        // Arrange: leader election says IS leader
        var leaderElection = new Mock<ILeaderElectionService>();
        leaderElection.SetupGet(l => l.IsLeader).Returns(true);

        _store.Setup(s => s.SetMembersAsync("test:members")).ReturnsAsync([]);

        var svc = CreateService(leaderElection.Object);

        await svc.SweepAsync(CancellationToken.None);

        // TODO: This test only verifies that SetMembersAsync was called (sweep ran), but does not
        // exercise the stale-member removal path. An additional test with leader-present + stale member
        // would provide stronger base-class coverage and distinguish "sweep ran" from "removal works".
        // (Review: TestQualityReviewer)
        _store.Verify(s => s.SetMembersAsync("test:members"), Times.Once,
            "leader must proceed to read the membership set");
    }

    [Fact]
    public async Task SweepAsync_NullLeaderElection_AlwaysSweeps()
    {
        // null = no election service (local dev / single-replica) → sweep unconditionally
        _store.Setup(s => s.SetMembersAsync("test:members")).ReturnsAsync([]);

        var svc = CreateService(leaderElection: null);

        await svc.SweepAsync(CancellationToken.None);

        _store.Verify(s => s.SetMembersAsync("test:members"), Times.Once,
            "null leader election service means always sweep");
    }

    // ── Cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_CancellationRequested_ThrowsBeforeProcessingAllMembers()
    {
        // Arrange: two members; cancellation fired during the first member's processing
        const string id1 = "item-001";
        const string id2 = "item-002";
        _store.Setup(s => s.SetMembersAsync("test:members")).ReturnsAsync([id1, id2]);

        var cts = new CancellationTokenSource();
        _store.Setup(s => s.ExistsAsync($"test:{id1}"))
            .Callback(() => cts.Cancel())
            .ReturnsAsync(false);
        _store.Setup(s => s.SetRemoveAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(0L);

        var svc = CreateService();

        var act = () => svc.SweepAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // id2 was never reached
        // TODO: This test does not verify whether RemoveStaleAsync was called for id1. The sequence is:
        // ExistsAsync(id1) → cancel() → RemoveStaleAsync(id1) → ThrowIfCancellationRequested (for id2).
        // So id1 IS partially processed (removed) before the throw. The assertion only covers that id2 is
        // not reached, leaving the partial-removal-before-cancel path unverified. Consider adding a
        // dedicated assertion on SetRemoveAsync call count if this ordering matters. Same pattern exists
        // in AgentRegistryCleanupServiceTests and RunServiceCleanupServiceTests. (Review: TestQualityReviewer, DotNetSpecialist)
        _store.Verify(s => s.ExistsAsync($"test:{id2}"), Times.Never,
            "cancellation must abort the loop before processing remaining members");
    }

    // ── Inline test double ───────────────────────────────────────────────

    /// <summary>
    /// Minimal concrete implementation of <see cref="RedisSetCleanupService"/> for base-class
    /// testing. Uses a single membership set ("test:members") and hash prefix ("test:").
    /// </summary>
    private sealed class TestableCleanupService : RedisSetCleanupService
    {
        public TestableCleanupService(
            IRedisStore store,
            ILogger logger,
            ILeaderElectionService? leaderElection = null)
            : base(store, logger, leaderElection)
        {
        }

        protected override TimeSpan SweepInterval => TimeSpan.FromMinutes(1);
        protected override string ServiceName => "TestableCleanupService";
        protected override string MembershipSetKey => "test:members";
        protected override string HashKeyPrefix => "test:";

        protected override async Task RemoveStaleAsync(string id, CancellationToken ct)
        {
            await _store.SetRemoveAsync("test:members", id);
        }
    }
}

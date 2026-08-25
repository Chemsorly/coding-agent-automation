using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.TestUtilities;
using Moq;
using Serilog;
using StackExchange.Redis;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AgentRegistryCleanupService"/>.
/// Each test gets a fresh <see cref="FakeRedisStore"/> via xUnit's per-test class instantiation —
/// no shared state, no <c>Reset()</c> needed.
/// </summary>
public sealed class AgentRegistryCleanupServiceTests
{
    private readonly FakeRedisStore _store = new();
    private readonly Mock<ILeaderElectionService> _leaderMock = new();

    private AgentRegistryCleanupService MakeService(ILeaderElectionService? leaderElection = null)
        => new(_store, Log.Logger, leaderElection);

    // ── Sweep logic ────────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_ExpiredHash_RemovedFromBothSets()
    {
        // Arrange: agent in sets but hash gone (TTL expired)
        await _store.SetAddAsync("agents:all", "agent-1");
        await _store.SetAddAsync("agents:idle", "agent-1");
        // no HashSetAsync → agent:agent-1 does not exist

        await MakeService().SweepAsync(CancellationToken.None);

        Assert.DoesNotContain("agent-1", await _store.SetMembersAsync("agents:all"));
        Assert.DoesNotContain("agent-1", await _store.SetMembersAsync("agents:idle"));
    }

    [Fact]
    public async Task SweepAsync_LiveHash_NotRemovedFromSets()
    {
        // Arrange: agent in sets AND hash present (healthy pod)
        await _store.SetAddAsync("agents:all", "agent-2");
        await _store.SetAddAsync("agents:idle", "agent-2");
        await _store.HashSetAsync("agent:agent-2",
        [
            new HashEntry("agentId", "agent-2"),
            new HashEntry("status", "Idle")
        ]);

        await MakeService().SweepAsync(CancellationToken.None);

        Assert.Contains("agent-2", await _store.SetMembersAsync("agents:all"));
        Assert.Contains("agent-2", await _store.SetMembersAsync("agents:idle"));
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_ExpiredHash_RemovedFromAllSet_IdleSetUnaffected()
    {
        // A Busy agent is in agents:all but NOT in agents:idle.
        // When its hash expires the sweep must remove it from agents:all and issue a
        // no-op SREM on agents:idle (which is safe — SREM on a non-member returns 0).
        await _store.SetAddAsync("agents:all", "agent-busy");
        // deliberately NOT added to agents:idle
        // no hash

        await MakeService().SweepAsync(CancellationToken.None);

        Assert.DoesNotContain("agent-busy", await _store.SetMembersAsync("agents:all"));
        // agents:idle was never touched — SREM on a non-member is a no-op
        Assert.DoesNotContain("agent-busy", await _store.SetMembersAsync("agents:idle"));
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_LiveHash_NotRemovedFromAll()
    {
        // A Busy agent with a live hash must not be removed even though it is not in agents:idle.
        // The sweep iterates agents:all, not agents:idle, so the Busy agent is visited.
        await _store.SetAddAsync("agents:all", "agent-busy-alive");
        await _store.HashSetAsync("agent:agent-busy-alive",
        [
            new HashEntry("agentId", "agent-busy-alive"),
            new HashEntry("status", "Busy")
        ]);
        // NOT in agents:idle

        await MakeService().SweepAsync(CancellationToken.None);

        Assert.Contains("agent-busy-alive", await _store.SetMembersAsync("agents:all"));
    }

    [Fact]
    public async Task SweepAsync_MixedAgents_OnlyExpiredRemoved()
    {
        await _store.SetAddAsync("agents:all",  "agent-alive");
        await _store.SetAddAsync("agents:idle", "agent-alive");
        await _store.HashSetAsync("agent:agent-alive",
            [new HashEntry("agentId", "agent-alive")]);

        await _store.SetAddAsync("agents:all",  "agent-expired");
        await _store.SetAddAsync("agents:idle", "agent-expired");
        // no hash for agent-expired

        await MakeService().SweepAsync(CancellationToken.None);

        var all  = await _store.SetMembersAsync("agents:all");
        var idle = await _store.SetMembersAsync("agents:idle");
        Assert.Contains("agent-alive", all);
        Assert.Contains("agent-alive", idle);
        Assert.DoesNotContain("agent-expired", all);
        Assert.DoesNotContain("agent-expired", idle);
    }

    [Fact]
    public async Task SweepAsync_EmptySets_NoOp()
    {
        // No agents registered — should complete without exception
        var ex = await Record.ExceptionAsync(() => MakeService().SweepAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    [Fact]
    public async Task SweepAsync_AfterSweep_LiveAgentAddedBetweenSweeps_NotRemovedOnSecondSweep()
    {
        // First sweep removes expired agent. A live agent registered between sweeps must survive.
        await _store.SetAddAsync("agents:all",  "agent-old-expired");
        await _store.SetAddAsync("agents:idle", "agent-old-expired");

        var sut = MakeService();
        await sut.SweepAsync(CancellationToken.None);

        // New agent registered after first sweep, with a live hash
        await _store.SetAddAsync("agents:all",  "agent-new-live");
        await _store.SetAddAsync("agents:idle", "agent-new-live");
        await _store.HashSetAsync("agent:agent-new-live",
            [new HashEntry("agentId", "agent-new-live")]);

        await sut.SweepAsync(CancellationToken.None);

        // Old expired agent gone, new live agent untouched
        var all = await _store.SetMembersAsync("agents:all");
        Assert.DoesNotContain("agent-old-expired", all);
        Assert.Contains("agent-new-live", all);
    }

    [Fact]
    public async Task SweepAsync_CancelledToken_ThrowsOperationCancelledException()
    {
        // Arrange: put something in agents:all so the foreach body is entered
        await _store.SetAddAsync("agents:all", "agent-1");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => MakeService().SweepAsync(cts.Token));
    }

    // ── Leader-election gating ─────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_WhenLeader_RunsSweep()
    {
        await _store.SetAddAsync("agents:all",  "agent-stale");
        await _store.SetAddAsync("agents:idle", "agent-stale");

        _leaderMock.Setup(l => l.IsLeader).Returns(true);
        await MakeService(_leaderMock.Object).SweepAsync(CancellationToken.None);

        Assert.DoesNotContain("agent-stale", await _store.SetMembersAsync("agents:all"));
    }

    [Fact]
    public async Task SweepAsync_WhenNotLeader_SkipsSweep_BothSetsUntouched()
    {
        await _store.SetAddAsync("agents:all",  "agent-stale");
        await _store.SetAddAsync("agents:idle", "agent-stale");

        _leaderMock.Setup(l => l.IsLeader).Returns(false);
        await MakeService(_leaderMock.Object).SweepAsync(CancellationToken.None);

        // Sweep skipped — both sets must still contain the stale member
        Assert.Contains("agent-stale", await _store.SetMembersAsync("agents:all"));
        Assert.Contains("agent-stale", await _store.SetMembersAsync("agents:idle"));
    }

    [Fact]
    public async Task SweepAsync_WhenNoLeaderService_AlwaysSweeps()
    {
        // null service = local dev / single-replica → sweep unconditionally
        await _store.SetAddAsync("agents:all",  "agent-stale");
        await _store.SetAddAsync("agents:idle", "agent-stale");

        await MakeService(leaderElection: null).SweepAsync(CancellationToken.None);

        Assert.DoesNotContain("agent-stale", await _store.SetMembersAsync("agents:all"));
    }

    [Fact]
    public async Task SweepAsync_LeadershipLostBetweenSweeps_SecondSweepSkipped()
    {
        await _store.SetAddAsync("agents:all",  "agent-a");
        await _store.SetAddAsync("agents:idle", "agent-a");

        _leaderMock.SetupSequence(l => l.IsLeader)
            .Returns(true)   // first sweep: leader
            .Returns(false); // second sweep: no longer leader

        var sut = MakeService(_leaderMock.Object);

        // First sweep (leader): removes agent-a
        await sut.SweepAsync(CancellationToken.None);
        Assert.DoesNotContain("agent-a", await _store.SetMembersAsync("agents:all"));

        // Stale agents added AFTER first sweep — second sweep must not touch them
        await _store.SetAddAsync("agents:all",  "agent-b");
        await _store.SetAddAsync("agents:idle", "agent-b");
        await _store.SetAddAsync("agents:all",  "agent-c");

        // Second sweep (not leader): skips entirely
        await sut.SweepAsync(CancellationToken.None);
        var afterSecond = await _store.SetMembersAsync("agents:all");
        Assert.Contains("agent-b", afterSecond);
        Assert.Contains("agent-c", afterSecond);
    }
}
